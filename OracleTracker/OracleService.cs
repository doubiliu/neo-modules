using Neo;
using Neo.Cryptography.ECC;
using Neo.IO;
using Neo.Ledger;
using Neo.Network.P2P.Payloads;
using Neo.Oracle.Protocols.Https;
using Neo.Persistence;
using Neo.Plugins;
using Neo.SmartContract;
using Neo.SmartContract.Native;
using Neo.SmartContract.Native.Tokens;
using Neo.VM;
using Neo.Wallets;
using OracleTracker.Protocols;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace OracleTracker
{
    public class OracleService
    {
        private (Contract Contract, KeyPair Key)[] _accounts;
        private readonly OracleTracker _trackern;

        private long _isStarted = 0;
        private CancellationTokenSource _cancel;
        private Task[] _oracleTasks;
        private static System.Timers.Timer _gcTimer;
        private static readonly TimeSpan TimeoutInterval = TimeSpan.FromMinutes(5);

        private SnapshotView _lastSnapshot;
        private readonly Func<SnapshotView> _snapshotFactory;
        private Func<OracleRequest, OracleResponseAttribute> Protocols { get; }
        private static IOracleProtocol HTTPSProtocol { get; } = new OracleHttpProtocol();

        private readonly BlockingCollection<OracleTask> _processingQueue;
        private readonly ConcurrentDictionary<UInt256, OracleTask> _pendingQueue;

        public class OracleTask
        {
            public readonly DateTime timeStamp;
            public UInt256 requestTxHash;
            public OracleRequest request;
            public ResponseCollection responseItems;
            private Object locker = new Object();

            public OracleTask(UInt256 requestTxHash, OracleRequest request = null, StoreView snapshot = null)
            {
                this.requestTxHash = requestTxHash;
                this.request = request;
                this.responseItems = new ResponseCollection();
                this.timeStamp = TimeProvider.Current.UtcNow;
            }

            public bool UpdateTaskState(OracleRequest request = null)
            {
                if (request != null) this.request = request;
                return true;
            }

            public bool AddResponseItem(Contract contract, ECPoint[] publicKeys, ResponseItem item, OracleTracker tracker, ConcurrentDictionary<UInt256, OracleTask> _pendingQueue)
            {
                lock (locker)
                {
                    responseItems.RemoveOutOfDateResponseItem(publicKeys);
                    responseItems.Add(item);

                    var mine_responseItem = responseItems.Where(p => p.IsMine).FirstOrDefault();
                    if (mine_responseItem is null) return true;
                    ContractParametersContext responseTransactionContext = new ContractParametersContext(mine_responseItem.Tx);

                    foreach (var responseItem in responseItems)
                    {
                        responseTransactionContext.AddSignature(contract, responseItem.OraclePub, responseItem.Signature);
                    }
                    if (responseTransactionContext.Completed)
                    {
                        mine_responseItem.Tx.Witnesses = responseTransactionContext.GetWitnesses();
                        Log($"Send response tx: responseTx={mine_responseItem.Tx.Hash}");
                        _pendingQueue.TryRemove(mine_responseItem.RequestTxHash, out _);
                        tracker.SendMessage(new Blockchain.RelayResult { Inventory = mine_responseItem.Tx });
                    }
                }
                return true;
            }
        }

        public class ResponseItem
        {
            public readonly Transaction Tx;
            public readonly OraclePayload Payload;
            public readonly DateTime Timestamp;
            public ECPoint OraclePub => Payload.OraclePub;
            public byte[] Signature => Payload.ResponseTxSignature;
            public UInt256 RequestTxHash => Payload.RequestTxHash;
            public bool IsMine => Tx != null;

            public ResponseItem(OraclePayload payload, Transaction responseTx = null)
            {
                this.Tx = responseTx;
                this.Payload = payload;
                this.Timestamp = TimeProvider.Current.UtcNow;
            }

            public bool Verify(StoreView snapshot)
            {
                return Payload.Verify(snapshot);
            }
        }

        public class ResponseCollection : IEnumerable<ResponseItem>
        {
            private readonly Dictionary<ECPoint, ResponseItem> _items = new Dictionary<ECPoint, ResponseItem>();
            public int Count => _items.Count;

            public bool Add(ResponseItem item)
            {
                if (_items.TryGetValue(item.OraclePub, out var prev))
                {
                    if (prev.Timestamp > item.Timestamp) return false;
                    _items[item.OraclePub] = item;
                    return true;
                }
                if (_items.TryAdd(item.OraclePub, item)) return true;
                return false;
            }

            public bool RemoveOutOfDateResponseItem(ECPoint[] publicKeys)
            {
                List<ECPoint> temp = new List<ECPoint>();
                foreach (var item in _items)
                {
                    if (!publicKeys.Contains(item.Key))
                    {
                        temp.Add(item.Key);
                    }
                }
                foreach (var e in temp)
                {
                    _items.Remove(e, out _);
                }
                return true;
            }

            public IEnumerator<ResponseItem> GetEnumerator()
            {
                return (IEnumerator<ResponseItem>)_items.Select(u => u.Value).ToArray().GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return this.GetEnumerator();
            }
        }

        public OracleService(OracleTracker tracker, int capacity)
        {
            Protocols = Process;
            _accounts = new (Contract Contract, KeyPair Key)[0];
            _snapshotFactory = new Func<SnapshotView>(() => _lastSnapshot ?? Blockchain.Singleton.GetSnapshot());
            _trackern = tracker;
            _processingQueue = new BlockingCollection<OracleTask>(new ConcurrentQueue<OracleTask>(), capacity);
            _pendingQueue = new ConcurrentDictionary<UInt256, OracleTask>();
        }

        public bool Start(Wallet wallet, byte numberOfTasks = 4)
        {
            if (Interlocked.Exchange(ref _isStarted, 1) != 0) return false;
            if (numberOfTasks == 0) throw new ArgumentException("The task count must be greater than 0");
            using SnapshotView snapshot = _snapshotFactory();
            var oracles = NativeContract.Oracle.GetOracleValidators(snapshot)
                .Select(u => Contract.CreateSignatureRedeemScript(u).ToScriptHash());

            _accounts = wallet?.GetAccounts()
                .Where(u => u.HasKey && !u.Lock && oracles.Contains(u.ScriptHash))
                .Select(u => (u.Contract, u.GetKey()))
                .ToArray();
            if (_accounts.Length == 0)
            {
                throw new ArgumentException("The wallet doesn't have any oracle accounts");
            }
            // Create tasks
            Log($"OnStart: tasks={numberOfTasks}");

            _cancel = new CancellationTokenSource();
            _oracleTasks = new Task[numberOfTasks];

            for (int x = 0; x < _oracleTasks.Length; x++)
            {
                _oracleTasks[x] = new Task(() =>
                {
                    foreach (var tx in _processingQueue.GetConsumingEnumerable(_cancel.Token))
                    {
                        ProcessRequest(tx);
                    }
                },
                _cancel.Token);
            }

            _gcTimer = new System.Timers.Timer();
            _gcTimer.Elapsed += new ElapsedEventHandler(CleanOutOfDateOracleTask);
            _gcTimer.Interval = TimeoutInterval.TotalMilliseconds;
            _gcTimer.AutoReset = true;
            _gcTimer.Enabled = true;
            // Start tasks
            foreach (var task in _oracleTasks) task.Start();
            return true;
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _isStarted, 0) != 1) return;
            Log("OnStop");
            _cancel.Cancel();
            for (int x = 0; x < _oracleTasks.Length; x++)
            {
                try { _oracleTasks[x].Wait(); } catch { }
                try { _oracleTasks[x].Dispose(); } catch { }
            }
            _gcTimer.Stop();
            _cancel.Dispose();
            _cancel = null;
            _oracleTasks = null;
            // Clean queue
            while (true)
            {
                if (!_processingQueue.TryTake(out _)) break;
            }
            _pendingQueue.Clear();
            _accounts = new (Contract Contract, KeyPair Key)[0];
        }

        public void CleanOutOfDateOracleTask(object source, ElapsedEventArgs e)
        {
            List<UInt256> outOfDateTaskHashs = new List<UInt256>();
            foreach (var outOfDateTask in _pendingQueue)
            {
                DateTime now = TimeProvider.Current.UtcNow;
                if (now - outOfDateTask.Value.timeStamp <= TimeoutInterval) break;
                outOfDateTaskHashs.Add(outOfDateTask.Key);
            }
            foreach (UInt256 txHash in outOfDateTaskHashs)
            {
                _pendingQueue.TryRemove(txHash, out _);
            }
        }

        public void SubmitRequest(SnapshotView snapshot, Transaction tx)
        {
            if (_isStarted == 1)
            {
                _lastSnapshot = snapshot;
                _processingQueue.Add(new OracleTask(tx.Hash));
            }
        }

        public void ProcessRequest(OracleTask task)
        {
            Log($"Process oracle request: requestTx={task.requestTxHash}");
            SnapshotView snapshot = _snapshotFactory();
            OracleRequest request = NativeContract.Oracle.GetRequest(snapshot, task.requestTxHash);
            if (request is null || request.Status != RequestStatusType.Request) return;
            ECPoint[] oraclePublicKeys = NativeContract.Oracle.GetOracleValidators(snapshot);
            var contract = Contract.CreateMultiSigContract(oraclePublicKeys.Length - (oraclePublicKeys.Length - 1) / 3, oraclePublicKeys);

            OracleResponseAttribute response = Protocols(request);
            var responseTx = CreateResponseTransaction(snapshot.Clone(), response, contract);
            if (responseTx is null) return;
            Log($"Generated response tx: requestTx={task.requestTxHash} responseTx={responseTx.Hash}");

            foreach (var account in _accounts)
            {
                var response_payload = new OraclePayload()
                {
                    OraclePub = account.Key.PublicKey,
                    RequestTxHash = task.requestTxHash,
                    ResponseTxSignature = responseTx.Sign(account.Key),
                };

                var signatureMsg = response_payload.Sign(account.Key);
                var signPayload = new ContractParametersContext(response_payload);

                if (signPayload.AddSignature(account.Contract, response_payload.OraclePub, signatureMsg) && signPayload.Completed)
                {
                    response_payload.Witnesses = signPayload.GetWitnesses();
                    task.UpdateTaskState(request);
                    task.AddResponseItem(contract, oraclePublicKeys, new ResponseItem(response_payload, responseTx), _trackern, _pendingQueue);
                    if (!_pendingQueue.TryAdd(task.requestTxHash, task))
                    {
                        _pendingQueue.TryGetValue(task.requestTxHash, out OracleTask new_oracleTask);
                        if (new_oracleTask != null)
                        {
                            new_oracleTask.UpdateTaskState(task.request);
                            new_oracleTask.AddResponseItem(contract, oraclePublicKeys, new ResponseItem(response_payload, responseTx), _trackern, _pendingQueue);
                        }
                    }

                    Log($"Send oracle signature: oracle={response_payload.OraclePub} requestTx={task.requestTxHash} signaturePayload={response_payload.Hash}");
                    _trackern.SendMessage(new Blockchain.RelayResult { Inventory = response_payload, Result = VerifyResult.Succeed });
                }
            }
        }

        private static Transaction CreateResponseTransaction(StoreView snapshot, OracleResponseAttribute response, Contract contract)
        {
            ScriptBuilder script = new ScriptBuilder();
            script.EmitAppCall(NativeContract.Oracle.Hash, "callback");
            var tx = new Transaction()
            {
                Version = 0,
                ValidUntilBlock = snapshot.Height + Transaction.MaxValidUntilBlockIncrement,
                Attributes = new TransactionAttribute[]{
                    new Cosigner()
                    {
                        Account = contract.ScriptHash,
                        AllowedContracts = new UInt160[]{ NativeContract.Oracle.Hash },
                        Scopes = WitnessScope.CalledByEntry
                    },
                    response
                },
                Sender = NativeContract.Oracle.Hash,
                Witnesses = new Witness[0],
                Script = script.ToArray(),
                NetworkFee = 0,
                Nonce = 0,
                SystemFee = 0
            };
            StorageKey storageKey = new StorageKey
            {
                Id = NativeContract.Oracle.Id,
                Key = new byte[sizeof(byte) + UInt256.Length]
            };
            storageKey.Key[0] = 21;
            response.RequestTxHash.ToArray().CopyTo(storageKey.Key.AsSpan(1));
            OracleRequest request = snapshot.Storages.GetAndChange(storageKey)?.GetInteroperable<OracleRequest>();
            request.Status = RequestStatusType.Ready;

            var state = new TransactionState
            {
                BlockIndex = snapshot.PersistingBlock.Index,
                Transaction = tx
            };
            snapshot.Transactions.Add(tx.Hash, state);
            var engine = ApplicationEngine.Run(tx.Script, snapshot, tx, testMode: true);
            if (engine.State != VMState.HALT) return null;
            tx.SystemFee = engine.GasConsumed;
            int size = tx.Size;
            tx.NetworkFee += Wallet.CalculateNetworkFee(contract.Script, ref size);
            tx.NetworkFee += size * NativeContract.Policy.GetFeePerByte(snapshot);
            return tx;
        }

        public void SubmitOraclePayload(OraclePayload msg)
        {
            var snapshot = _snapshotFactory();
            OracleRequest request = NativeContract.Oracle.GetRequest(snapshot, msg.RequestTxHash);
            if (request != null && request.Status != RequestStatusType.Request) return;
            if (_isStarted == 1)
            {
                ECPoint[] oraclePublicKeys = NativeContract.Oracle.GetOracleValidators(snapshot);
                var contract = Contract.CreateMultiSigContract(oraclePublicKeys.Length - (oraclePublicKeys.Length - 1) / 3, oraclePublicKeys);
                OracleTask task = new OracleTask(msg.RequestTxHash);
                task.AddResponseItem(contract, oraclePublicKeys, new ResponseItem(msg), _trackern, _pendingQueue);
                if (!_pendingQueue.TryAdd(msg.RequestTxHash, task))
                {
                    if (_pendingQueue.TryGetValue(task.requestTxHash, out OracleTask new_oracleTask))
                        new_oracleTask.AddResponseItem(contract, oraclePublicKeys, new ResponseItem(msg), _trackern, _pendingQueue);
                }
            }
        }

        private static void Log(string message, LogLevel level = LogLevel.Info)
        {
            Utility.Log(nameof(OracleService), level, message);
        }

        public static OracleResponseAttribute Process(OracleRequest request)
        {
            Uri.TryCreate(request.Url, UriKind.Absolute, out var uri);
            switch (uri.Scheme.ToLowerInvariant())
            {
                case "http":
                case "https":
                    return HTTPSProtocol.Process(request);
                default:
                    return CreateError(request.RequestTxHash);
            }
        }

        public static OracleResponseAttribute CreateError(UInt256 requestHash)
        {
            return CreateResult(requestHash, null, 0);
        }

        public static OracleResponseAttribute CreateResult(UInt256 requestTxHash, byte[] result, long filterCost)
        {
            return new OracleResponseAttribute()
            {
                RequestTxHash = requestTxHash,
                Data = result,
                FilterCost = filterCost
            };
        }
    }
}

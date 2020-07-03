using Akka.Actor;
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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static OracleTracker.OraclePostHandler;

namespace OracleTracker
{
    public class OraclePreHandler : UntypedActor
    {
        public IActorRef oraclePreHandler;
        public class ProcessRequestTask { public SnapshotView snapshot; public Transaction tx; }
        public class StartService { public Wallet wallet; }
        public class StopService { }
        public class ProcessOraclePayload { public OraclePayload payload; }
        private class Timer { }

        private (Contract Contract, KeyPair Key)[] _accounts;
        private readonly OracleTracker _trackern;

        private long _isStarted = 0;

        private SnapshotView _lastSnapshot;
        private readonly Func<SnapshotView> _snapshotFactory;
        private Func<OracleRequest, OracleResponseAttribute> Protocols { get; }
        private static IOracleProtocol HTTPSProtocol { get; } = new OracleHttpProtocol();

        public OraclePreHandler(IActorRef postHandler, OracleTracker tracker, int capacity)
        {
            Protocols = Process;
            oraclePreHandler = postHandler;
            _accounts = new (Contract Contract, KeyPair Key)[0];
            _snapshotFactory = new Func<SnapshotView>(() => _lastSnapshot ?? Blockchain.Singleton.GetSnapshot());
            _trackern = tracker;
        }

        public bool OnStart(Wallet wallet)
        {
            if (Interlocked.Exchange(ref _isStarted, 1) != 0) return false;
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
            return true;
        }

        public void OnStop()
        {
            if (Interlocked.Exchange(ref _isStarted, 0) != 1) return;
            Log("OnStop");
            _accounts = new (Contract Contract, KeyPair Key)[0];
        }

        public void ProcessRequest(SnapshotView snapshot, Transaction tx)
        {
            if (_isStarted != 1) return;
            OracleTask task = new OracleTask(tx.Hash);
            Log($"Process oracle request: requestTx={task.requestTxHash}");
            _lastSnapshot = snapshot;
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
                    task.request = request;
                    task.responseItems.Add(new ResponseItem(response_payload, responseTx));
                    oraclePreHandler.Tell(new AddOrUpdateOracleTask() { snapshot = snapshot, task = task });
                    Log($"Send oracle signature: oracle={response_payload.OraclePub} requestTx={task.requestTxHash} signaturePayload={response_payload.Hash}");
                    _trackern.SendMessage(response_payload);
                }
            }
        }

        private Transaction CreateResponseTransaction(StoreView snapshot, OracleResponseAttribute response, Contract contract)
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
            if (_isStarted != 1) return;
            var snapshot = _snapshotFactory();
            if (!msg.Verify(snapshot)) throw new Exception("Invailed Data");
            OracleRequest request = NativeContract.Oracle.GetRequest(snapshot, msg.RequestTxHash);
            if (request != null && request.Status != RequestStatusType.Request) throw new Exception("Request has been finished");
            if (_isStarted == 1)
            {
                OracleTask task = new OracleTask(msg.RequestTxHash);
                task.responseItems.Add(new ResponseItem(msg));
                oraclePreHandler.Tell(new AddOrUpdateOracleTask() { snapshot = snapshot, task = task });
            }
        }

        public static void Log(string message, LogLevel level = LogLevel.Info)
        {
            Utility.Log(nameof(OraclePreHandler), level, message);
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

        protected override void OnReceive(object message)
        {
            switch (message)
            {
                case StartService start:
                    OnStart(start.wallet);
                    break;
                case StopService stop:
                    OnStop();
                    break;
                case ProcessRequestTask request:
                    new Task(() => ProcessRequest(request.snapshot, request.tx));
                    break;
                case ProcessOraclePayload payload:
                    new Task(() => SubmitOraclePayload(payload.payload));
                    break;
            }
        }

        public static Props Props(IActorRef postHanler, OracleTracker tracker, int capacity)
        {
            return Akka.Actor.Props.Create(() => new OraclePreHandler(postHanler, tracker, capacity)).WithMailbox("OraclePreHandler-mailbox");
        }
    }
}

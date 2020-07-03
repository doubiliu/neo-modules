using Akka.Actor;
using Neo;
using Neo.Cryptography.ECC;
using Neo.IO;
using Neo.Ledger;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.Plugins;
using Neo.SmartContract;
using Neo.SmartContract.Native;
using Neo.SmartContract.Native.Tokens;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace OracleTracker
{
    public class OraclePostHandler:UntypedActor
    {
        public class AddOrUpdateOracleTask { public SnapshotView snapshot; public OracleTask task; }
        private class Timer { }

        private static readonly TimeSpan TimeoutInterval = TimeSpan.FromMinutes(5);

        private readonly ICancelable timer = Context.System.Scheduler.ScheduleTellRepeatedlyCancelable(TimeoutInterval, TimeoutInterval, Context.Self, new Timer(), ActorRefs.NoSender);

        private readonly ConcurrentDictionary<UInt256, OracleTask> _pendingQueue;

        private OracleTracker tracker;

        public class OracleTask
        {
            public readonly DateTime timeStamp;
            public UInt256 requestTxHash;
            public OracleRequest request;
            public ResponseCollection responseItems;

            public OracleTask(UInt256 requestTxHash, OracleRequest request = null, ResponseCollection responses=null)
            {
                this.requestTxHash = requestTxHash;
                this.request = request;
                this.responseItems = responses??new ResponseCollection();
                this.timeStamp = TimeProvider.Current.UtcNow;
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

        public OraclePostHandler(OracleTracker tracker)
        {
            this.tracker = tracker;
            _pendingQueue = new ConcurrentDictionary<UInt256, OracleTask>();
        }

        public void CleanOutOfDateOracleTask()
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

        private static void Log(string message, LogLevel level = LogLevel.Info)
        {
            Utility.Log(nameof(OraclePreHandler), level, message);
        }

        public void OnAddOrUpdateOracleTask(SnapshotView snapshot , OracleTask task) {
            ECPoint[] oraclePublicKeys = NativeContract.Oracle.GetOracleValidators(snapshot);
            var contract = Contract.CreateMultiSigContract(oraclePublicKeys.Length - (oraclePublicKeys.Length - 1) / 3, oraclePublicKeys);

            if (_pendingQueue.TryGetValue(task.requestTxHash, out OracleTask initOracleTask))
            {
                if (task.request != null) initOracleTask.request = task.request;
                initOracleTask.responseItems.RemoveOutOfDateResponseItem(oraclePublicKeys);
                foreach (var item in task.responseItems) {
                    initOracleTask.responseItems.Add(item);
                }

                var mine_responseItem = initOracleTask.responseItems.Where(p => p.IsMine).FirstOrDefault();
                if (mine_responseItem is null) return;
                ContractParametersContext responseTransactionContext = new ContractParametersContext(mine_responseItem.Tx);

                foreach (var responseItem in initOracleTask.responseItems)
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
            else {
                _pendingQueue.TryAdd(task.requestTxHash, task);
            }
        }

        protected override void OnReceive(object message)
        {
            switch (message)
            {
                case AddOrUpdateOracleTask task:
                    OnAddOrUpdateOracleTask(task.snapshot,task.task);
                    break;
                case Timer timer:
                    CleanOutOfDateOracleTask();
                    break;
            }
        }

        public static Props Props(OracleTracker tracker, int capacity)
        {
            return Akka.Actor.Props.Create(() => new OraclePostHandler(tracker)).WithMailbox("OraclePostHandlere-mailbox");
        }
    }
}

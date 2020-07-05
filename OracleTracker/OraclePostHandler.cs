using Akka.Actor;
using Neo;
using Neo.Cryptography.ECC;
using Neo.Ledger;
using Neo.Persistence;
using Neo.Plugins;
using Neo.SmartContract;
using Neo.SmartContract.Native;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace OracleTracker
{
    public class OraclePostHandler : UntypedActor
    {
        private IActorRef blockChain;
        public class AddOrUpdateOracleTask { public SnapshotView snapshot; public OracleTask task; }
        private class Timer { }

        private static readonly TimeSpan TimeoutInterval = TimeSpan.FromMinutes(5);
        private readonly ICancelable timer = Context.System.Scheduler.ScheduleTellRepeatedlyCancelable(TimeoutInterval, TimeoutInterval, Context.Self, new Timer(), ActorRefs.NoSender);

        private readonly ConcurrentDictionary<UInt256, OracleTask> pendingQueue;

        public OraclePostHandler(IActorRef blockChain)
        {
            this.blockChain = blockChain;
            pendingQueue = new ConcurrentDictionary<UInt256, OracleTask>();
        }

        public void CleanOutOfDateOracleTask()
        {
            List<UInt256> outOfDateTaskHashs = new List<UInt256>();
            foreach (var outOfDateTask in pendingQueue)
            {
                DateTime now = TimeProvider.Current.UtcNow;
                if (now - outOfDateTask.Value.timeStamp <= TimeoutInterval) break;
                outOfDateTaskHashs.Add(outOfDateTask.Key);
            }
            foreach (UInt256 txHash in outOfDateTaskHashs)
            {
                pendingQueue.TryRemove(txHash, out _);
            }
        }

        public void OnAddOrUpdateOracleTask(SnapshotView snapshot, OracleTask task)
        {
            ECPoint[] oraclePublicKeys = NativeContract.Oracle.GetOracleValidators(snapshot);
            var contract = Contract.CreateMultiSigContract(oraclePublicKeys.Length - (oraclePublicKeys.Length - 1) / 3, oraclePublicKeys);

            if (pendingQueue.TryGetValue(task.requestTxHash, out OracleTask initOracleTask))
            {
                if (task.request != null) initOracleTask.request = task.request;
                initOracleTask.responseItems.RemoveOutOfDateResponseItem(oraclePublicKeys);
                foreach (var item in task.responseItems)
                {
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
                    pendingQueue.TryRemove(mine_responseItem.RequestTxHash, out _);
                    blockChain.Tell(new Blockchain.RelayResult { Inventory = mine_responseItem.Tx });
                }
            }
            else
            {
                pendingQueue.TryAdd(task.requestTxHash, task);
            }
        }

        private static void Log(string message, LogLevel level = LogLevel.Info)
        {
            Utility.Log(nameof(OracleService), level, message);
        }

        protected override void OnReceive(object message)
        {
            switch (message)
            {
                case AddOrUpdateOracleTask task:
                    OnAddOrUpdateOracleTask(task.snapshot, task.task);
                    break;
                case Timer timer:
                    CleanOutOfDateOracleTask();
                    break;
            }
        }

        public static Props Props(IActorRef blockChain)
        {
            return Akka.Actor.Props.Create(() => new OraclePostHandler(blockChain)).WithMailbox("OraclePostHandler-mailbox");
        }
    }
}

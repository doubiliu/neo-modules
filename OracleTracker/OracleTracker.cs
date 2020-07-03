using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.Plugins;
using Neo.SmartContract.Native;
using Neo.VM;
using Neo.Wallets;
using System.Collections.Generic;
using System.Linq;
using static Neo.Ledger.Blockchain;
using Neo;
using Akka.Actor;

namespace OracleTracker
{
    public class OracleTracker : Plugin, IPersistencePlugin
    {
        public OracleService service;

        public override string Description => "Oracle plugin";

        public OracleTracker()
        {
            service = new OracleService(this, ProtocolSettings.Default.MemoryPoolMaxTransactions);
            RpcServerPlugin.RegisterMethods(this);
        }

        protected override void Configure()
        {
            var nodes = GetConfiguration().GetSection("nodes").Value;
            // connect other oracle nodes
        }

        public void OnPersist(StoreView snapshot, IReadOnlyList<ApplicationExecuted> applicationExecutedList)
        {
            foreach (var appExec in applicationExecutedList)
            {
                Transaction tx = appExec.Transaction;
                VMState state = appExec.VMState;
                if (tx is null || state != VMState.HALT) continue;
                var notify = appExec.Notifications.Where(q =>
                {
                    if (q.ScriptHash.Equals(NativeContract.Oracle.Hash) && (q.EventName.Equals("Request"))) return true;
                    return false;
                }).FirstOrDefault();
                if (notify is null) continue;
                service.SubmitRequest((SnapshotView)snapshot.Clone(), tx);
            }
        }

        public virtual void SendMessage(RelayResult relayResult) {
            System.Blockchain.Tell(relayResult);
        }

        public void StartOracle(Wallet wallet, byte numberOfTasks = 4)
        {
            service.Start(wallet, numberOfTasks);
        }

        public void StopOracle()
        {
            service.Stop();
        }
    }
}

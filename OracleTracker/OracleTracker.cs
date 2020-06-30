using Neo.Ledger;
using Neo.Network.P2P.Payloads;
using Neo.Network.P2P;
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
    public class OracleTracker : Plugin, IPersistencePlugin, IP2PPlugin
    {
        public OracleService service;

        public OracleTracker(IActorRef Blockchain = null)
        {
            service = new OracleService(Blockchain ?? System.Blockchain, ProtocolSettings.Default.MemoryPoolMaxTransactions);
        }

        public bool OnP2PMessage(Message message)
        {
            if (message.Command == MessageCommand.Oracle)
            {
                OraclePayload payload = (OraclePayload)message.Payload;
                StoreView snapshot = Blockchain.Singleton.GetSnapshot();
                if (!payload.Verify(snapshot)) return false;
                service.SubmitOraclePayload(payload);
            }
            return true;
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

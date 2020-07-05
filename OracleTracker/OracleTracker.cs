using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.Plugins;
using Neo.SmartContract.Native;
using Neo.VM;
using System.Collections.Generic;
using System.Linq;
using static Neo.Ledger.Blockchain;
using Akka.Actor;
using static OracleTracker.OracleService;
using Microsoft.Extensions.Configuration;
using Neo.IO;

namespace OracleTracker
{
    public class OracleTracker : Plugin, IPersistencePlugin
    {
        public IActorRef postHandler;
        public OracleRPC oracleRPC;
        public IActorRef oracleService;

        public override string Description => "Oracle plugin";

        protected override void Configure()
        {
            var nodes = GetConfiguration().GetSection("Nodes").GetChildren().Select(p => p.Get<string>()).ToArray();
            postHandler = System.ActorSystem.ActorOf(OraclePostHandler.Props(System.Blockchain));
            oracleService = System.ActorSystem.ActorOf(OracleService.Props(postHandler,nodes));
            oracleRPC = new OracleRPC(oracleService);
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
                oracleService.Tell(new ProcessRequest() { snapshot=(SnapshotView)snapshot.Clone(),tx= tx });
            }
        }
    }
}

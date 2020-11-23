using Akka.Actor;
using Neo.IO;
using Neo.IO.Json;
using Neo.Ledger;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.Plugins.FSStorage.innerring;
using Neo.SmartContract;
using Neo.VM;
using System;
using System.Collections.Generic;
using static Neo.Plugins.FSStorage.innerring.InnerRingService;

namespace Neo.Plugins.FSStorage
{
    public class FSStorage : Plugin, IPersistencePlugin
    {
        public IActorRef innering;
        public override string Name => "FSStorage";
        public override string Description => "Uses FSStorage to provide distributed file storage service";

        public FSStorage()
        {
            if (Settings.Default.IsSender)
            {
                innering = System.ActorSystem.ActorOf(InnerRingService.Props(Plugin.System));
                RpcServerPlugin.RegisterMethods(this);
                innering.Tell(new Start() { });
            }
            else
            {
                innering = System.ActorSystem.ActorOf(InnerRingSender.Props());
            }
        }

        protected override void Configure()
        {
            Settings.Load(GetConfiguration());
        }

        public void OnPersist(StoreView snapshot, IReadOnlyList<Blockchain.ApplicationExecuted> applicationExecutedList)
        {
            foreach (var appExec in applicationExecutedList)
            {
                Transaction tx = appExec.Transaction;
                VMState state = appExec.VMState;
                if (tx is null || state != VMState.HALT) continue;
                var notifys = appExec.Notifications;
                if (notifys is null) continue;
                foreach (var notify in notifys)
                {
                    var contract = notify.ScriptHash;
                    if (Settings.Default.IsSender)
                    {
                        if (contract != Settings.Default.FsContractHash) continue;
                        innering.Tell(new MainContractEvent() { notify = notify });
                    }
                    else
                    {
                        if (!Settings.Default.Contracts.Contains(contract)) continue;
                        innering.Tell(new MorphContractEvent() { notify = notify });
                    }
                }
            }
        }

        [RpcMethod]
        public bool ReceiveMainNetEvent(JArray _params)
        {
            var notify = GetNotifyEventArgsFromJson(_params);
            innering.Tell(new MainContractEvent() { notify = notify });
            return true;
        }

        public static NotifyEventArgs GetNotifyEventArgsFromJson(JArray _params)
        {
            IVerifiable container = _params[0].AsString().HexToBytes().AsSerializable<Transaction>();
            UInt160 contractHash = UInt160.Parse(_params[1].AsString());
            string eventName = _params[2].AsString();
            IEnumerator<JObject> array = ((JArray)_params[3]).GetEnumerator();
            VM.Types.Array state = new VM.Types.Array();
            while (array.MoveNext())
            {
                state.Add(Neo.Network.RPC.Utility.StackItemFromJson(array.Current));
            }
            return new NotifyEventArgs(container, contractHash, eventName, state);
        }

        public override void Dispose()
        {
            base.Dispose();
            innering.Tell(new Stop() { });
        }
    }
}

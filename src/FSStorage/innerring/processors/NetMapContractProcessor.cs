using Akka.Actor;
using Neo.Cryptography.ECC;
using Neo.Plugins.FSStorage.innerring.invoke;
using Neo.Plugins.FSStorage.innerring.timers;
using Neo.Plugins.FSStorage.morph.invoke;
using Neo.Plugins.util;
using NeoFS.API.v2.Netmap;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using static Neo.Plugins.FSStorage.innerring.timers.EpochTickEvent;
using static Neo.Plugins.FSStorage.MorphEvent;
using static Neo.Plugins.util.WorkerPool;

namespace Neo.Plugins.FSStorage.innerring.processors
{
    public class NetMapContractProcessor : IProcessor
    {
        private string name = "NetMapContractProcessor";
        public UInt160 NetmapContractHash => Settings.Default.NetmapContractHash;
        private string NewEpochNotification = "NewEpoch";
        private string AddPeerNotification = "AddPeer";
        private string UpdatePeerStateNotification = "UpdateState";

        public CleanupTable NetmapSnapshot;

        public Client Client;
        public IActiveState ActiveState;
        public IEpochState EpochState;
        public IEpochTimerReseter EpochTimerReseter;
        public IActorRef WorkPool;

        public string Name { get => name; set => name = value; }

        public bool IsActive()
        {
            return ActiveState.IsActive();
        }

        public HandlerInfo[] ListenerHandlers()
        {
            HandlerInfo newEpochHandler = new HandlerInfo();
            newEpochHandler.ScriptHashWithType = new ScriptHashWithType() { Type = NewEpochNotification, ScriptHashValue = NetmapContractHash };
            newEpochHandler.Handler = HandleNewEpoch;

            HandlerInfo addPeerHandler = new HandlerInfo();
            addPeerHandler.ScriptHashWithType = new ScriptHashWithType() { Type = AddPeerNotification, ScriptHashValue = NetmapContractHash };
            addPeerHandler.Handler = HandleAddPeer;

            HandlerInfo updatePeerStateHandler = new HandlerInfo();
            updatePeerStateHandler.ScriptHashWithType = new ScriptHashWithType() { Type = UpdatePeerStateNotification, ScriptHashValue = NetmapContractHash };
            updatePeerStateHandler.Handler = HandleUpdateState;

            return new HandlerInfo[] { newEpochHandler, addPeerHandler, updatePeerStateHandler };
        }

        public ParserInfo[] ListenerParsers()
        {
            ParserInfo newEpochParser = new ParserInfo();
            newEpochParser.ScriptHashWithType = new ScriptHashWithType() { Type = NewEpochNotification, ScriptHashValue = NetmapContractHash };
            newEpochParser.Parser = MorphEvent.ParseNewEpochEvent;

            ParserInfo addPeerParser = new ParserInfo();
            addPeerParser.ScriptHashWithType = new ScriptHashWithType() { Type = AddPeerNotification, ScriptHashValue = NetmapContractHash };
            addPeerParser.Parser = MorphEvent.ParseAddPeerEvent;

            ParserInfo updatePeerParser = new ParserInfo();
            updatePeerParser.ScriptHashWithType = new ScriptHashWithType() { Type = UpdatePeerStateNotification, ScriptHashValue = NetmapContractHash };
            updatePeerParser.Parser = MorphEvent.ParseUpdatePeerEvent;

            return new ParserInfo[] { newEpochParser, addPeerParser, updatePeerParser };
        }

        public HandlerInfo[] TimersHandlers()
        {
            HandlerInfo newEpochHandler = new HandlerInfo();
            newEpochHandler.ScriptHashWithType = new ScriptHashWithType() { Type = Timers.EpochTimer };
            newEpochHandler.Handler = HandleNewEpochTick;
            return new HandlerInfo[] { newEpochHandler };
        }

        public void HandleNewEpochTick(IContractEvent timersEvent)
        {
            NewEpochTickEvent newEpochTickEvent = (NewEpochTickEvent)timersEvent;
            Dictionary<string, string> pairs = new Dictionary<string, string>();
            pairs.Add("tick", ":");
            pairs.Add("type", "epoch");
            Utility.Log(Name, LogLevel.Info, pairs.ParseToString());
            WorkPool.Tell(new NewTask() { process = Name,task = new Task(() => ProcessNewEpochTick(newEpochTickEvent))});
        }

        public void HandleNewEpoch(IContractEvent morphEvent)
        {
            NewEpochEvent newEpochEvent = (NewEpochEvent)morphEvent;
            Dictionary<string, string> pairs = new Dictionary<string, string>();
            pairs.Add("notification", ":");
            pairs.Add("type", "new epoch");
            pairs.Add("value", newEpochEvent.EpochNumber.ToString());
            Utility.Log(Name, LogLevel.Info, pairs.ParseToString());
            WorkPool.Tell(new NewTask() { process = Name, task = new Task(() => ProcessNewEpoch(newEpochEvent))});
        }

        public void HandleAddPeer(IContractEvent morphEvent)
        {
            AddPeerEvent addPeerEvent = (AddPeerEvent)morphEvent;
            Dictionary<string, string> pairs = new Dictionary<string, string>();
            pairs.Add("notification", ":");
            pairs.Add("type", "add peer");
            Utility.Log(Name, LogLevel.Info, pairs.ParseToString());
            WorkPool.Tell(new NewTask() { process = Name, task = new Task(() => ProcessAddPeer(addPeerEvent)) });
        }

        public void HandleUpdateState(IContractEvent morphEvent)
        {
            UpdatePeerEvent updateStateEvent = (UpdatePeerEvent)morphEvent;
            Dictionary<string, string> pairs = new Dictionary<string, string>();
            pairs.Add("notification", ":");
            pairs.Add("type", "update peer state");
            pairs.Add("key", updateStateEvent.PublicKey.EncodePoint(true).ToHexString());
            Utility.Log(Name, LogLevel.Info, pairs.ParseToString());
            WorkPool.Tell(new NewTask() { process = Name, task = new Task(() => ProcessUpdateState(updateStateEvent)) });
        }

        public void HandleCleanupTick(IContractEvent morphEvent)
        {
            if (!NetmapSnapshot.Enabled)
            {
                Utility.Log(Name, LogLevel.Debug, "netmap clean up routine is disabled");
                return;
            }
            NetmapCleanupTickEvent netmapCleanupTickEvent = (NetmapCleanupTickEvent)morphEvent;
            Dictionary<string, string> pairs = new Dictionary<string, string>();
            pairs.Add("tick", ":");
            pairs.Add("type", "netmap cleaner");
            Utility.Log(Name, LogLevel.Info, pairs.ParseToString());
            WorkPool.Tell(new NewTask() { process = Name, task = new Task(() => ProcessNetmapCleanupTick(netmapCleanupTickEvent)) });
        }

        public void ProcessNetmapCleanupTick(NetmapCleanupTickEvent netmapCleanupTickEvent)
        {
            if (!IsActive())
            {
                Utility.Log(Name, LogLevel.Info, "passive mode, ignore new netmap cleanup tick");
                return;
            }
            try
            {
                NetmapSnapshot.ForEachRemoveCandidate(netmapCleanupTickEvent.Epoch, Func);
            }
            catch (Exception e)
            {
                Utility.Log(Name, LogLevel.Warning, string.Format("can't iterate on netmap cleaner cache.{0}",e.Message));
            }
        }

        private void Func(string s)
        {
            ECPoint key = null;
            try
            {
                key = ECPoint.FromBytes(s.HexToBytes(), ECCurve.Secp256r1);
            }
            catch
            {
                Utility.Log("can't decode public key of netmap node", LogLevel.Warning, s);
            }
            Utility.Log(Name, LogLevel.Info, string.Format("vote to remove node from netmap,{0}",s));
            try
            {
                ContractInvoker.UpdatePeerState(Client, new ContractInvoker.UpdatePeerArgs()
                {
                    Key = key,
                    Status = (int)NeoFS.API.v2.Netmap.NodeInfo.Types.State.Offline
                });
            }
            catch (Exception e)
            {
                Utility.Log(Name, LogLevel.Error, string.Format("can't invoke netmap.UpdateState,{0}",e.Message));
            }
        }

        public void ProcessNewEpochTick(NewEpochTickEvent timersEvent)
        {
            if (!IsActive())
            {
                Utility.Log(Name, LogLevel.Info, "passive mode, ignore new epoch tick");
                return;
            }
            ulong nextEpoch = EpochCounter() + 1;
            Utility.Log(Name, LogLevel.Info, string.Format("next epoch,{0}",nextEpoch));
            try
            {
                ContractInvoker.SetNewEpoch(Client, nextEpoch);
            }
            catch (Exception e)
            {
                Utility.Log(Name, LogLevel.Error, string.Format("can't invoke netmap.NewEpoch,{0}",e.Message));
            }
        }

        public void ProcessNewEpoch(NewEpochEvent newEpochEvent)
        {
            EpochState.SetEpochCounter(newEpochEvent.EpochNumber);
            EpochTimerReseter.ResetEpochTimer();

            NodeInfo[] snapshot;
            try
            {
                snapshot = ContractInvoker.NetmapSnapshot(Client);
            }
            catch (Exception e)
            {
                Utility.Log(Name, LogLevel.Info, string.Format("can't get netmap snapshot to perform cleanup,{0}", e.Message));
                return;
            }
            NetmapSnapshot.Update(snapshot, newEpochEvent.EpochNumber);
            HandleCleanupTick(new NetmapCleanupTickEvent() { Epoch = newEpochEvent.EpochNumber });
        }

        public void ProcessAddPeer(AddPeerEvent addPeerEvent)
        {
            if (!IsActive())
            {
                Utility.Log(Name, LogLevel.Info, "passive mode, ignore new peer notification");
                return;
            }
            NodeInfo nodeInfo = null;
            try
            {
                nodeInfo = NodeInfo.Parser.ParseFrom(addPeerEvent.Node);
            }
            catch
            {
                Utility.Log(Name, LogLevel.Warning, "can't parse network map candidate");
                return;
            }
            var key = nodeInfo.PublicKey.ToByteArray().ToHexString();
            if (!NetmapSnapshot.Touch(key, EpochState.EpochCounter()))
            {
                Utility.Log(Name, LogLevel.Info, string.Format("approving network map candidate,{0}",key));
                try
                {
                    ContractInvoker.ApprovePeer(Client, addPeerEvent.Node);
                }
                catch (Exception e)
                {
                    Utility.Log(Name, LogLevel.Error, string.Format("can't invoke netmap.AddPeer:{0}",e.Message));
                }
            }
        }

        public void ProcessUpdateState(UpdatePeerEvent updateStateEvent)
        {
            if (!IsActive())
            {
                Utility.Log(Name, LogLevel.Info, "passive mode, ignore new epoch tick");
                return;
            }
            if (updateStateEvent.Status != (uint)NeoFS.API.v2.Netmap.NodeInfo.Types.State.Offline)
            {
                Dictionary<string, string> pairs = new Dictionary<string, string>();
                pairs.Add("node proposes unknown state", ":");
                pairs.Add("key", updateStateEvent.PublicKey.EncodePoint(true).ToHexString());
                pairs.Add("status", updateStateEvent.Status.ToString());
                Utility.Log(Name, LogLevel.Warning, pairs.ParseToString());
                return;
            }
            NetmapSnapshot.Flag(updateStateEvent.PublicKey.ToString());
            try
            {
                ContractInvoker.UpdatePeerState(Client, new ContractInvoker.UpdatePeerArgs()
                {
                    Key = updateStateEvent.PublicKey,
                    Status = (int)updateStateEvent.Status
                });
            }
            catch (Exception e)
            {
                Utility.Log(Name, LogLevel.Error, string.Format("can't invoke netmap.UpdatePeer,{0}", e.Message));
            }
        }

        public ulong EpochCounter()
        {
            return EpochState.EpochCounter();
        }

        public void SetEpochCounter(ulong epoch)
        {
            EpochState.SetEpochCounter(epoch);
        }

        public void ResetEpochTimer()
        {
            EpochTimerReseter.ResetEpochTimer();
        }

        public class CleanupTable
        {
            private object lockObject;
            private Dictionary<string, EpochStamp> lastAccess;
            private bool enabled;
            private ulong threshold;

            public bool Enabled { get => enabled; set => enabled = value; }

            public CleanupTable(bool enabled, ulong threshold)
            {
                this.lockObject = new object();
                this.enabled = enabled;
                this.threshold = threshold;
                lastAccess = new Dictionary<string, EpochStamp>();
            }

            public void Update(NodeInfo[] snapshot, ulong now)
            {
                lock (lockObject)
                {
                    var newMap = new Dictionary<string, EpochStamp>();
                    foreach (var item in snapshot)
                    {
                        var key = item.PublicKey.ToByteArray().ToHexString();
                        if (lastAccess.TryGetValue(key, out EpochStamp access))
                        {
                            access.RemoveFlag = false;
                            newMap.Add(key, access);
                        }
                        else
                        {
                            newMap.Add(key, new EpochStamp() { Epoch = now });
                        }
                    }
                    lastAccess = newMap;
                }
            }

            public bool Touch(string key, ulong now)
            {
                lock (lockObject)
                {
                    EpochStamp epochStamp = null;
                    bool result = false;
                    if (lastAccess.TryGetValue(key, out EpochStamp access))
                    {
                        epochStamp = access;
                        result = !epochStamp.RemoveFlag;
                    }
                    else
                    {
                        epochStamp = new EpochStamp();
                    }
                    epochStamp.RemoveFlag = false;
                    if (now > epochStamp.Epoch)
                    {
                        epochStamp.Epoch = now;
                    }
                    lastAccess[key] = epochStamp;
                    return result;
                }
            }

            public void Flag(string key)
            {
                lock (lockObject)
                {
                    if (lastAccess.TryGetValue(key, out EpochStamp access))
                    {
                        access.RemoveFlag = true;
                        lastAccess[key] = access;
                    }
                    else
                    {
                        lastAccess[key] = new EpochStamp() { RemoveFlag = true };
                    }
                }
            }

            public void ForEachRemoveCandidate(ulong epoch, Action<string> f)
            {
                lock (lockObject)
                {
                    foreach (var item in lastAccess)
                    {
                        var key = item.Key;
                        var access = item.Value;
                        if (epoch - access.Epoch > threshold)
                        {
                            access.RemoveFlag = true;
                            lastAccess[key] = access;
                            f(key);
                        }
                    }
                }
            }
        }

        public class EpochStamp
        {
            private ulong epoch;
            private bool removeFlag;

            public ulong Epoch { get => epoch; set => epoch = value; }
            public bool RemoveFlag { get => removeFlag; set => removeFlag = value; }
        }
    }
}

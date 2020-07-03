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
using Neo.IO.Json;
using System;
using static OracleTracker.OraclePreHandler;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Neo.IO;

namespace OracleTracker
{
    public class OracleTracker : Plugin, IPersistencePlugin
    {
        public IActorRef postHandler;
        public IActorRef preHandler;
        private string[] nodes;

        public override string Description => "Oracle plugin";

        public OracleTracker()
        {
            postHandler=System.ActorSystem.ActorOf(OraclePostHandler.Props(this, ProtocolSettings.Default.MemoryPoolMaxTransactions));
            postHandler=System.ActorSystem.ActorOf(OraclePreHandler.Props(postHandler,this, ProtocolSettings.Default.MemoryPoolMaxTransactions));
            RpcServerPlugin.RegisterMethods(this);
        }

        protected override void Configure()
        {
            nodes = GetConfiguration().GetSection("Nodes").GetChildren().Select(p => p.Get<string>()).ToArray(); ;
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
                preHandler.Tell(new ProcessRequestTask() { snapshot=(SnapshotView)snapshot.Clone(),tx= tx });
            }
        }

        [RpcMethod]
        public JObject SubmitOracleSignatures(JArray _params)
        {
            byte[] data=_params[0].ToString().HexToBytes();
            OraclePayload payload = Neo.IO.Helper.AsSerializable<OraclePayload>(data);
            try
            {
                preHandler.Tell(new ProcessOraclePayload() {payload=payload });
            }
            catch (Exception ex)
            {
                throw new RpcException(-100, ex.Message);
            }
            return JObject.Parse("Submit success");
        }

        public virtual void SendMessage(RelayResult relayResult) {
            System.Blockchain.Tell(relayResult);
        }

        public virtual void SendMessage(OraclePayload payload)
        {
            foreach(var node in nodes) {
                var url = new Uri("http://" + node + "/");
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);

                request.Method = "POST";
                request.ContentType = "application/json";
                string data=payload.ToArray().ToHexString();
                string strContent = "[{\"data\":\""+data+"\"}]";
                using (StreamWriter dataStream = new StreamWriter(request.GetRequestStream()))
                {
                    dataStream.Write(strContent);
                    dataStream.Close();
                }
                HttpWebResponse response = (HttpWebResponse)request.GetResponse();
                string encoding = response.ContentEncoding;
                if (encoding == null || encoding.Length < 1)
                {
                    encoding = "UTF-8"; 
                }
                StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.GetEncoding(encoding));
                var retString = reader.ReadToEnd();
            }
        }

        public void StartOracle(Wallet wallet)
        {
            preHandler.Tell(new StartService() {wallet=wallet });
        }

        public void StopOracle()
        {
            preHandler.Tell(new StopService() {});
        }
    }
}

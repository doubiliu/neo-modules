using Akka.Actor;
using Neo;
using Neo.Plugins;
using Newtonsoft.Json.Linq;
using System;
using static OracleTracker.OracleService;

namespace OracleTracker
{
    public class OracleRPC
    {
        private IActorRef oracleService;

        public OracleRPC(IActorRef oracleService)
        {
            this.oracleService = oracleService;
            RpcServerPlugin.RegisterMethods(this);
        }

        [RpcMethod]
        public JObject ReceiveSignature(JArray _params)
        {
            byte[] data = _params[0].ToString().HexToBytes();
            OraclePayload payload = Neo.IO.Helper.AsSerializable<OraclePayload>(data);
            try
            {
                oracleService.Tell(new ProcessOraclePayload() { payload = payload });
            }
            catch (Exception ex)
            {
                throw new RpcException(-100, ex.Message);
            }
            return JObject.Parse("Signature has received");
        }
    }
}

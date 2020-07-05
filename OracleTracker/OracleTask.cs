using Neo.SmartContract.Native.Tokens;
using System;

namespace Neo.Plugins
{
    public class OracleTask
    {
        public UInt256 requestTxHash;
        public OracleRequest request;
        public ResponseCollection responseItems;
        public readonly DateTime timeStamp;

        public OracleTask(UInt256 requestTxHash, OracleRequest request = null, ResponseCollection responses = null)
        {
            this.requestTxHash = requestTxHash;
            this.request = request;
            this.responseItems = responses ?? new ResponseCollection();
            this.timeStamp = TimeProvider.Current.UtcNow;
        }
    }
}

using Neo.Cryptography.ECC;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using System;

namespace Neo.Plugins
{
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
}

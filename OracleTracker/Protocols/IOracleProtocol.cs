using Neo.Network.P2P.Payloads;
using Neo.SmartContract.Native.Tokens;

namespace OracleTracker.Protocols
{
    interface IOracleProtocol
    {
        OracleResponseAttribute Process(OracleRequest request);
    }
}

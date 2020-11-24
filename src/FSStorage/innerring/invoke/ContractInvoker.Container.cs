using Neo.Cryptography.ECC;
using Neo.Plugins.FSStorage.morph.invoke;

namespace Neo.Plugins.FSStorage.innerring.invoke
{
    public partial class ContractInvoker
    {
        private static UInt160 ContainerContractHash => Settings.Default.ContainerContractHash;
        private const string PutContainerMethod = "put";
        private const string DeleteContainerMethod = "delete";

        public class ContainerParams
        {
            public ECPoint Key;
            public byte[] Container;
            public byte[] Signature;
        }

        public class RemoveContainerParams
        {
            public byte[] ContainerID;
            public byte[] Signature;
        }

        public static bool RegisterContainer(Client client, ContainerParams p)
        {
            return client.InvokeFunction(ContainerContractHash, PutContainerMethod, 2 * ExtraFee, p.Container, p.Signature, p.Key.EncodePoint(true));
        }

        public static bool RemoveContainer(Client client, RemoveContainerParams p)
        {
            return client.InvokeFunction(ContainerContractHash, DeleteContainerMethod, ExtraFee, p.ContainerID, p.Signature);
        }
    }
}

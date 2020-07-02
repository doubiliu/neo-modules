using Neo;
using Neo.Cryptography;
using Neo.Cryptography.ECC;
using Neo.IO;
using Neo.Ledger;
using Neo.Network.P2P;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.SmartContract;
using Neo.SmartContract.Manifest;
using Neo.SmartContract.Native;
using Neo.VM;
using System;
using System.IO;
using System.Linq;

namespace OracleTracker
{
    public class OraclePayload : IInventory
    {
        private const long MaxWitnessGas = 0_02000000;

        public ECPoint OraclePub;
        public UInt256 RequestTxHash;
        public byte[] ResponseTxSignature;

        public OraclePayload()
        {
        }

        public Witness[] Witnesses { get; set; }

        public int Size =>
            ResponseTxSignature.GetVarSize() +  // Oracle Response Transaction Signature
            OraclePub.Size +                    // Oracle Node Public key
            Witnesses.GetVarSize() +            // Witnesses
            UInt256.Length;                     // RequestTx Hash

        public UInt256 Hash => new UInt256(Crypto.Hash256(this.GetHashData()));

        public InventoryType InventoryType => InventoryType.TX;

        void ISerializable.Deserialize(BinaryReader reader)
        {
            ((IVerifiable)this).DeserializeUnsigned(reader);
            Witnesses = reader.ReadSerializableArray<Witness>(1);
            if (Witnesses.Length != 1) throw new FormatException();
        }

        void IVerifiable.DeserializeUnsigned(BinaryReader reader)
        {
            OraclePub = reader.ReadSerializable<ECPoint>();
            RequestTxHash = reader.ReadSerializable<UInt256>();
            ResponseTxSignature = reader.ReadFixedBytes(64);
        }

        public virtual void Serialize(BinaryWriter writer)
        {
            ((IVerifiable)this).SerializeUnsigned(writer);
            writer.Write(Witnesses);
        }

        void IVerifiable.SerializeUnsigned(BinaryWriter writer)
        {
            writer.Write(OraclePub);
            writer.Write(RequestTxHash);
            writer.Write(ResponseTxSignature);
        }

        UInt160[] IVerifiable.GetScriptHashesForVerifying(StoreView snapshot)
        {
            return new[] { Contract.CreateSignatureRedeemScript(OraclePub).ToScriptHash() };
        }

        public bool Verify(StoreView snapshot)
        {
            ECPoint[] validators = NativeContract.Oracle.GetOracleValidators(snapshot);
            if (!validators.Any(u => u.Equals(OraclePub))) return false;
            return VerifyWitnesses(this,snapshot, MaxWitnessGas);
        }

        private bool VerifyWitnesses(IVerifiable verifiable, StoreView snapshot, long gas)
        {
            if (gas < 0) return false;

            UInt160[] hashes;
            try
            {
                hashes = verifiable.GetScriptHashesForVerifying(snapshot);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            if (hashes.Length != verifiable.Witnesses.Length) return false;
            for (int i = 0; i < hashes.Length; i++)
            {
                int offset;
                byte[] verification = verifiable.Witnesses[i].VerificationScript;
                if (verification.Length == 0)
                {
                    ContractState cs = snapshot.Contracts.TryGet(hashes[i]);
                    if (cs is null) return false;
                    ContractMethodDescriptor md = cs.Manifest.Abi.GetMethod("verify");
                    if (md is null) return false;
                    verification = cs.Script;
                    offset = md.Offset;
                }
                else
                {
                    if (hashes[i] != verifiable.Witnesses[i].ScriptHash) return false;
                    offset = 0;
                }
                using (ApplicationEngine engine = new ApplicationEngine(TriggerType.Verification, verifiable, snapshot, gas))
                {
                    engine.LoadScript(verification, CallFlags.ReadOnly).InstructionPointer = offset;
                    engine.LoadScript(verifiable.Witnesses[i].InvocationScript, CallFlags.None);
                    if (engine.Execute() == VMState.FAULT) return false;
                    if (engine.ResultStack.Count != 1 || !engine.ResultStack.Pop().ToBoolean()) return false;
                    gas -= engine.GasConsumed;
                }
            }
            return true;
        }
    }
}

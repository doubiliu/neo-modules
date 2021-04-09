using Akka.Actor;
using Neo;
using Neo.Cryptography;
using Neo.Cryptography.ECC;
using Neo.IO;
using Neo.Ledger;
using Neo.Network.P2P.Payloads;
using Neo.SmartContract;
using Neo.SmartContract.Native;
using Neo.VM;
using Neo.Wallets;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Neo.Plugins
{
    public class NotaryService : UntypedActor
    {
        public class Start { }
        private bool started = false;
        private Wallet wallet;
        private readonly Settings notarySetting;
        private readonly NeoSystem neoSystem;

        private readonly ConcurrentDictionary<UInt256, NotaryTask> pendingQueue = new ConcurrentDictionary<UInt256, NotaryTask>();
        private readonly List<NotaryRequest> payloadCache = new List<NotaryRequest>();

        internal NotaryService(NeoSystem neoSystem, Settings settings, Wallet wallet)
        {
            this.wallet = wallet;
            this.neoSystem = neoSystem;
            this.notarySetting = settings;
            Context.System.EventStream.Subscribe(Self, typeof(Blockchain.PersistCompleted));
            Context.System.EventStream.Subscribe(Self, typeof(Blockchain.RelayResult));
        }


        protected override void OnReceive(object message)
        {
            if (message is Start)
            {
                if (started) return;
                OnStart();
            }
            else
            {
                if (!started) return;
                switch (message)
                {
                    case Blockchain.PersistCompleted completed:
                        OnPersistCompleted(completed.Block);
                        break;
                    case Blockchain.RelayResult rr:
                        if (rr.Result == VerifyResult.Succeed && rr.Inventory is NotaryRequest payload)
                            OnNotaryPayload(payload);
                        break;
                }
            }
        }

        private void OnStart()
        {
            Log("OnStart");
            started = true;
        }

        private void OnNotaryPayload(NotaryRequest payload) {
            if (payloadCache.Count < 100)
            {
                payloadCache.Add(payload);
                OnNewRequest(payload);
            }
            else {
                var oldPayload = payloadCache[0];
                payloadCache.RemoveAt(0);
                OnRequestRemoval(oldPayload);
                OnNewRequest(payload);
            }
        }

        private void OnPersistCompleted(Block block)
        {
            Log($"Persisted {nameof(Block)}: height={block.Index} hash={block.Hash} tx={block.Transactions.Length}");
            var currHeight = block.Index;
            foreach (var item in pendingQueue)
            {
                var h = item.Key;
                var r = item.Value;
                if (!r.isSent && r.typ != RequestType.Unknown && r.nSigs == r.nSigsCollected && r.minNotValidBefore > currHeight)
                {
                    Finalize(r.mainTx);
                    r.isSent = true;
                    continue;
                }
                if (r.minNotValidBefore <= currHeight)
                {
                    var newFallbacks = new List<Transaction>();
                    foreach (var fb in r.fallbackTxs)
                    {
                        var nvb = fb.GetAttributes<NotValidBefore>().ToArray()[0].Height;
                        if (nvb <= currHeight) Finalize(fb);
                        newFallbacks.Add(fb);
                    }
                    if (newFallbacks.Count == 0) pendingQueue.TryRemove(h, out _);
                    else r.fallbackTxs = newFallbacks.ToArray();
                }
            }
        }

        private void OnNewRequest(NotaryRequest payload)
        {
            var nvbFallback = payload.FallbackTransaction.GetAttributes<NotValidBefore>().ToArray()[0].Height;
            byte nKeys = payload.MainTransaction.GetAttributes<NotaryAssisted>().ToArray()[0].NKeys;
            VerifyIncompleteWitnesses(payload.MainTransaction, nKeys, out RequestType typ, out int nSigs, out ECPoint[] pubs, out string validationErr);
            var exists = pendingQueue.TryGetValue(payload.MainTransaction.Hash, out NotaryTask r);
            if (exists)
            {
                foreach (var fb in r.fallbackTxs) if (fb.Hash.Equals(payload.FallbackTransaction.Hash)) return;
                if (nvbFallback < r.minNotValidBefore) r.minNotValidBefore = nvbFallback;
                if (r.typ == RequestType.Unknown && validationErr is null)
                {
                    r.typ = typ;
                    r.nSigs = (byte)nSigs;
                }
            }
            else
            {
                r = new NotaryTask()
                {
                    nSigs = (byte)nSigs,
                    mainTx = payload.MainTransaction,
                    typ = typ,
                    minNotValidBefore = nvbFallback
                };
                pendingQueue[payload.MainTransaction.Hash] = r;
            }
            r.fallbackTxs.Append(payload.FallbackTransaction);
            if (exists && r.typ != RequestType.Unknown && r.nSigsCollected > r.nSigs) return;
            if (validationErr is null)
            {
                for (int i = 0; i < payload.MainTransaction.Witnesses.Length; i++)
                {
                    if (payload.MainTransaction.Signers[i].Equals(NativeContract.Notary.Hash)) continue;
                    if (payload.Witnesses[i].InvocationScript.Length != 0 && payload.Witnesses[i].VerificationScript.Length != 0)
                    {
                        switch (r.typ)
                        {
                            case RequestType.Signature:
                                if (!exists) r.nSigsCollected++;
                                else if (r.mainTx.Witnesses[i].InvocationScript.Length == 0)
                                {
                                    r.mainTx.Witnesses[i] = payload.MainTransaction.Witnesses[i];
                                    r.nSigsCollected++;
                                }
                                if (r.nSigsCollected == r.nSigs) goto loop;
                                break;
                            case RequestType.MultiSignature:
                                if (r.sigs is null) r.sigs = new Dictionary<ECPoint, byte[]>();
                                var hash = r.mainTx.Hash.ToArray();
                                foreach (var pub in pubs)
                                {
                                    if (!r.sigs.TryGetValue(pub, out _)) continue;
                                    if (Crypto.VerifySignature(hash, payload.MainTransaction.Witnesses[i].InvocationScript.Skip(2).ToArray(), pub))
                                    {
                                        r.sigs[pub] = payload.Witnesses[i].InvocationScript;
                                        r.nSigsCollected++;
                                        if (r.nSigsCollected == r.nSigs)
                                        {
                                            byte[] invScript = new byte[0];
                                            for (int j = 0; j < pubs.Length; j++)
                                            {
                                                if (r.sigs.TryGetValue(pubs[j], out var sig))
                                                    invScript = invScript.Concat(sig).ToArray();
                                            }
                                            r.mainTx.Witnesses[i].InvocationScript = invScript;
                                        }
                                        goto loop;
                                    }
                                }
                                goto loop;
                        }
                    }
                }
                loop:;
            }
            var currentHeight = NativeContract.Ledger.CurrentIndex(neoSystem.GetSnapshot());
            if (r.typ != RequestType.Unknown && r.nSigsCollected == nSigs && r.minNotValidBefore > currentHeight)
            {
                Finalize(r.mainTx);
                r.isSent = true;
            }
        }

        private void OnRequestRemoval(NotaryRequest payload)
        {
            var r = pendingQueue[payload.MainTransaction.Hash];
            if (r is null) return;
            for (int i = 0; i < r.fallbackTxs.Length; i++)
            {
                var fb = r.fallbackTxs[i];
                if (fb.Hash.Equals(payload.FallbackTransaction.Hash))
                {
                    var tempList = r.fallbackTxs.ToList();
                    tempList.RemoveAt(i);
                    r.fallbackTxs = tempList.ToArray();
                    break;
                }
            }
            if (r.fallbackTxs.Length == 0) pendingQueue.Remove(r.mainTx.Hash, out _);
        }

        private void VerifyIncompleteWitnesses(Transaction tx, byte nKeys, out RequestType typ, out int nSigs, out ECPoint[] pubs, out string validationErr)
        {
            typ = RequestType.Unknown;
            nSigs = 0;
            byte nKeysActual = 0;
            pubs = null;
            byte[][] pubsBytes = null;
            validationErr = null;
            if (tx.Signers.Length < 2)
            {
                validationErr = "transaction should have at least 2 signers";
                return;
            }
            if (tx.Signers.Any(p => p.Equals(NativeContract.Notary.Hash)))
            {
                validationErr = "P2PNotary contract should be a signer of the transaction";
                return;
            }
            for (int i = 0; i < tx.Witnesses.Length; i++)
            {
                if (tx.Signers[i].Equals(NativeContract.Notary.Hash)) continue;
                if (tx.Witnesses[i].VerificationScript.Length == 0) continue;
                if (tx.Signers[i].Equals(tx.Witnesses[i].VerificationScript.ToScriptHash()))
                {
                    validationErr = string.Format("transaction should have valid verification script for signer {0}", i);
                    return;
                }
                if (tx.Witnesses[i].VerificationScript.IsMultiSigContract(out nSigs, out pubs))
                {
                    if (typ == RequestType.Signature || typ == RequestType.MultiSignature)
                    {
                        typ = RequestType.Unknown;
                        validationErr = string.Format("bad type of witness {0}: only one multisignature witness is allowed", i);
                        return;
                    }
                    typ = RequestType.MultiSignature;
                    nKeysActual = (byte)pubsBytes.Length;
                    if (tx.Witnesses[i].InvocationScript.Length != 66 || (tx.Witnesses[i].InvocationScript[0] != (byte)OpCode.PUSHDATA1 && tx.Witnesses[i].InvocationScript[1] != 64))
                    {
                        typ = RequestType.Unknown;
                        validationErr = "multisignature invocation script should have length = 66 and be of the form[PUSHDATA1, 64, signatureBytes...]";
                    }
                    continue;
                }
                if (tx.Witnesses[i].VerificationScript.IsSignatureContract())
                {
                    if (typ == RequestType.MultiSignature)
                    {
                        typ = RequestType.Unknown;
                        validationErr = string.Format("bad type of witness {0}: multisignature witness can not be combined with other witnesses", i);
                        return;
                    }
                    typ = RequestType.Signature;
                    nSigs = nKeys;
                    continue;
                }
                typ = RequestType.Unknown;
                validationErr = string.Format("unable to define the type of witness {0}", i);
                return;
            }
            switch (typ)
            {
                case RequestType.Signature:
                    if (tx.Witnesses.Length < (nKeys + 1))
                    {
                        typ = RequestType.Unknown;
                        validationErr = string.Format("transaction should comtain at least {0} witnesses (1 for notary + nKeys)", nKeys + 1);
                        return;
                    }
                    break;
                case RequestType.MultiSignature:
                    if (nKeysActual != nKeys)
                    {
                        typ = RequestType.Unknown;
                        validationErr = string.Format("bad m out of n partial multisignature witness: expected n = {0}, got n = {1}", nKeys, nKeysActual);
                        return;
                    }
                    pubs = new ECPoint[pubsBytes.Length];
                    for (int i = 0; i < pubsBytes.Length; i++)
                    {
                        try
                        {
                            var pub = ECPoint.FromBytes(pubsBytes[i], ECCurve.Secp256r1);
                            pubs[i] = pub;
                        }
                        catch
                        {
                            typ = RequestType.Unknown;
                            pubs = null;
                            validationErr = string.Format("invalid bytes of {0} public key: {1}", i, pubsBytes[i].ToHexString());
                            return;
                        }
                    }
                    break;
                default:
                    typ = RequestType.Unknown;
                    validationErr = "unexpected Notary request type";
                    return;
            }
        }

        private void Finalize(Transaction tx)
        {
            var prefix = new byte[] { (byte)OpCode.PUSHDATA1, 64 };
            var notaryWitness = new Witness()
            {
                InvocationScript = prefix.Concat(tx.Sign(wallet.GetAccounts().ToArray()[0].GetKey(), Settings.Default.Network)).ToArray(),
                VerificationScript = new byte[0]
            };
            for (int i = 0; i < tx.Signers.Length; i++)
                if (tx.Signers[i].Account.Equals(NativeContract.Notary.Hash))
                {
                    tx.Witnesses[i] = notaryWitness;
                    break;
                }
            neoSystem.Blockchain.Tell(tx);
        }

        private static void Log(string message, LogLevel level = LogLevel.Info)
        {
            Utility.Log(nameof(NotaryService), level, message);
        }

        public static Props Props(NeoSystem neoSystem, Settings notarySettings, Wallet wallet)
        {
            return Akka.Actor.Props.Create(() => new NotaryService(neoSystem, notarySettings, wallet));
        }

        public class NotaryTask
        {
            public RequestType typ;
            public bool isSent;
            public Transaction mainTx;
            public Transaction[] fallbackTxs;
            public uint minNotValidBefore;
            public byte nSigs;
            public byte nSigsCollected;
            public Dictionary<ECPoint, byte[]> sigs;
        }

        public enum RequestType : byte
        {
            Unknown = 0x00,
            Signature = 0x01,
            MultiSignature = 0x02
        }
    }
}

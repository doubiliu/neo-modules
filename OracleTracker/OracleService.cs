using Akka.Actor;
using Neo;
using Neo.Cryptography.ECC;
using Neo.IO;
using Neo.Ledger;
using Neo.Network.P2P.Payloads;
using Neo.Oracle.Protocols.Https;
using Neo.Persistence;
using Neo.Plugins;
using Neo.SmartContract;
using Neo.SmartContract.Native;
using Neo.SmartContract.Native.Tokens;
using Neo.VM;
using Neo.Wallets;
using OracleTracker.Protocols;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static OracleTracker.OraclePostHandler;

namespace OracleTracker
{
    public class OracleService : UntypedActor
    {
        public IActorRef oraclePostHandler;
        public class ProcessRequest { public SnapshotView snapshot; public Transaction tx; }
        public class StartService { public Wallet wallet; }
        public class StopService { }
        public class ProcessOraclePayload { public OraclePayload payload; }
        public class SendSignature { public OraclePayload payload; }
        private class Timer { }

        private (Contract Contract, KeyPair Key)[] accounts;
        private string[] nodes;
        private long isStarted = 0;
        private SnapshotView lastSnapshot;
        private readonly Func<SnapshotView> snapshotFactory;
        private Func<OracleRequest, OracleResponseAttribute> Protocols { get; }
        private static IOracleProtocol HTTPSProtocol { get; } = new OracleHttpProtocol();

        public OracleService(IActorRef postHandler, string[] nodes)
        {
            Protocols = Process;
            oraclePostHandler = postHandler;
            accounts = new (Contract Contract, KeyPair Key)[0];
            snapshotFactory = new Func<SnapshotView>(() => lastSnapshot ?? Blockchain.Singleton.GetSnapshot());
            this.nodes = nodes;
        }

        public bool OnStart(Wallet wallet)
        {
            if (Interlocked.Exchange(ref isStarted, 1) != 0) return false;
            using SnapshotView snapshot = snapshotFactory();
            var oracles = NativeContract.Oracle.GetOracleValidators(snapshot)
                .Select(u => Contract.CreateSignatureRedeemScript(u).ToScriptHash());

            accounts = wallet?.GetAccounts()
                .Where(u => u.HasKey && !u.Lock && oracles.Contains(u.ScriptHash))
                .Select(u => (u.Contract, u.GetKey()))
                .ToArray();
            if (accounts.Length == 0) throw new ArgumentException("The wallet doesn't have any oracle accounts");
            return true;
        }

        public void OnStop()
        {
            if (Interlocked.Exchange(ref isStarted, 0) != 1) return;
            Log("OnStop");
            accounts = new (Contract Contract, KeyPair Key)[0];
        }

        public void OnProcessRequest(SnapshotView snapshot, Transaction tx)
        {
            if (isStarted != 1) return;
            OracleTask task = new OracleTask(tx.Hash);
            Log($"Process oracle request: requestTx={task.requestTxHash}");
            lastSnapshot = snapshot;
            OracleRequest request = NativeContract.Oracle.GetRequest(snapshot, task.requestTxHash);
            if (request is null || request.Status != RequestStatusType.Request) return;
            ECPoint[] oraclePublicKeys = NativeContract.Oracle.GetOracleValidators(snapshot);
            var contract = Contract.CreateMultiSigContract(oraclePublicKeys.Length - (oraclePublicKeys.Length - 1) / 3, oraclePublicKeys);

            OracleResponseAttribute response = Protocols(request);
            var responseTx = CreateResponseTransaction(snapshot.Clone(), response, contract);
            if (responseTx is null) return;
            Log($"Generated response tx: requestTx={task.requestTxHash} responseTx={responseTx.Hash}");

            foreach (var account in accounts)
            {
                var response_payload = new OraclePayload()
                {
                    OraclePub = account.Key.PublicKey,
                    RequestTxHash = task.requestTxHash,
                    ResponseTxSignature = responseTx.Sign(account.Key),
                };

                var signatureMsg = response_payload.Sign(account.Key);
                var signPayload = new ContractParametersContext(response_payload);

                if (signPayload.AddSignature(account.Contract, response_payload.OraclePub, signatureMsg) && signPayload.Completed)
                {
                    response_payload.Witnesses = signPayload.GetWitnesses();
                    task.request = request;
                    task.responseItems.Add(new ResponseItem(response_payload, responseTx));
                    oraclePostHandler.Tell(new AddOrUpdateOracleTask() { snapshot = snapshot, task = task });
                    Log($"Send oracle signature: oracle={response_payload.OraclePub} requestTx={task.requestTxHash} signaturePayload={response_payload.Hash}");
                    Self.Tell(new SendSignature() { payload = response_payload });
                }
            }
        }

        private Transaction CreateResponseTransaction(StoreView snapshot, OracleResponseAttribute response, Contract contract)
        {
            ScriptBuilder script = new ScriptBuilder();
            script.EmitAppCall(NativeContract.Oracle.Hash, "callback");
            var tx = new Transaction()
            {
                Version = 0,
                ValidUntilBlock = snapshot.Height + Transaction.MaxValidUntilBlockIncrement,
                Attributes = new TransactionAttribute[]{
                    new Cosigner()
                    {
                        Account = contract.ScriptHash,
                        AllowedContracts = new UInt160[]{ NativeContract.Oracle.Hash },
                        Scopes = WitnessScope.CalledByEntry
                    },
                    response
                },
                Sender = NativeContract.Oracle.Hash,
                Witnesses = new Witness[0],
                Script = script.ToArray(),
                NetworkFee = 0,
                Nonce = 0,
                SystemFee = 0
            };
            StorageKey storageKey = new StorageKey
            {
                Id = NativeContract.Oracle.Id,
                Key = new byte[sizeof(byte) + UInt256.Length]
            };
            storageKey.Key[0] = 21;
            response.RequestTxHash.ToArray().CopyTo(storageKey.Key.AsSpan(1));
            OracleRequest request = snapshot.Storages.GetAndChange(storageKey)?.GetInteroperable<OracleRequest>();
            request.Status = RequestStatusType.Ready;

            var state = new TransactionState
            {
                BlockIndex = snapshot.PersistingBlock.Index,
                Transaction = tx
            };
            snapshot.Transactions.Add(tx.Hash, state);
            var engine = ApplicationEngine.Run(tx.Script, snapshot, tx, testMode: true);
            if (engine.State != VMState.HALT) return null;
            tx.SystemFee = engine.GasConsumed;
            int size = tx.Size;
            tx.NetworkFee += Wallet.CalculateNetworkFee(contract.Script, ref size);
            tx.NetworkFee += size * NativeContract.Policy.GetFeePerByte(snapshot);
            return tx;
        }

        public void OnProcessOraclePayload(OraclePayload msg)
        {
            if (isStarted != 1) return;
            var snapshot = snapshotFactory();
            if (!msg.Verify(snapshot)) throw new Exception("Invailed Data");
            OracleRequest request = NativeContract.Oracle.GetRequest(snapshot, msg.RequestTxHash);
            if (request != null && request.Status != RequestStatusType.Request) throw new Exception("Request has been finished");
            if (isStarted == 1)
            {
                OracleTask task = new OracleTask(msg.RequestTxHash);
                task.responseItems.Add(new ResponseItem(msg));
                oraclePostHandler.Tell(new AddOrUpdateOracleTask() { snapshot = snapshot, task = task });
            }
        }

        public void OnSendSignature(OraclePayload payload)
        {
            new Task(() =>
            {
                foreach (var node in nodes)
                {
                    var url = new Uri("http://" + node + "/");
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);

                    request.Method = "POST";
                    request.ContentType = "application/json";
                    string data = payload.ToArray().ToHexString();
                    string strContent = "[{\"data\":\"" + data + "\"}]";
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
            }).Start();
        }

        public static void Log(string message, LogLevel level = LogLevel.Info)
        {
            Utility.Log(nameof(OracleService), level, message);
        }

        public static OracleResponseAttribute Process(OracleRequest request)
        {
            Uri.TryCreate(request.Url, UriKind.Absolute, out var uri);
            switch (uri.Scheme.ToLowerInvariant())
            {
                case "http":
                case "https":
                    return HTTPSProtocol.Process(request);
                default:
                    return CreateError(request.RequestTxHash);
            }
        }

        public static OracleResponseAttribute CreateError(UInt256 requestHash)
        {
            return CreateResult(requestHash, null, 0);
        }

        public static OracleResponseAttribute CreateResult(UInt256 requestTxHash, byte[] result, long filterCost)
        {
            return new OracleResponseAttribute()
            {
                RequestTxHash = requestTxHash,
                Data = result,
                FilterCost = filterCost
            };
        }

        protected override void OnReceive(object message)
        {
            switch (message)
            {
                case StartService start:
                    OnStart(start.wallet);
                    break;
                case StopService stop:
                    OnStop();
                    break;
                case ProcessRequest request:
                    new Task(() => OnProcessRequest(request.snapshot, request.tx));
                    break;
                case ProcessOraclePayload payload:
                    new Task(() => OnProcessOraclePayload(payload.payload));
                    break;
                case SendSignature sendSignature:
                    OnSendSignature(sendSignature.payload);
                    break;
            }
        }

        public static Props Props(IActorRef postHanler, string[] nodes)
        {
            return Akka.Actor.Props.Create(() => new OracleService(postHanler, nodes)).WithMailbox("OraclePreHandler-mailbox");
        }
    }
}

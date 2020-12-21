using Akka.Actor;
using Akka.TestKit.Xunit2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.IO;
using Neo.IO.Json;
using Neo.Network.P2P.Payloads;
using Neo.Plugins.FSStorage.innerring;
using Neo.Plugins.FSStorage.morph.invoke;
using Neo.SmartContract;
using Neo.VM;
using Neo.Wallets.NEP6;
using System;
using System.Linq;
using static Neo.Plugins.FSStorage.innerring.InnerRingService;

namespace Neo.Plugins.FSStorage.morph.client.Tests
{
    [TestClass()]
    public class InnerRingServiceTests : TestKit
    {
        private NeoSystem system;
        private NEP6Wallet wallet;
        private IActorRef innerring;
        private Client client;

        [TestInitialize]
        public void TestSetup()
        {
            system = TestBlockchain.TheNeoSystem;
            wallet = TestBlockchain.wallet;
            client = new MorphClient()
            {
                Wallet = wallet,
                Blockchain = TestActor
            };
            innerring = system.ActorSystem.ActorOf(InnerRingService.Props(system, wallet, client, client));
        }

        [TestMethod()]
        public void InitConfigAndContractEventTest()
        {
            innerring.Tell(new InnerRingService.Start());
            //create notify
            var tx = new Transaction()
            {
                Attributes = Array.Empty<TransactionAttribute>(),
                NetworkFee = 0,
                Nonce = 0,
                Script = new byte[] { 0x01 },
                Signers = new Signer[] { new Signer() { Account = wallet.GetAccounts().ToArray()[0].ScriptHash } },
                SystemFee = 0,
                ValidUntilBlock = 0,
                Version = 0,
            };
            var data = new ContractParametersContext(tx);
            wallet.Sign(data);
            tx.Witnesses = data.GetWitnesses();
            JArray obj = new JArray();
            obj.Add(tx.ToArray().ToHexString());
            obj.Add(UInt160.Zero.ToArray().ToHexString());
            obj.Add("test");
            obj.Add(new JArray(new VM.Types.Boolean(true).ToJson()));
            NotifyEventArgs notify = FSStorage.GetNotifyEventArgsFromJson(obj);
            innerring.Tell(new MainContractEvent() { notify=notify});
            ExpectNoMsg();
            innerring.Tell(new MorphContractEvent() { notify = notify });
            ExpectNoMsg();
            innerring.Tell(new InnerRingService.Stop());
        }
    }
}

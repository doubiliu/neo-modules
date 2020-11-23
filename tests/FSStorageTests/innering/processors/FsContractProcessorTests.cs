using Akka.Actor;
using Akka.TestKit.Xunit2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Cryptography;
using Neo.IO;
using Neo.Plugins.FSStorage.innerring.processors;
using Neo.Wallets;
using System.Collections.Generic;
using System.Linq;
using static Neo.Plugins.FSStorage.morph.invoke.Tests.BalanceContractProcessorTests;
using static Neo.Plugins.FSStorage.MorphEvent;
using Neo.Cryptography.ECC;
using Neo.Plugins.util;
using FSStorageTests.innering.processors;

namespace Neo.Plugins.FSStorage.morph.invoke.Tests
{
    [TestClass()]
    public class FsContractProcessorTests : TestKit
    {
        private NeoSystem system;
        private FsContractProcessor processor;
        private MorphClient morphclient;
        private Wallet wallet;

        [TestInitialize]
        public void TestSetup()
        {
            system = TestBlockchain.TheNeoSystem;
            wallet = TestBlockchain.wallet;
            morphclient = new MorphClient()
            {
                Wallet = wallet,
                Blockchain = system.ActorSystem.ActorOf(Props.Create(() => new ProcessorFakeActor()))
            };
            processor = new FsContractProcessor()
            {
                Client = morphclient,
                Convert = new Fixed8ConverterUtil(),
                ActiveState = new PositiveActiveState(),
                EpochState = new EpochState(),
                WorkPool = system.ActorSystem.ActorOf(Props.Create(() => new ProcessorFakeActor()))
            };
        }

        [TestMethod()]
        public void HandleDepositTest()
        {
            processor.HandleDeposit(new DepositEvent()
            {
                Id = new byte[] { 0x01 },
                Amount = 0,
                From = UInt160.Zero,
                To = UInt160.Zero
            });
            var nt = ExpectMsg<ProcessorFakeActor.OperationResult2>().nt;
            Assert.IsNotNull(nt);
        }

        [TestMethod()]
        public void HandleWithdrawTest()
        {
            processor.HandleWithdraw(new WithdrawEvent()
            {
                Id = new byte[] { 0x01 },
                Amount = 0,
                UserAccount = UInt160.Zero
            });
            var nt = ExpectMsg<ProcessorFakeActor.OperationResult2>().nt;
            Assert.IsNotNull(nt);
        }

        [TestMethod()]
        public void HandleChequeTest()
        {
            processor.HandleCheque(new ChequeEvent()
            {
                Id = new byte[] { 0x01 },
                Amount = 0,
                UserAccount = UInt160.Zero,
                LockAccount = UInt160.Zero
            });
            var nt = ExpectMsg<ProcessorFakeActor.OperationResult2>().nt;
            Assert.IsNotNull(nt);
        }

        [TestMethod()]
        public void HandleConfigTest()
        {
            processor.HandleConfig(new ConfigEvent()
            {
                Key = new byte[] { 0x01 },
                Value = new byte[] { 0x01 }
            });
            var nt = ExpectMsg<ProcessorFakeActor.OperationResult2>().nt;
            Assert.IsNotNull(nt);
        }

        [TestMethod()]
        public void HandleUpdateInnerRingTest()
        {
            processor.HandleUpdateInnerRing(new UpdateInnerRingEvent()
            {
                Keys = new Cryptography.ECC.ECPoint[0]
            });
            var nt = ExpectMsg<ProcessorFakeActor.OperationResult2>().nt;
            Assert.IsNotNull(nt);
        }

        [TestMethod()]
        public void ProcessDepositTest()
        {
            IEnumerable<WalletAccount> accounts = wallet.GetAccounts();
            processor.ProcessDeposit(new DepositEvent()
            {
                Id = new byte[] { 0x01 },
                Amount = 0,
                From = UInt160.Zero,
                To = accounts.ToArray()[0].ScriptHash
            });
            var tx = ExpectMsg<ProcessorFakeActor.OperationResult1>().tx;
            Assert.IsNotNull(tx);
        }

        [TestMethod()]
        public void ProcessWithdrawTest()
        {
            processor.ProcessWithdraw(new WithdrawEvent()
            {
                Id = UInt160.Zero.ToArray(),
                Amount = 0,
                UserAccount = UInt160.Zero
            });
            var tx = ExpectMsg<ProcessorFakeActor.OperationResult1>().tx;
            Assert.IsNotNull(tx);
        }

        [TestMethod()]
        public void ProcessChequeTest()
        {
            processor.ProcessCheque(new ChequeEvent()
            {
                Id = new byte[] { 0x01 },
                Amount = 0,
                UserAccount = UInt160.Zero,
                LockAccount = UInt160.Zero
            });
            var tx = ExpectMsg<ProcessorFakeActor.OperationResult1>().tx;
            Assert.IsNotNull(tx);
        }

        [TestMethod()]
        public void ProcessConfigTest()
        {
            processor.ProcessConfig(new ConfigEvent()
            {
                Id = new byte[] { 0x01 },
                Key = Utility.StrictUTF8.GetBytes("ContainerFee"),
                Value = new byte[] { 0x01 }
            });
            var tx = ExpectMsg<ProcessorFakeActor.OperationResult1>().tx;
            Assert.IsNotNull(tx);
        }

        [TestMethod()]
        public void ProcessUpdateInnerRingTest()
        {
            IEnumerable<WalletAccount> accounts = wallet.GetAccounts();
            KeyPair key = accounts.ToArray()[0].GetKey();
            processor.ProcessUpdateInnerRing(new UpdateInnerRingEvent()
            {
                Keys = new ECPoint[] { key.PublicKey }
            });
            var tx = ExpectMsg<ProcessorFakeActor.OperationResult1>().tx;
            Assert.IsNotNull(tx);
        }

        [TestMethod()]
        public void ListenerHandlersTest()
        {
            var handlerInfos = processor.ListenerHandlers();
            Assert.AreEqual(handlerInfos.Length, 5);
        }

        [TestMethod()]
        public void ListenerParsersTest()
        {
            var parserInfos = processor.ListenerParsers();
            Assert.AreEqual(parserInfos.Length, 5);
        }

        [TestMethod()]
        public void ListenerTimersHandlersTest()
        {
            var handlerInfos = processor.TimersHandlers();
            Assert.AreEqual(0, handlerInfos.Length);
        }

        public class EpochState : IEpochState
        {
            public ulong EpochCounter()
            {
                return 0; ;
            }

            public void SetEpochCounter(ulong epoch)
            {
                return;
            }
        }
    }
}

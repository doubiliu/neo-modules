using Akka.Actor;
using Akka.TestKit.Xunit2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.IO;
using Neo.IO.Json;
using Neo.Network.P2P.Payloads;
using Neo.Plugins.FSStorage.innerring.processors;
using Neo.SmartContract;
using Neo.VM;
using Neo.Wallets;
using System;
using System.Linq;

namespace Neo.Plugins.FSStorage.morph.client.Tests
{
    [TestClass()]
    public class ListenerTests : TestKit, IProcessor
    {
        private IActorRef listener;
        private Wallet wallet;
        private string name= "Testlistener";
        public string Name { get => name; set => name=value; }

        [TestInitialize]
        public void TestSetup()
        {
            wallet = TestBlockchain.wallet;
            listener = Sys.ActorOf(Listener.Props("Testlistener"));
        }

        [TestMethod()]
        public void OnStartAndOnStopAndNewContractEventTest()
        {
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
            //send notify with no handler and parser
            listener.Tell(new Listener.Start());
            listener.Tell(new Listener.NewContractEvent()
            {
                notify = notify
            });
            ExpectNoMsg();
            listener.Tell(new Listener.Stop());
            //bind handler and parser
            listener.Tell(new Listener.BindProcessorEvent() { processor = this });
            listener.Tell(new Listener.Start());
            //bind handler and parser on starting
            listener.Tell(new Listener.BindProcessorEvent() { processor = this });
            //send normal notify 
            listener.Tell(new Listener.NewContractEvent()
            {
                notify = notify
            });
            var result = (TestContractEvent)ExpectMsg<IContractEvent>();
            Assert.IsNotNull(result);
            Assert.AreEqual(result.current, 1);
            result = (TestContractEvent)ExpectMsg<IContractEvent>();
            Assert.IsNotNull(result);
            Assert.AreEqual(result.current, 2);
            ExpectNoMsg();
            //send notify with no state
            JArray obj_no_state = new JArray();
            obj_no_state.Add(tx.ToArray().ToHexString());
            obj_no_state.Add(UInt160.Zero.ToString());
            obj_no_state.Add("test");
            obj_no_state.Add(new JArray());
            NotifyEventArgs notify_no_state = FSStorage.GetNotifyEventArgsFromJson(obj_no_state);
            listener.Tell(new Listener.NewContractEvent()
            {
                notify = notify_no_state
            });
            ExpectNoMsg();
            //send notify with no parser
            JArray obj_no_parser = new JArray();
            obj_no_parser.Add(tx.ToArray().ToHexString());
            obj_no_parser.Add(wallet.GetAccounts().ToArray()[0].ScriptHash.ToString());
            obj_no_parser.Add("test");
            obj_no_parser.Add(new JArray(new VM.Types.Boolean(true).ToJson()));
            NotifyEventArgs notify_no_parser = FSStorage.GetNotifyEventArgsFromJson(obj_no_state);
            listener.Tell(new Listener.NewContractEvent()
            {
                notify = notify_no_parser
            });
            ExpectNoMsg();
            //send notify with no handler
            JArray obj_no_handler = new JArray();
            obj_no_handler.Add(tx.ToArray().ToHexString());
            obj_no_handler.Add(UInt160.Zero.ToString());
            obj_no_handler.Add("test with no handler");
            obj_no_handler.Add(new JArray(new VM.Types.Boolean(true).ToJson()));
            NotifyEventArgs notify_no_handler = FSStorage.GetNotifyEventArgsFromJson(obj_no_handler);
            listener.Tell(new Listener.NewContractEvent()
            {
                notify = notify_no_handler
            });
            ExpectNoMsg();
            //send wrong format notify
            JArray obj_wrong_format = new JArray();
            obj_wrong_format.Add(tx.ToArray().ToHexString());
            obj_wrong_format.Add(UInt160.Zero.ToString());
            obj_wrong_format.Add("test");
            var state = new JArray();
            state.Add(new VM.Types.Boolean(true).ToJson());
            state.Add(new VM.Types.Boolean(true).ToJson());
            obj_wrong_format.Add(state);
            NotifyEventArgs notify_wrong_format = FSStorage.GetNotifyEventArgsFromJson(obj_wrong_format);
            listener.Tell(new Listener.NewContractEvent()
            {
                notify = notify_wrong_format
            });
            ExpectNoMsg();
            //stop
            listener.Tell(new Listener.Stop());
        }

        public ParserInfo[] ListenerParsers()
        {
            //both handler and parser
            ParserInfo parserInfo1 = new ParserInfo()
            {
                ScriptHashWithType = new ScriptHashWithType()
                {
                    Type = "test",
                    ScriptHashValue = UInt160.Zero
                },
                Parser = ParseContractEvent
            };
            //parser is null
            ParserInfo parserInfo2 = new ParserInfo()
            {
                ScriptHashWithType = new ScriptHashWithType()
                {
                    Type = "parser is null",
                    ScriptHashValue = UInt160.Zero
                },
                Parser = null
            };
            //parser with no handler
            ParserInfo parserInfo3 = new ParserInfo()
            {
                ScriptHashWithType = new ScriptHashWithType()
                {
                    Type = "test with no handler",
                    ScriptHashValue = UInt160.Zero
                },
                Parser = ParseContractEvent
            };
            return new ParserInfo[] { parserInfo1, parserInfo2, parserInfo3 };
        }

        public HandlerInfo[] ListenerHandlers()
        {
            //both handler and parser
            HandlerInfo handlerInfo1 = new HandlerInfo()
            {
                ScriptHashWithType = new ScriptHashWithType()
                {
                    Type = "test",
                    ScriptHashValue = UInt160.Zero
                },
                Handler = F
            };
            //no handler
            HandlerInfo handlerInfo2 = new HandlerInfo()
            {
                ScriptHashWithType = new ScriptHashWithType()
                {
                    Type = "test",
                    ScriptHashValue = UInt160.Zero
                },
                Handler = null
            };
            //no parser
            HandlerInfo handlerInfo3 = new HandlerInfo()
            {
                ScriptHashWithType = new ScriptHashWithType()
                {
                    Type = "test no parser",
                    ScriptHashValue = UInt160.Zero
                },
                Handler = F
            };
            //double handler
            HandlerInfo handlerInfo4 = new HandlerInfo()
            {
                ScriptHashWithType = new ScriptHashWithType()
                {
                    Type = "test",
                    ScriptHashValue = UInt160.Zero
                },
                Handler = F
            };
            return new HandlerInfo[] { handlerInfo1, handlerInfo2, handlerInfo3, handlerInfo4};
        }

        public HandlerInfo[] TimersHandlers()
        {
            return null;
        }

        private void F(IContractEvent contractEvent)
        {
            var testEvent = new TestContractEvent();
            TestContractEvent.count++;
            testEvent.current = TestContractEvent.count;
            TestActor.Tell(testEvent);
        }

        public IContractEvent ParseContractEvent(VM.Types.Array eventParams)
        {
            if (eventParams.Count != 1) throw new Exception();
            return new TestContractEvent();
        }

        public string GetName()
        {
            return "Listener test"; ;
        }

        public class TestContractEvent : IContractEvent
        {
            public int current;
            public static int count;
            public void ContractEvent()
            {
            }
        }
    }
}

using Akka.Actor;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo;
using Neo.Plugins;
using static Neo.Ledger.Blockchain;

namespace OracleTracker.Tests
{
    [TestClass()]
    public class OracleTrackerTests
    {
        private NeoSystem neoSystem;
        private IActorRef postHandler;
        private IActorRef oracleService;

        [TestInitialize]
        public void Setup()
        {
            TestBlockchain.InitializeMockNeoSystem();
            neoSystem = TestBlockchain.TheNeoSystem;
            TestOracleTracker tracker = new TestOracleTracker();
            postHandler = neoSystem.ActorSystem.ActorOf(OraclePostHandler.Props(neoSystem.Blockchain));
            oracleService = neoSystem.ActorSystem.ActorOf(OracleService.Props(postHandler,new string[] { }));
            tracker.oracleService = oracleService;
            tracker.postHandler = postHandler;
        }

        [TestMethod()]
        public void OracleTrackerTest()
        {
            //Assert.Fail();
            System.Console.WriteLine("start");
        }

        [TestMethod()]
        public void OnPersistTest()
        {
            Assert.Fail();
        }

        [TestMethod()]
        public void SubmitOracleSignaturesTest()
        {
            Assert.Fail();
        }

        [TestMethod()]
        public void SendMessageTest()
        {
            Assert.Fail();
        }

        [TestMethod()]
        public void SendMessageTest1()
        {
            Assert.Fail();
        }

        [TestMethod()]
        public void StartOracleTest()
        {
            Assert.Fail();
        }

        [TestMethod()]
        public void StopOracleTest()
        {
            Assert.Fail();
        }

        public class TestOracleTracker : OracleTracker
        {
            protected override void Configure() {

            }
        }
    }
}

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo;

namespace OracleTrackerTests
{
    class OracleTrackerTests
    {
        private NeoSystem neoSystem;
        private OracleTracker.OracleTracker tracker;
        private OracleTracker.OracleService service;

        [TestInitialize]
        public void Setup()
        {
            neoSystem = TestBlockchain.TheNeoSystem;
            tracker = new OracleTracker.OracleTracker(neoSystem.Blockchain);
            service = tracker.service;
        }

        [TestMethod]
        public void TestStartAndStop()
        {
            service.Start(null);
            service.Stop();
        }
    }
}

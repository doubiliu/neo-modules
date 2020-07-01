using Akka.Actor;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo;
using Neo.Ledger;
using Neo.Oracle;
using Neo.SmartContract;
using Neo.SmartContract.Native;
using Neo.VM;
using Neo.Wallets;
using Neo.Wallets.NEP6;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Akka.TestKit.Xunit2;
using Neo.Wallets.SQLite;

namespace OracleTrackerTests
{
    [TestClass]
    public class OracleTrackerTests
    {
        private static OracleTracker.OracleTracker tracker;
        private static OracleTracker.OracleService service;
        private static Wallet wallet;
        private static KeyPair[] _accounts;

        [ClassInitialize]
        public static void TestSetup(TestContext ctx)
        {
            tracker = new TestOracleTracker();
            service = new OracleTracker.OracleService(tracker, 1000);

            wallet = UserWallet.Create("./ test.db3", "123456",ScryptParameters.Default);
            for (int i = 0; i < 7; i++)
            {
                byte[] privateKey = new byte[32];
                using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(privateKey);
                }
                KeyPair key = new KeyPair(privateKey);
                wallet.CreateAccount(privateKey);
            }
            _accounts = wallet?.GetAccounts().Where(u => u.HasKey && !u.Lock).Select(u => u.GetKey()).ToArray();
            var pubkeys = _accounts.Select(p => p.PublicKey).ToArray();
            var snapshot = Blockchain.Singleton.GetSnapshot();
            var script = new ScriptBuilder();
            script.EmitAppCall(NativeContract.Oracle.Hash, "setOracleValidators", pubkeys);
            UInt160 committeeAddress = NativeContract.NEO.GetCommitteeAddress(snapshot);
            var engine = new ApplicationEngine(TriggerType.Application, new ManualWitness(committeeAddress), snapshot, 0, true);
            engine.LoadScript(script.ToArray());
            engine.Execute();
        }

        [TestMethod]
        public void TestStartAndStop()
        {
            service.Start(wallet,4);
            service.Stop();
        }

        public class TestOracleTracker : OracleTracker.OracleTracker
        {
            public override void SendMessage(Blockchain.RelayResult relayResult)
            {
                
            }
        }
    }
}

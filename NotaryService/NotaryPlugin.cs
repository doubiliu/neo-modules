using Akka.Actor;
using Neo.ConsoleService;
using Neo.Network.P2P;
using Neo.Plugins;
using Neo.Wallets;
using Settings = Neo.Plugins.Settings;

namespace Neo.Consensus
{
    public class NotaryPlugin : Plugin
    {
        private IActorRef notary;
        private IWalletProvider walletProvider;
        private bool started = false;
        private NeoSystem neoSystem;
        private Settings settings;

        public NotaryPlugin() { }

        public NotaryPlugin(Settings settings)
        {
            this.settings = settings;
        }

        public override string Description => "Notary plugin";

        protected override void Configure()
        {
            if (settings == null) settings = new Settings(GetConfiguration());
        }

        protected override void OnSystemLoaded(NeoSystem system)
        {
            if (system.Settings.Network != settings.Network) return;
            neoSystem = system;
            neoSystem.ServiceAdded += NeoSystem_ServiceAdded;
        }

        private void NeoSystem_ServiceAdded(object sender, object service)
        {
            if (service is IWalletProvider)
            {
                walletProvider = service as IWalletProvider;
                neoSystem.ServiceAdded -= NeoSystem_ServiceAdded;
                if (settings.AutoStart)
                {
                    walletProvider.WalletChanged += WalletProvider_WalletChanged;
                }
            }
        }

        private void WalletProvider_WalletChanged(object sender, Wallet wallet)
        {
            walletProvider.WalletChanged -= WalletProvider_WalletChanged;
            Start(wallet);
        }

        [ConsoleCommand("start notary", Category = "Notary", Description = "Start notary service")]
        private void OnStart()
        {
            Start(walletProvider.GetWallet());
        }

        public void Start(Wallet wallet)
        {
            if (started) return;
            started = true;
            notary = neoSystem.ActorSystem.ActorOf(NotaryService.Props(neoSystem, settings, wallet));
            notary.Tell(new NotaryService.Start());
        }
    }
}

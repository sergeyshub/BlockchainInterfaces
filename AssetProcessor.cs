using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assets.Crypto;
using DataAccess;
using ConfigAccess;
using LogAccess;
using Assets.Fiat.Water;

namespace Assets
{
    public static class AssetProcessor
    {
        private const int INITIAL_PAUSE = 60 * 1000;                    // milliseconds

        private static int CoreManagerInterval;

        public static void Initialize()
        {
            try
            {
                CoreManagerInterval = Config.GetInt("CryptoAssets:BalanceCheckInterval", 10) * 60 * 1000;

                Task.Run(() => StartCoreManager());

                using (var dbContext = Database.GetContext())
                {
                    var cores = dbContext.Core.Where(a => a.Status == Database.STATUS_ACTIVE && a.Node != null).ToList();

                    foreach (var core in cores)
                    {
                        if (Config.GetBool($"CryptoAssets:{core.Node}:On", false))
                        {
                            var cryptoCore = (VirtualCryptoCore)CoreFactory.Create(core);
                            cryptoCore.StartProcesses();
                        }
                    }

                    // Water is special case
                    var water = dbContext.Core.Where(a => a.Name == "WTR" && a.Status == Database.STATUS_ACTIVE).FirstOrDefault();
                    if (water != null && Config.GetBool($"Water:On", false))
                    {
                        var cryptoCore = (VirtualWaterCore)CoreFactory.Create(water);
                        cryptoCore.StartProcesses();
                    }

                }
            }
            catch (Exception e)
            {
                Log.Error($"AssetProcessor.Initialize failed. Error: \"{e.Message}\"");
            }
        }

        private static void StartCoreManager()
        {
            Thread.Sleep(INITIAL_PAUSE);

            while (true)
            {
                new AssetCoreManager().CheckAssets();
                Thread.Sleep(CoreManagerInterval);
            }
        }
    }
}
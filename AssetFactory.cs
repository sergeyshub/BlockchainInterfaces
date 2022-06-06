using System;
using System.Linq;
using DataAccess;
using Assets.Crypto;
using Assets.Fiat.Water;

namespace Assets
{
    /// <summary>
    /// Class producing assets based on asset code, i.e. "usd" or "btc".
    /// </summary>
    public class AssetFactory
    {
        private DatabaseContext DbContext;

        public AssetFactory(DatabaseContext dbContext)
        {
            DbContext = dbContext;
        }

        /// <summary>
        /// Create crypto asset
        /// </summary>
        /// <remarks>
        /// Creates an asset on primary core based on the asset code, i.e. "USD" or "BTC".
        /// </remarks>
        /// <param name="assetCode">Asset code</param>
        /// <returns>Asset</returns>   
        public ProtoAsset CreateAsset(string assetCode)
        {
            var assetData = DbContext.Asset.FirstOrDefault(a => a.Code == assetCode && a.Status == Database.STATUS_ACTIVE);
            if (assetData == null) throw new ApplicationException($"Crypto asset code '{assetCode}' not found.");

            if (assetData.IsCrypto)
            {
                var core = (VirtualCryptoCore)CoreFactory.Create(DbContext, assetData.CoreType);
                return new CryptoAsset(DbContext, assetCode, core);
            }
            else
            {
                return new WaterFiatAsset(DbContext, assetCode);
            }
        }
    }
}

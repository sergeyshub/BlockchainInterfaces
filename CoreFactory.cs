using System;
using System.Linq;
using Assets.Crypto.Bitcoin;
using Assets.Crypto.Litecoin;
using Assets.Crypto.Dash;
using Assets.Crypto.Ethereum;
using Assets.Fiat.Water;
using Assets.Crypto.Ripple;
using DataAccess;

namespace Assets
{
    /// <summary>
    /// Class producing cores based on core code, i.e. "BTC" or "ETH".
    /// </summary>
    public static class CoreFactory
    {
        /// <summary>
        /// Create crypto core
        /// </summary>
        /// <remarks>
        /// Creates a core based on the database core object.
        /// </remarks>
        /// <param name="Core">Core</param>
        /// <returns>Core</returns>   
        public static VirtualCore Create(Core core)
        {
            if (string.IsNullOrEmpty(core.Type)) throw new ApplicationException($"Core.Type is empty.");

            switch (core.Type)
            {
            }
        }

        /// <summary>
        /// Create crypto core
        /// </summary>
        /// <remarks>
        /// Creates a primary core based on core type, i.e. "USD" or "BTC".
        /// </remarks>
        /// <param name="coreType">Core type</param>
        /// <returns>Core</returns>   
        public static VirtualCore Create(DatabaseContext dbContext, string coreType)
        {
            if (string.IsNullOrEmpty(coreType)) return null;

            var coreData = dbContext.Core.Where(a => a.Type == coreType && a.IsPrimary && a.Status == Database.STATUS_ACTIVE).FirstOrDefault();
            if (coreData == null) throw new ApplicationException($"The active primary core for core type '{coreType}' is not found.");

            return Create(coreData);
        }
    }
}

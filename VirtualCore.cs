using System;
using System.Linq;
using DataAccess;
using MessageLib;
using LogAccess;

namespace Assets
{
    public class CoreSendReceiveResult
    {
        public const byte STATUS_SUCCEESS = 1;
        public const byte STATUS_BALANCE = 2;
        public const byte STATUS_FAILED = 3;

        public byte Status;
        public string Txid;
        public decimal Amount;
        public decimal Fee;
    }

    /// <summary>
    /// Abstract class representing an asset core.
    /// </summary>
    /// <remarks>
    /// Contains functions to work with a core.
    /// </remarks>
    public abstract class VirtualCore
    {
        public string Name;
        public string Type;
        public bool IsExchange;

        protected Core DataCore;
        protected Asset MainAsset;

        public VirtualCore(string coreName)
        {
            Name = coreName;

            using (var dbContext = Database.GetContext())
            {
                DataCore = dbContext.Core.FirstOrDefault(a => a.Name == Name);
                if (DataCore == null) throw new ApplicationException($"Core name {Name} does not exist.");

                IsExchange = DataCore.IsExchange;

                MainAsset = ConfigData.GetMainAsset(dbContext);
            }
        }

        public Core GetDataCore(DatabaseContext dbContext)
        {
            var dataCore = dbContext.Core.Find(DataCore.Id);
            return dataCore;
        }
    }
}

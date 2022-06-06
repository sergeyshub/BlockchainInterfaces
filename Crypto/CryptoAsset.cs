using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using DataAccess;

namespace Assets.Crypto
{
    public class CryptoAsset : ProtoAsset
    {
        protected VirtualCryptoCore Core;

        public CryptoAsset(DatabaseContext dbContext, string assetCode, VirtualCryptoCore core) : base(dbContext, assetCode)
        {
            Core = core;
        }

        public override ProtoAccount GetAccount(int id)
        {
            var account = DbContext.Account.Where(a => a.Id == id).Include(a => a.Asset).FirstOrDefault();

            if (account.Asset != Asset) throw new ApplicationException($"Account id {id} is for a different asset.");

            return new CryptoAccount(DbContext, Core, account);
        }

        public bool ValidateAddress(string address)
        {
            return Core.ValidateAddress(address);
        }
    }
}

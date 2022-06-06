using System;
using System.Linq;
using System.Linq.Expressions;
using DataAccess;

namespace Assets
{
    public abstract class ProtoAsset
    {
        protected DatabaseContext DbContext;
        protected Asset Asset;

        public ProtoAsset(DatabaseContext dbContext, string assetCode)
        {
            DbContext = dbContext;
            Asset = DbContext.Asset.First(a => a.Code == assetCode);
        }

        public ProtoAccount CreateAccount(User user, Company company = null, string name = null, User createdBy = null)
        {
            var account = Asset.CreateAccount(DbContext, user, company, name, createdBy);
            var assetAccount = new ProtoAccount(DbContext, account);
            return assetAccount;
        }

        public abstract ProtoAccount GetAccount(int id);
    }
}

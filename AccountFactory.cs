using System;
using Assets.Crypto;
using Assets.Fiat.Water;
using DataAccess;

namespace Assets
{
    public static class AccountFactory
    {
        public static ProtoAccount Create(DatabaseContext dbContext, VirtualCore core, Account account)
        {
            if (account.Asset == null) dbContext.Entry(account).Reference(a => a.Asset).Load();

            if (account.Asset.IsCrypto) return new CryptoAccount(dbContext, (VirtualCryptoCore)core, account);

            switch (core.Type)
            {
            }
        }
    }
}

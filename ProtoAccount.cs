using System;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using DataAccess;
using DataModels;
using LiveUpdate;
using WebSocket;
using ConfigAccess;
using LogAccess;

namespace Assets
{
    public class ProtoAccount
    {
        protected DatabaseContext DbContext;
        protected Asset Asset;
        protected Asset MainAsset;
        public Account Account;

        public ProtoAccount(DatabaseContext dbContext, Account account)
        {
            DbContext = dbContext;
            Account = account;
            if (Account.Asset == null) dbContext.Entry(Account).Reference(a => a.Asset).Load();
            Asset = Account.Asset;
            MainAsset = ConfigData.GetMainAsset(dbContext);
        }

        public Balance GetBalance()
        {
            return new Balance(Account.BalanceTotal, Account.BalanceReserved, Account.BalancePending);
        }

        // This function needs to be executed only inside a transaction with a row lock on Account.
        // Updating BalanceTotal only.
        public void UpdateBalance(DateTime? startTime = null)
        {
            decimal balance;

            if (startTime == null) startTime = new DateTime(2000, 1, 1);

            var previousTransactionSum = DbContext.Transaction
                .Where(a => a.Account == Account && 
                            ((a.TimeExecuted == null ? a.TimeAdded : a.TimeExecuted)  < startTime) &&
                            ((a.Status == DataAccess.Database.STATUS_PENDING && 0 < a.Amount) || a.Status == DataAccess.Database.STATUS_SUCCESS))
                .Sum(s => s.Amount);

            balance = previousTransactionSum;

            var transactions = DbContext.Transaction
                .Where(a => a.Account == Account && ((a.TimeExecuted != null && startTime <= a.TimeExecuted) || (a.TimeExecuted == null && startTime <= a.TimeAdded)))
                .OrderBy(c => c.TimeExecuted == null ? c.TimeAdded : c.TimeExecuted).ThenBy(c => c.Id)
                .ToList();

            foreach (var transaction in transactions)
            {
                if (transaction.Status == DataAccess.Database.STATUS_SUCCESS
                    || (transaction.Status == DataAccess.Database.STATUS_PENDING && 0 < transaction.Amount))
                    balance += transaction.Amount;

                transaction.BalanceAfter = balance;
            }

            Account.BalanceTotal = balance;
            DbContext.SaveChanges();
            //Log.Warning($"-UpdateBalance for acc {Account.Id} finished");
        }

        public decimal CalcRealBalance()
        {
            var balance = DbContext.Transaction
                .Where(a => a.Account == Account && ((a.Status == DataAccess.Database.STATUS_PENDING && 0 < a.Amount) || a.Status == DataAccess.Database.STATUS_SUCCESS))
                .Sum(s => s.Amount);
            return balance;
        }

        public class MoveResult
        {
            public Transaction TransactionIn, TransactionOut;
        }

        public MoveResult Move(decimal amount, Account toAccount, User createdBy, TradeFill tradeFill = null)
        {
            IDbContextTransaction dbTransaction = null;

            var exRate = AssetQuoter.Get(Account.Asset, MainAsset);

            if (DbContext.Database.CurrentTransaction == null)
            {
                dbTransaction = DbContext.Database.BeginTransaction();

                DbContext.LockAndUpdate(ref Account);
                DbContext.LockAndUpdate(ref toAccount);
            }

            if (Account.BalanceTotal - Account.BalanceReserved - Account.BalancePending < amount)
                throw new ApplicationException($"Insufficient available balance on account {Account.Number} to move {amount}.");

            // No need to call ComputeBalance(), as these are the last transactions
            Account.BalanceTotal -= amount;
            toAccount.BalanceTotal += amount;

            var transactionOut = new Transaction();

            transactionOut.Account = Account;
            transactionOut.Asset = Asset;
            transactionOut.Amount = -amount;
            transactionOut.BalanceAfter = Account.BalanceTotal;
            transactionOut.ExRate = exRate;
            transactionOut.CreatedBy = createdBy;
            transactionOut.Number = Transaction.GetEntityNumber(DbContext);
            transactionOut.TimeExecuted = DateTime.Now;
            transactionOut.Type = (tradeFill == null) ? Transaction.TYPE_TRANSFER : Transaction.TYPE_TRADE;
            transactionOut.Status = DataAccess.Database.STATUS_SUCCESS;
            if (tradeFill != null) transactionOut.TradeFill = tradeFill;

            DbContext.Transaction.Add(transactionOut);

            var transactionIn = new Transaction();

            transactionIn.Account = toAccount;
            transactionIn.Asset = Asset; // moving the same asset
            transactionIn.Amount = amount;
            transactionIn.BalanceAfter = toAccount.BalanceTotal;
            transactionIn.ExRate = exRate;
            transactionIn.CreatedBy = createdBy;
            transactionIn.Reciprocal = transactionOut;
            transactionIn.Number = Transaction.GetEntityNumber(DbContext);
            transactionIn.TimeExecuted = DateTime.Now;
            transactionIn.Type = (tradeFill == null) ? Transaction.TYPE_TRANSFER : Transaction.TYPE_TRADE;
            transactionIn.Status = DataAccess.Database.STATUS_SUCCESS;
            if (tradeFill != null) transactionIn.TradeFill = tradeFill;

            DbContext.Transaction.Add(transactionIn);
            DbContext.SaveChanges();  // Need this Save to avoid the "circular dependency was detected" error

            transactionOut.Reciprocal = transactionIn;

            DbContext.SaveChanges();

            if (dbTransaction != null) dbTransaction.Commit();

            // WebSocket
            SocketPusher.SendAccountUpdate(DbContext, transactionOut);
            SocketPusher.SendAccountUpdate(DbContext, transactionIn);

            return new MoveResult{ TransactionIn = transactionIn, TransactionOut = transactionOut };
        }
    }
}

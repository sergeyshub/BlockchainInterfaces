using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using DataAccess;
using LiveUpdate;
using HelpersLib;
using MessageLib;
using LogAccess;

namespace Assets.Fiat
{
    /// <summary>
    /// Abstract class representing a fiat asset core.
    /// </summary>
    /// <remarks>
    /// Contains functions to work with fiat core.
    /// </remarks>
    public abstract class VirtualFiatCore : VirtualCore
    {
        protected const int TIME_INITIAL_PAUSE = 60 * 1000; // milliseconds

        protected int UpdateInterval, TimeSendInterval, TimeRecoveryDelay, TimeRetryBalance;

        public VirtualFiatCore(string coreName) : base(coreName)
        {
        }

        /// <summary>
        /// Get balance of an address
        /// </summary>
        /// <remarks>
        /// Checks the balance of the external account.
        /// 
        /// If ExternalId is null, returns the balance of the main account.
        /// 
        /// </remarks>
        /// <param name="asset">Asset</param>
        /// <param name="externalId">External account Id</param>
        /// <returns>Balance</returns>
        public abstract decimal GetBalance(DatabaseContext dbContext, Asset asset, string account = null);

        /// <summary>
        /// Get transaction information
        /// </summary>
        /// <remarks>
        /// Gets transaction transaction information.
        /// Throws an exception, if transaction does not exist.
        /// </remarks>
        /// <param name="txid">Txid</param>
        /// <returns>Transaction information</returns>
        abstract public VirtualFiatTx GetTxDetails(DatabaseContext dbContext, string txid);

        /// <summary>
        /// Get transactions
        /// </summary>
        /// <remarks>
        /// Retrieves a list of last transactions.
        /// </remarks>
        /// <param name="first">How many trx to skip from the end</param>
        /// <param name="number">Max number of transactions to fetch</param>
        /// <param name="account">Account</param>
        /// <returns>Transactions</returns>
        abstract public VirtualFiatTx[] GetTransactions(DatabaseContext dbContext, int first, int number, string account);

        /// <summary>
        /// Get fee
        /// </summary>
        /// <remarks>
        /// Calculates the fee to send a transaction with a given payment method.
        /// </remarks>
        /// <param name="amount">The amount to send. It is positive for deposits and negative for withdrawals. </param>
        /// <param name="paymentMethod">Payment Method</param>
        /// <param name="isSend">true if send, false if receive</param>
        /// <returns>Fee</returns>
        abstract public decimal GetFee(DatabaseContext dbContext, decimal amount, PaymentMethod paymentMethod);

        /// <summary>
        /// Send amount with a payment method
        /// </summary>
        /// <remarks>
        /// Sends the specified amount with a payment method.
        /// </remarks>
        /// <param name="dbContext">Database context</param>
        /// <param name="transactionFiat">Transaction to send</param>
        /// <returns>Send result</returns>
        abstract public CoreSendReceiveResult Send(DatabaseContext dbContext, TransactionFiat transactionFiat);

        /// <summary>
        /// Get funds from a payment method
        /// </summary>
        /// <remarks>
        /// Charges the specified amount with a payment method.
        /// </remarks>
        /// <param name="dbContext">Database context</param>
        /// <param name="transactionFiat">Transaction to receive</param>
        /// <returns>Send result</returns>
        abstract public CoreSendReceiveResult ReceivePull(DatabaseContext dbContext, TransactionFiat transactionFiat);

        virtual public void ProcessTransactionFailure(DatabaseContext dbContext, TransactionFiat transactionFiat)
        {
            Notifier.SendTransactionFailureMessage(dbContext, transactionFiat);
        }

        public void CancelTransaction(DatabaseContext dbContext, TransactionFiat transactionFiat, byte status = Database.STATUS_CANCELED)
        {
            Account account = null;
            FiatAccount fiatAccount = null;

            var dbTransaction = dbContext.Database.BeginTransaction();

            dbContext.LockAndUpdate(ref transactionFiat);

            if (transactionFiat.Status != Database.STATUS_NEW
                && transactionFiat.Status != Database.STATUS_PENDING_ADMIN)
                throw new ApplicationException($"Cannot cancel fiat transaction {transactionFiat.Id}. This status is {transactionFiat.Status}.");

            transactionFiat.Status = status;

            if (transactionFiat.Account != null)
            {
                account = transactionFiat.Account;
                fiatAccount = (FiatAccount)AccountFactory.Create(dbContext, this, account);
                fiatAccount.CancelTransaction(transactionFiat, status);
            }

            FailSystemTransactions(dbContext, transactionFiat);

            dbContext.SaveChanges();
            dbTransaction.Commit();
        }

        protected ulong GetLastSyncBlock(DatabaseContext dbContext)
        {
            var core = GetDataCore(dbContext);
            return (core.LastSyncBlock == null) ? 0 : (ulong)core.LastSyncBlock;
        }

        protected void SetLastSyncBlock(DatabaseContext dbContext, ulong lastSyncBlock)
        {
            var core = GetDataCore(dbContext);
            core.LastSyncBlock = lastSyncBlock;
            dbContext.SaveChanges();
        }

        protected void SetSyncTime(DatabaseContext dbContext)
        {
            var core = GetDataCore(dbContext);
            core.TimeSynced = DateTime.Now;
            dbContext.SaveChanges();
        }

        virtual public void StartProcesses()
        {
            Task.Run(() => StartSendingReceiving());
            Task.Run(() => StartSynchronization());
            Task.Run(() => StartConfirmation());
            Task.Run(() => StartRecovery());
        }

        private void StartSynchronization()
        {
            Thread.Sleep(TIME_INITIAL_PAUSE);

            Log.Information($"{Name} core sync started");

            while (true)
            {
                SyncTransactions();
                Thread.Sleep(UpdateInterval);
            }
        }

        private void StartSendingReceiving()
        {
            Thread.Sleep(TIME_INITIAL_PAUSE);

            while (true)
            {
                SendReceiveTransactions();
                Thread.Sleep(TimeSendInterval);
            }
        }

        private void StartConfirmation()
        {
            Thread.Sleep(TIME_INITIAL_PAUSE);

            while (true)
            {
                ConfirmTransactions();
                Thread.Sleep(UpdateInterval);
            }
        }

        private void StartRecovery()
        {
            Thread.Sleep(TIME_INITIAL_PAUSE);

            while (true)
            {
                RecoverTransactions();
                Thread.Sleep(UpdateInterval);
            }
        }

        protected abstract void SyncTransactions();

        protected void SendReceiveTransactions()
        {
            const int BATCH_SIZE = 1000;

            try
            {
                Log.Information($"VirtualFiatCore.SendReceiveTransactions");

                using (var dbContext = Database.GetContext())
                {
                    var transactionFiats = dbContext.TransactionFiat.Where(a => a.Core == DataCore && a.Status == Database.STATUS_NEW && (a.TimeRetry == null || a.TimeRetry <= DateTime.Now))
                        .Include(a => a.Account).Include(a => a.Asset)
                        .Take(BATCH_SIZE)
                        .ToList();

                    foreach (var transactionFiat in transactionFiats) SendReceiveTransaction(dbContext, transactionFiat);
                }
            }
            catch (Exception e)
            {
                Log.Error($"VirtualFiatCore.SendReceiveTransactions failed. Error: \"{e.Message}\"");
            }
        }

        protected void ConfirmTransactions()
        {
            const int BATCH_SIZE = 1000;

            try
            {
                // Log.Information($"VirtualFiatCore.ConfirmTransactions");

                using (var dbContext = Database.GetContext())
                {
                    var transactionFiats = dbContext.TransactionFiat.Where(a => a.Core == DataCore && a.Status == Database.STATUS_PENDING)
                        .Include(a => a.Account).Include(a => a.Asset)
                        .Take(BATCH_SIZE)
                        .ToList();

                    foreach (var transactionFiat in transactionFiats) ConfirmTransaction(dbContext, transactionFiat);
                }
            }
            catch (Exception e)
            {
                Log.Error($"VirtualFiatCore.ConfirmTransactions failed. Error: \"{e.Message}\"");
            }
        }

        protected void RecoverTransactions()
        {
            try
            {
                using (var dbContext = Database.GetContext())
                {
                    if (DataCore.TimeSynced == null) return;
                    var TimeSynced = (DateTime)DataCore.TimeSynced;

                    var transactionFiats = dbContext.TransactionFiat.Where(a => a.Core == DataCore && a.Status == Database.STATUS_ACTIVE && a.TimeExecuted < TimeSynced.AddMilliseconds(-TimeRecoveryDelay))
                        .OrderBy(a => a.TimeExecuted)
                        .ThenBy(a => a.Id)
                        .Include(a => a.Account).Include(a => a.Asset)
                        .ToList();

                    foreach (var transactionFiat in transactionFiats)
                    {
                        transactionFiat.Status = Database.STATUS_NEW;
                        transactionFiat.TimeExecuted = null;
                    }

                    dbContext.SaveChanges();
                }
            }
            catch (Exception e)
            {
                Log.Error($"VirtualFiatCore.RecoverTransactions failed. Error: \"{e.Message}\"");
            }
        }

        protected void SendReceiveTransaction(DatabaseContext dbContext, TransactionFiat transactionFiat)
        {
            Log.Information($"VirtualFiatCore.SendReceiveTransaction id: {transactionFiat.Id}");

            Account account = null;
            CoreSendReceiveResult result;

            // If Account is null, it means it's a system transaction
            if (transactionFiat.Account != null) account = transactionFiat.Account;

            var dbTransaction = dbContext.Database.BeginTransaction();

            dbContext.LockAndUpdate(ref transactionFiat);

            // Check if not cancelled already
            if (transactionFiat.Status != Database.STATUS_NEW) return;

            // Not locking transactionFiat, as Send is done in only one thread 
            transactionFiat.Status = Database.STATUS_ACTIVE;
            transactionFiat.SendAttempts += 1;
            transactionFiat.TimeExecuted = DateTimeEx.RoundToSeconds(DateTime.Now);

            dbContext.SaveChanges();
            dbTransaction.Commit();

            dbTransaction = dbContext.Database.BeginTransaction();

            try
            {
                FiatAccount fiatAccount = null;

                if (account != null) fiatAccount = (FiatAccount)AccountFactory.Create(dbContext, this, account);

                if (transactionFiat.Amount < 0) result = Send(dbContext, transactionFiat);
                else result = ReceivePull(dbContext, transactionFiat);

                if (result.Status == CoreSendReceiveResult.STATUS_FAILED)
                {
                    transactionFiat.Status = Database.STATUS_FAILED;
                    ProcessTransactionFailure(dbContext, transactionFiat);
                    transactionFiat.TimeExecuted = null;

                    FailSystemTransactions(dbContext, transactionFiat);

                    if (fiatAccount != null) fiatAccount.FailTransaction(transactionFiat);

                    dbContext.SaveChanges();
                    dbTransaction.Commit();
                    return;
                }

                if (result.Status == CoreSendReceiveResult.STATUS_BALANCE)
                {
                    transactionFiat.Status = Database.STATUS_NEW;
                    transactionFiat.TimeRetry = DateTime.Now.AddMilliseconds(TimeRetryBalance);
                    transactionFiat.TimeExecuted = null;

                    dbContext.SaveChanges();
                    dbTransaction.Commit();
                    return;
                }

                transactionFiat.TimeExecuted = DateTimeEx.NowSeconds(); // Important!
                transactionFiat.ExternalId = result.Txid;
                transactionFiat.Status = Database.STATUS_PENDING;

                if (fiatAccount != null) fiatAccount.FinalizeTransaction(transactionFiat);

                dbContext.SaveChanges();
                dbTransaction.Commit();
            }
            catch (Exception e)
            {
                dbTransaction.Rollback();
                Log.Error($"VirtualFiatCore.SendReceiveTransaction failed. Error: \"{e.Message}\"");
            }
        }

        protected void ConfirmTransaction(DatabaseContext dbContext, TransactionFiat transactionFiat)
        {
            VirtualFiatTx details = null;
            bool isFailed = false;
            string failureReason = null;

            if (string.IsNullOrEmpty(transactionFiat.ExternalId))
            {
                failureReason = "ExternalId missing";
                isFailed = true;
            }

            if (!isFailed)
            {
                details = GetTxDetails(dbContext, transactionFiat.ExternalId);

                if (details == null)
                {
                    failureReason = "ExternalId not found";
                    isFailed = true;
                }
                else if (details.Status == VirtualFiatTx.STATUS_FAILED)
                {
                    failureReason = details.FailureReason;
                    isFailed = true;
                }
            }

            if (isFailed)
            {
                Log.Warning($"VirtuaFiatCore.ConfirmTransaction id {transactionFiat.Id} FAILED. Reason: '{failureReason}'.");

                var dbTransaction = dbContext.Database.BeginTransaction();

                try
                {
                    transactionFiat.Status = Database.STATUS_FAILED;
                    ProcessTransactionFailure(dbContext, transactionFiat);

                    var account = transactionFiat.Account;

                    if (account != null)
                    {
                        var fiatAccount = (FiatAccount)AccountFactory.Create(dbContext, this, account);
                        fiatAccount.FailTransaction(transactionFiat);
                    }

                    FailSystemTransactions(dbContext, transactionFiat);

                    dbContext.SaveChanges();
                    dbTransaction.Commit();
                }
                catch (Exception e)
                {
                    dbTransaction.Rollback();
                    Log.Error($"VirtuaFiatCore.ConfirmTransaction could not confirm transaction {transactionFiat.Id}. Error: \"{e.Message}\"");
                }

                return;
            }

            if (details.Status == VirtualFiatTx.STATUS_COMPLETED)
            {
                Log.Information($"VirtuaFiatCore.ConfirmTransaction id {transactionFiat.Id} completed.");

                var dbTransaction = dbContext.Database.BeginTransaction();

                try
                {
                    var account = transactionFiat.Account;

                    if (account != null)
                    {
                        var fiatAccount = (FiatAccount)AccountFactory.Create(dbContext, this, account);
                        fiatAccount.ConfirmTransaction(transactionFiat);
                    }

                    ConfirmSystemTransactions(dbContext, transactionFiat, details);

                    dbContext.SaveChanges();
                    dbTransaction.Commit();
                }
                catch (Exception e)
                {
                    dbTransaction.Rollback();
                    Log.Error($"VirtuaFiatCore.ConfirmTransaction could not confirm transaction {transactionFiat.Id}. Error: \"{e.Message}\"");
                }
            }
        }

        protected virtual TransactionFiat AddSyncTransaction(DatabaseContext dbContext, Asset asset, Account account, PaymentMethod paymentMethod, VirtualFiatTx fiatTx)
        {
            FiatAccount fiatAccount;
            TransactionFiat transactionFiat = null;

            // Log.Information($"VirtualFiatCore.AddSyncTransaction txid: {fiatTx.Txid}");

            if (fiatTx.Amount < 0)
            {
                Log.Information($"Dashboard send detected. Amount: {-fiatTx.Amount} {asset.Ticker}, txid: {fiatTx.Txid}.");
            }

            if (paymentMethod == null || account == null)
            {
                transactionFiat = AddNonUserTransaction(dbContext, asset, fiatTx);
            }
            else
            {
                if (paymentMethod.Asset == null) dbContext.Entry(paymentMethod).Reference(a => a.Asset).Load();
                if (asset != paymentMethod.Asset) throw new ApplicationException($"Payment method asset '{paymentMethod.Asset.Code}' does not match the transaction asset '{asset.Code}' in {fiatTx.Txid}.");

                // TODO: add transactions without accounts (need to create an account)
                if (account == null) throw new ApplicationException($"Account is missing for {fiatTx.Txid}.");

                fiatAccount = (FiatAccount)AccountFactory.Create(dbContext, this, account);

                transactionFiat = fiatAccount.AddTransaction(asset, account, paymentMethod, fiatTx);
            }

            return transactionFiat;
        }

        protected virtual TransactionFiat AddNonUserTransaction(DatabaseContext dbContext, Asset asset, VirtualFiatTx fiatTx)
        {
            TransactionSystem transactionSystem = null, transactionSystemFee = null;

            var exRate = AssetQuoter.Get(asset, MainAsset);

            // Create the fiat transaction
            var transactionFiat = new TransactionFiat();

            transactionFiat.Core = GetDataCore(dbContext);
            transactionFiat.Asset = asset;
            transactionFiat.Amount = fiatTx.Amount;
            transactionFiat.Fee = -fiatTx.Fee;
            transactionFiat.ExternalId = fiatTx.Txid;
            transactionFiat.TimeExecuted = fiatTx.TimeAdded;
            transactionFiat.AddedBy = TransactionFiat.ADDEDBY_SYNCHRONIZAITON;

            dbContext.TransactionFiat.Add(transactionFiat);

            if (fiatTx.Status != Database.STATUS_FAILED)
            {
                // Create system transaction
                transactionSystem = new TransactionSystem();

                transactionSystem.Type = TransactionSystem.TYPE_EXTERNAL;
                transactionSystem.Asset = asset;
                transactionSystem.Amount = transactionFiat.Amount;
                transactionSystem.ExRate = exRate;
                transactionSystem.FiatTx = transactionFiat;

                dbContext.TransactionSystem.Add(transactionSystem);

                if (0 < fiatTx.Fee)
                {
                    transactionSystemFee = new TransactionSystem();

                    transactionSystemFee.Type = TransactionSystem.TYPE_FEE;
                    transactionSystemFee.Asset = asset;
                    transactionSystemFee.Amount = -fiatTx.Fee;
                    transactionSystemFee.ExRate = exRate;
                    transactionSystemFee.Parent = transactionSystem;

                    dbContext.TransactionSystem.Add(transactionSystemFee);
                }
            }

            switch (fiatTx.Status)
            {
                case VirtualFiatTx.STATUS_COMPLETED:
                    transactionFiat.Status = Database.STATUS_SUCCESS;
                    if (transactionSystem != null) transactionSystem.Status = Database.STATUS_SUCCESS;
                    if (transactionSystemFee != null) transactionSystemFee.Status = Database.STATUS_SUCCESS;
                    break;
                case VirtualFiatTx.STATUS_PENDING:
                    transactionFiat.Status = Database.STATUS_PENDING;
                    if (transactionSystem != null) transactionSystem.Status = Database.STATUS_PENDING;
                    if (transactionSystemFee != null) transactionSystemFee.Status = Database.STATUS_PENDING;
                    break;
                case VirtualFiatTx.STATUS_FAILED:
                    transactionFiat.Status = Database.STATUS_FAILED;
                    ProcessTransactionFailure(dbContext, transactionFiat);
                    if (transactionSystem != null) transactionSystem.Status = Database.STATUS_FAILED;
                    if (transactionSystemFee != null) transactionSystemFee.Status = Database.STATUS_FAILED;
                    break;
                default:
                    throw new ApplicationException($"Unknown fiat tx status {fiatTx.Status} for {fiatTx.Txid}.");
            }

            return transactionFiat;
        }

        protected void RepairSyncTransaction(DatabaseContext dbContext, TransactionFiat transactionFiat, VirtualFiatTx fiatTx)
        {
            if (transactionFiat.Account == null) dbContext.Entry(transactionFiat).Reference(a => a.Account).Load();

            var account = transactionFiat.Account;

            if (account != null) dbContext.LockAndUpdate(ref transactionFiat);

            if (transactionFiat.Status == Database.STATUS_CANCELED) return;

            transactionFiat.Fee = -fiatTx.Fee;
            transactionFiat.ExternalId = fiatTx.Txid;
            transactionFiat.TimeExecuted = DateTimeEx.RoundToSeconds(fiatTx.TimeAdded); // important!
            transactionFiat.Status = Database.STATUS_PENDING;

            if (account != null)
            {
                var fiatAccount = (FiatAccount)AccountFactory.Create(dbContext, this, account);
                fiatAccount.FinalizeTransaction(transactionFiat);

                if (fiatTx.Status == VirtualFiatTx.STATUS_COMPLETED) fiatAccount.ConfirmTransaction(transactionFiat);
            }

            dbContext.SaveChanges();
        }

        protected void ConfirmSystemTransactions(DatabaseContext dbContext, TransactionFiat transactionFiat, VirtualFiatTx details)
        {
            transactionFiat.Status = Database.STATUS_SUCCESS;

            // Sometimes, the correct miner fee is only returned after the transaction is confirmed
            transactionFiat.Fee = -details.Fee;

            // Check if there is a system transaction
            var transactionSystem = dbContext.TransactionSystem.FirstOrDefault(a => a.FiatTx == transactionFiat);
            if (transactionSystem != null)
            {
                transactionSystem.Status = Database.STATUS_SUCCESS;

                var transactionSystemMinerFee = dbContext.TransactionSystem.FirstOrDefault(a => a.Parent == transactionSystem);
                if (transactionSystemMinerFee != null)
                {
                    transactionSystemMinerFee.Status = Database.STATUS_SUCCESS;
                    transactionSystemMinerFee.Amount = transactionFiat.Fee;
                }
            }
        }

        protected virtual void FailSystemTransactions(DatabaseContext dbContext, TransactionFiat transactionFiat)
        {
            // Check if the transaction had an internal miner fee transaction
            if (transactionFiat.InternalTx == null) dbContext.Entry(transactionFiat).Reference(a => a.InternalTx).Load();
            if (transactionFiat.InternalTx != null) transactionFiat.InternalTx.Status = Database.STATUS_FAILED;

            // Check if the transaction had a related system transaction
            var transactionSystem = dbContext.TransactionSystem.FirstOrDefault(a => a.FiatTx == transactionFiat);
            if (transactionSystem != null)
            {
                transactionSystem.Status = Database.STATUS_FAILED;

                var transactionSystemMinerFee = dbContext.TransactionSystem.FirstOrDefault(a => a.Parent == transactionSystem);
                if (transactionSystemMinerFee != null) transactionSystemMinerFee.Status = Database.STATUS_FAILED;
            }
        }

        protected void AddExchangeTransaction(DatabaseContext dbContext, Asset asset, VirtualFiatTx fiatTx)
        {
            // Log.Information($"VirtualFiatCore.AddExchangeTransaction txid: {fiatTx.Txid}");

            var exchange = dbContext.Exchange.FirstOrDefault(a => a.NameShort == DataCore.Name && a.Status == Database.STATUS_ACTIVE);
            if (exchange == null) return;

            var exchangeAsset = dbContext.ExchangeAsset.FirstOrDefault(a => a.Exchange == exchange && a.Asset == asset && a.Status == Database.STATUS_ACTIVE);
            if (exchangeAsset == null) return;

            var exRate = AssetQuoter.Get(asset, MainAsset);

            var exchangeTransaction = new ExchangeTransaction()
            {
                Type = ExchangeTransaction.TYPE_FUNDING,
                ExchangeAsset = exchangeAsset,
                Amount = fiatTx.Amount,
                ExRate = exRate,
                ExternalId = fiatTx.Txid,
                TimeAdded = DateTime.Now,
                TimeExecuted = fiatTx.TimeExecuted,
                Status = Database.STATUS_SUCCESS
            };

            dbContext.ExchangeTransaction.Add(exchangeTransaction);

            if (0 < fiatTx.Fee)
            {
                var exchangeTransactionFee = new ExchangeTransaction()
                {
                    Type = ExchangeTransaction.TYPE_FEE,
                    ExchangeAsset = exchangeAsset,
                    Amount = - fiatTx.Fee,
                    ExRate = exRate,
                    ExternalId = fiatTx.Txid,
                    TimeAdded = DateTime.Now,
                    TimeExecuted = fiatTx.TimeExecuted,
                    Status = Database.STATUS_SUCCESS
                };

                dbContext.ExchangeTransaction.Add(exchangeTransactionFee);
            }

            var balanceChange = fiatTx.Amount - fiatTx.Fee;
            var query = $"UPDATE ExchangeAsset SET BalanceTotal = BalanceTotal + {balanceChange}";
            query += $" WHERE Id = {exchangeAsset.Id}";

            // Log.Information($"VirtualFiatCore.AddExchangeTransaction query = '{query}'");

            dbContext.ExecuteStatement(query);
        }
    }
}

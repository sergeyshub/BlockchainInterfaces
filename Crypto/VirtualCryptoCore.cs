using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using DataAccess;
using LiveUpdate;
using ConfigAccess;
using HelpersLib;
using StatsLib;
using LogAccess;
using MessageLib;

namespace Assets.Crypto
{
    /// <summary>
    /// Abstract class representing a crypto core.
    /// </summary>
    /// <remarks>
    /// Contains functions to work with a crypto core.
    /// </remarks>
    public abstract class VirtualCryptoCore : VirtualCore
    {
        protected const int TIME_INITIAL_PAUSE = 30 * 1000; // milliseconds
        protected const int TIME_PROCESS_INTERVAL = 10 * 1000; // milliseconds
        protected const int TIME_RECOVERY_DELAY = 60 * 60 * 1000; // milliseconds

        protected const int TIME_RETRY_BALANCE = 60 * 60 * 1000; // milliseconds
        protected const int TIME_RETRY_FAILURE = 60000; // milliseconds
        protected const int TIME_RETRY_MOVING = 60000; // milliseconds
        protected const int TIME_RETRY_UNCONFIRMED = 60 * 60 * 1000; // milliseconds

        protected const int MAX_INTERNAL_ATTEMPTS = 10; // max number of times to attempt to send internal transactions

        protected int TimeSyncInterval;
        protected DateTime SyncTimeLimit;

        public readonly short MinConfirms;

        protected string NodeName;

        public VirtualCryptoCore(string coreName, string nodeName) : base(coreName)
        {
            if (nodeName == null) throw new ApplicationException($"Node name is not set for core {coreName}.");

            NodeName = nodeName;

            MinConfirms = (short)Config.GetInt($"CryptoAssets:{nodeName}:MinConfirms");
            TimeSyncInterval = Config.GetInt("CryptoAssets:SyncInterval", 10) * 1000;
            SyncTimeLimit = Config.GetDateTime($"CryptoAssets:{nodeName}:SyncTimeLimit", DateTime.Now.AddYears(-20));
        }

        /// <summary>
        /// Get balance of an address
        /// </summary>
        /// <remarks>
        /// Checks the balance of the wallet.
        /// 
        /// For Bitcoin, retrives the balance of the entire node, address is ignored
        /// For Etherium, retrives the balance of the address 
        /// 
        /// </remarks>
        /// <param name="Asset">Asset</param>
        /// <param name="address">Wallet address, if null, then checks MainAddress</param>
        /// <returns>Balance</returns>
        public abstract decimal GetBalance(Asset asset, string address = null, int? minConf = null);

        /// <summary>
        /// Validates an external address
        /// </summary>
        /// <remarks>
        /// Checks that the address is valid blockchain address.
        /// </remarks>
        /// <returns>True if valid</returns>
        public abstract bool ValidateAddress(string address);

        /// <summary>
        /// Is Mine Address
        /// </summary>
        /// <remarks>
        /// Checks that the address is ours.
        /// </remarks>
        /// <returns>True if the address is Ours</returns>
        public abstract bool IsMineAddress(string address);

        /// <summary>
        /// Checks that can send to the address
        /// </summary>
        /// <remarks>
        /// Some cores cannot send to a valid address. For example XRP cannot send to an address of its own node.
        /// </remarks>
        /// <returns>True if can send</returns>
        public virtual bool CanSendToAddress(string address)
        {
            return true;
        }

        /// <summary>
        /// Create a new internal address
        /// </summary>
        /// <remarks>
        /// Creates a new internal address.
        /// </remarks>
        /// <param name="password">Wallet password</param>
        /// <returns>New internal address</returns>
        public abstract string CreateNewAddress();

        /// <summary>
        /// Get transaction information
        /// </summary>
        /// <remarks>
        /// Gets transaction inputs and outputs.
        /// Throws an exception, if transaction does not exist.
        /// </remarks>
        /// <param name="txid">Txid</param>
        /// <returns>Array of inputs and outputs corresponding to this txid</returns>
        public abstract VirtualCryptoTx[] GetTxDetails(string txid);

        /// <summary>
        /// Get all transactions
        /// </summary>
        /// <remarks>
        /// Retrieves a list of last node transactions.
        /// </remarks>
        /// <param name="first">How many trx to skip from the end</param>
        /// <param name="number">Max number of transactions to fetch</param>
        /// <returns>Transactions</returns>
        public abstract VirtualCryptoTx[] GetTransactions(ulong first, ulong number);

        /// <summary>
        /// Get number of confirmations
        /// </summary>
        /// <remarks>
        /// Gets  number of confirmations.
        /// Throws an exception, if transaction does not exist.
        /// </remarks>
        /// <param name="txid">Txid</param>
        /// <param name="timeSent">Time sent</param>
        /// <returns>Number of confirmations. -1 means failed.</returns>
        public abstract int GetTxConfrims(string txid, DateTime? timeSent);

        /// <summary>
        /// Get miner fee options
        /// </summary>
        /// <remarks>
        /// Calculates miner fee options based on the transaction amount.
        /// Returns the specified number of options. The first is the fastest, the last is the slowest.
        /// The remaining evenly distrubted between the fastest and the slowest.
        /// </remarks>
        /// <param name="asset">The asset to send</param>
        /// <param name="amount">The amount to send</param>
        /// <param name="addressFrom">Address from</param>
        /// <param name="addressTo">Address to</param>
        /// <param name="number">Number of options to return</param>
        /// <returns>Fee options</returns>
        public abstract VirtualFeeOption[] GetFeeOptions(Asset asset, decimal amount, string addressFrom = null, string addressTo = null, int number = 1);

        /// <summary>
        /// Send amount to an address
        /// </summary>
        /// <remarks>
        /// Uses miner fee closest to the specified fee.
        /// Tries to use only confirmed inputs.
        /// Throws an exception, if the address is not a valid address.
        /// Throws an exception, if the amount exceeds the balance of the entire node.
        /// </remarks>
        /// <param name="dbContext">DatabaseContext</param>
        /// <param name="transactionCrypto">TransactionCrypto object</param>
        /// <returns>Send result</returns>
        public abstract CoreSendReceiveResult Send(DatabaseContext dbContext, TransactionCrypto transactionCrypto);

        /// <summary>
        /// Get main address
        /// </summary>
        /// <remarks>
        /// Returns the address where all outbound sends are sent from.
        /// </remarks>
        /// <returns>Send result</returns>
        public virtual string GetMainAddress()
        {
            return null;
        }

        /// <summary>
        /// Get miner fee
        /// </summary>
        /// <remarks>
        /// Retrieves the miner fee from transaction.
        /// </remarks>
        /// <param name="txid">Txid</param>
        /// <returns>Miner fee</returns>
        public decimal GetTxFee(string txid)
        {
            var cryptoTxs = GetTxDetails(txid);
            decimal fee = 0;

            if (cryptoTxs != null)
            {
                foreach (var cryptoTx in cryptoTxs)
                {
                    if (0 < Math.Abs(cryptoTx.Fee))
                    {
                        fee = Math.Abs(cryptoTx.Fee);
                        break;
                    }
                }
            }
            else
            {
                Log.Warning($"VirtualCryptoCore.GetTxFee Warning: GetTxDetails for {txid} returned NULL!");
            }

            return fee;
        }

        public void CancelTransaction(DatabaseContext dbContext, TransactionCrypto transactionCrypto, byte status = Database.STATUS_CANCELED)
        {
            Account account = null;
            CryptoAccount cryptoAccount = null;

            var dbTransaction = dbContext.Database.BeginTransaction();

            dbContext.LockAndUpdate(ref transactionCrypto);

            if (transactionCrypto.Status != Database.STATUS_NEW
                && transactionCrypto.Status != Database.STATUS_PENDING_ADMIN)
                throw new ApplicationException($"Cannot cancel crypto transaction {transactionCrypto.Id}. This status is {transactionCrypto.Status}.");

            transactionCrypto.Status = status;

            if (transactionCrypto.Account != null)
            {
                account = transactionCrypto.Account;
                cryptoAccount = (CryptoAccount)AccountFactory.Create(dbContext, this, account);
                cryptoAccount.CancelTransaction(transactionCrypto, status);
            }

            FailSystemAndChildTransactions(dbContext, transactionCrypto);

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
            Task.Run(() => StartSending());
            Task.Run(() => StartSynchronization());
            Task.Run(() => StartConfirmation());
            Task.Run(() => StartRecovery());
        }

        private void StartSending()
        {
            Thread.Sleep(TIME_INITIAL_PAUSE);

            while (true)
            {
                SendTransactions();
                Thread.Sleep(TIME_PROCESS_INTERVAL);
            }
        }

        private void StartSynchronization()
        {
            Thread.Sleep(TIME_INITIAL_PAUSE);

            Log.Information($"{Name} core sync started");

            int interval = 0;
            while (true)
            {
                SyncTransactions();
                Thread.Sleep(TimeSyncInterval);
                if (interval >= 60)
                {
                    interval = 0;
                    SaveBalance();
                }
                else interval++;
            }
        }

        private void StartConfirmation()
        {
            Thread.Sleep(TIME_INITIAL_PAUSE);

            while (true)
            {
                ConfirmTransactions();
                Thread.Sleep(TimeSyncInterval);
            }
        }

        private void StartRecovery()
        {
            Thread.Sleep(TIME_INITIAL_PAUSE);

            while (true)
            {
                RecoverTransactions();
                Thread.Sleep(TimeSyncInterval);
            }
        }

        protected void SendTransactions()
        {
            const int SEND_BATCH_SIZE = 1000;

            try
            {
                using (var dbContext = Database.GetContext())
                {
                    var transactionCryptos = dbContext.TransactionCrypto.Where(a => a.Core == DataCore && a.Status == Database.STATUS_NEW && (a.TimeRetry == null || a.TimeRetry <= DateTime.Now))
                        .Include(a => a.Account).Include(a => a.Asset)
                        .Take(SEND_BATCH_SIZE)
                        .ToList();

                    foreach (var transactionCrypto in transactionCryptos) SendTransaction(dbContext, transactionCrypto);
                }
            }
            catch (Exception e)
            {
                Log.Error($"VirtualCryptoCore.SendTransactions failed. Error: \"{e.Message}\"");
            }
        }

        protected abstract void SyncTransactions();

        public void ConfirmTransactions()
        {
            const int CONF_BATCH_SIZE = 1000;

            try
            {
                using (var dbContext = Database.GetContext())
                {
                    var transactionCryptos = dbContext.TransactionCrypto.Where(a => a.Core == DataCore && a.Status == Database.STATUS_PENDING)
                        .Include(a => a.Account).Include(a => a.Asset)
                        .OrderBy(a => a.TimeExecuted)
                        .ThenBy(a => a.Id)
                        .Take(CONF_BATCH_SIZE)
                        .ToList();

                    foreach (var transactionCrypto in transactionCryptos) ConfirmTransaction(dbContext, transactionCrypto);
                }
            }
            catch (Exception e)
            {
                Log.Error($"VirtualCryptoCore.ConfirmTransactions failed. Error: \"{e.Message}\"");
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

                    var transactionCryptos = dbContext.TransactionCrypto.Where(a => a.Core == DataCore && a.Status == Database.STATUS_ACTIVE && a.TimeExecuted < TimeSynced.AddMilliseconds(-TIME_RECOVERY_DELAY))
                        .OrderBy(a => a.TimeExecuted)
                        .ThenBy(a => a.Id)
                        .Include(a => a.Account).Include(a => a.Asset)
                        .ToList();

                    foreach (var transactionCrypto in transactionCryptos)
                    {
                        transactionCrypto.Status = Database.STATUS_NEW;
                        transactionCrypto.TimeExecuted = null;
                    }

                    dbContext.SaveChanges();
                }
            }
            catch (Exception e)
            {
                Log.Error($"VirtualCryptoCore.RecoverTransactions failed. Error: \"{e.Message}\"");
            }
        }

        protected void SendTransaction(DatabaseContext dbContext, TransactionCrypto transactionCrypto)
        {
            Account account = null;

            if (transactionCrypto.IsInternal)
            {
                if (transactionCrypto.Parent == null) dbContext.Entry(transactionCrypto).Reference("Parent").Load();
                var transactionCryptoParent = transactionCrypto.Parent;

                if (transactionCryptoParent != null)
                {
                    if (transactionCryptoParent.Status != Database.STATUS_SUCCESS)
                    {
                        transactionCrypto.TimeRetry = DateTime.Now.AddMilliseconds(TIME_RETRY_UNCONFIRMED);
                        dbContext.SaveChanges();
                        return;
                    }
                }
            }

            // If Account is null, it means it's a system transaction
            if (transactionCrypto.Account != null) account = transactionCrypto.Account;

            var dbTransaction = dbContext.Database.BeginTransaction();

            dbContext.LockAndUpdate(ref transactionCrypto);

            // Check if not cancelled already
            if (transactionCrypto.Status != Database.STATUS_NEW) return;

            // Not locking transactionCrypto, as Send is done in only one thread 
            transactionCrypto.Status = Database.STATUS_ACTIVE;
            transactionCrypto.SendAttempts += 1;
            transactionCrypto.TimeExecuted = DateTimeEx.RoundToSeconds(DateTime.Now);
            dbContext.SaveChanges();

            dbTransaction.Commit();

            dbTransaction = dbContext.Database.BeginTransaction();

            try
            {
                CryptoAccount cryptoAccount = null;
                if (account != null) cryptoAccount = (CryptoAccount)AccountFactory.Create(dbContext, this, account);
                var result = Send(dbContext, transactionCrypto);
                if (result.Status == CoreSendReceiveResult.STATUS_BALANCE
                    || result.Status == CoreSendReceiveResult.STATUS_FAILED)
                {
                    if (transactionCrypto.IsInternal && MAX_INTERNAL_ATTEMPTS < transactionCrypto.SendAttempts)
                    {
                        Log.Information($"VirtualCryptoCore.SendTransaction internal transaction failed, id: {transactionCrypto.Id}, reason: {((result.Status == CoreSendReceiveResult.STATUS_BALANCE) ? "balance" : "failed")}");

                        transactionCrypto.Status = Database.STATUS_FAILED;
                        FailSystemAndChildTransactions(dbContext, transactionCrypto);
                    }
                    else
                    {
                        Log.Information($"VirtualCryptoCore.SendTransaction send postponed, id: {transactionCrypto.Id}, reason: {((result.Status == CoreSendReceiveResult.STATUS_BALANCE) ? "balance" : "failed")}");

                        transactionCrypto.Status = Database.STATUS_NEW;
                        transactionCrypto.TimeRetry = DateTime.Now.AddMilliseconds(TIME_RETRY_BALANCE);
                    }

                    transactionCrypto.TimeExecuted = null;
                    dbContext.SaveChanges();
                    dbTransaction.Commit();
                    return;
                }
                Log.Information($"VirtualCryptoCore.SendTransaction sent id: {transactionCrypto.Id}, txid: {result.Txid}");
                transactionCrypto.MinerFee = -result.Fee;
                transactionCrypto.CryptoTxId = result.Txid;
                transactionCrypto.TimeExecuted = DateTimeEx.NowSeconds(); // Important!
                transactionCrypto.Status = Database.STATUS_PENDING;

                if (cryptoAccount != null) cryptoAccount.FinalizeTransaction(transactionCrypto);

                dbContext.SaveChanges();
                dbTransaction.Commit();
                //Log.Warning($"-SendTransaction for acc {account.Id} finished - {transactionCrypto.CryptoTxId}");
            }
            catch (Exception e)
            {
                dbTransaction.Rollback();
                Log.Error($"VirtualCryptoCore.SendTransaction failed. Error: \"{e.Message}\"");
            }
        }

        protected virtual TransactionCrypto AddSyncTransaction(DatabaseContext dbContext, Asset asset, VirtualCryptoTx cryptoTx)
        {
            Account account;
            CryptoAccount cryptoAccount;
            AddressCrypto addressCrypto = null;
            TransactionCrypto transactionCrypto;

            // There can be three possibilities:
            // - incoming to a known address
            // - incoming to an unknown address
            // - outgoing to any address (unidentified send)

            // Log.Information($"VirtualCryptoCore.AddSyncTransaction txid: {cryptoTx.Txid}");

            if (cryptoTx.Amount < 0)
            {
                var warning = $"Unidentified send! Amount: {-cryptoTx.Amount} {asset.Ticker}, txid: {cryptoTx.Txid}.";
                Log.Warning(warning);
                Messenger.SendTextToAdmins(dbContext, null, warning, "Assets");
            }

            // Find address. It can be for a different asset because of ERC20 tokens.
            if (cryptoTx.Address != null) addressCrypto = dbContext.AddressCrypto.Where(a => a.Address == cryptoTx.Address).Include(a => a.Account).Include(a => a.Account.Asset).FirstOrDefault();

            if (addressCrypto != null)
            {
                if (addressCrypto.Type != AddressCrypto.TYPE_USER) return AddNonUserTransaction(dbContext, asset, cryptoTx, addressCrypto);

                if (0 < cryptoTx.Amount && cryptoTx.Amount < asset.DepositMin) return AddNonUserTransaction(dbContext, asset, cryptoTx, addressCrypto);

                // Check if the address is for the same currency
                if (addressCrypto.Account.Asset.Code == asset.Code)
                {
                    cryptoAccount = new CryptoAccount(dbContext, this, addressCrypto.Account);
                    transactionCrypto = cryptoAccount.AddTransaction(cryptoTx);
                    return transactionCrypto;
                }

                // Check if there is already an account for that asset with same users
                account = asset.FindAccountWithSameUsers(dbContext, addressCrypto.Account);

                // Creare a new account for this asset
                if (account == null) account = asset.CreateAccountWithSameUsers(dbContext, addressCrypto.Account);

                cryptoAccount = new CryptoAccount(dbContext, this, account);
                transactionCrypto = cryptoAccount.AddTransaction(cryptoTx);
                return transactionCrypto;
            }

            // If the address is not in the DB, record this in the unclaimed account
            account = asset.FindAdminAccount(dbContext, Account.NAME_UNCLAIMED);
            cryptoAccount = new CryptoAccount(dbContext, this, account);

            // Add the address to the DB
            if (cryptoTx.Address != null)
            {
                addressCrypto = cryptoAccount.AddAddress(cryptoTx.Address);
                if (0 < cryptoTx.Amount && MinConfirms <= cryptoTx.Confirmations) addressCrypto.AmountReceived += cryptoTx.Amount;
            }

            transactionCrypto = cryptoAccount.AddTransaction(cryptoTx);

            return transactionCrypto;
        }

        protected virtual TransactionCrypto AddNonUserTransaction(DatabaseContext dbContext, Asset asset, VirtualCryptoTx cryptoTx, AddressCrypto addressCrypto)
        {
            var minerFeeAsset = dbContext.Asset.Find(cryptoTx.FeeAssetId);

            // Create the crypto transaction
            var transactionCrypto = new TransactionCrypto();

            transactionCrypto.Core = GetDataCore(dbContext);
            transactionCrypto.Asset = asset;
            transactionCrypto.Amount = cryptoTx.Amount;
            transactionCrypto.MinerFee = cryptoTx.Fee;
            transactionCrypto.MinerFeeAsset = minerFeeAsset;
            if (cryptoTx.Address != null) transactionCrypto.Address = cryptoTx.Address;
            if (cryptoTx.AddressExt != null) transactionCrypto.AddressExt = cryptoTx.AddressExt;
            transactionCrypto.CryptoTxId = cryptoTx.Txid;
            if (cryptoTx.Vout != null) transactionCrypto.CryptoTxIndex = cryptoTx.Vout;
            transactionCrypto.TimeExecuted = cryptoTx.Time;
            transactionCrypto.IsInternal = false;
            transactionCrypto.AddedBy = cryptoTx.AddedBy;
            transactionCrypto.Status = (MinConfirms <= cryptoTx.Confirmations) ? Database.STATUS_SUCCESS : Database.STATUS_PENDING;

            dbContext.TransactionCrypto.Add(transactionCrypto);

            dbContext.SaveChanges();

            return transactionCrypto;
        }

        protected TransactionCrypto AddInternalMoveTransaction(DatabaseContext dbContext, Asset asset, VirtualCryptoTx cryptoTx)
        {
            Account account;
            CryptoAccount cryptoAccount;
            AddressCrypto addressCrypto = null;

            Log.Information($"VirtualCryptoCore.AddInternalMoveTransaction txid: {cryptoTx.Txid}");

            // Check if any addresses are in the DB
            if (cryptoTx.Address != null) addressCrypto = dbContext.AddressCrypto.Include("Account").Include("Account.Asset").FirstOrDefault(a => a.Address == cryptoTx.Address);
            if (addressCrypto == null && cryptoTx.AddressExt != null) addressCrypto = dbContext.AddressCrypto.Include("Account").Include("Account.Asset").FirstOrDefault(a => a.Address == cryptoTx.AddressExt);

            if (addressCrypto != null)
            {
                if (0 < cryptoTx.Amount)
                {
                    // Mark the address as used
                    addressCrypto.AmountReceived += cryptoTx.Amount;
                    dbContext.SaveChanges();
                }

                // Check if the address is for the same currency
                if (addressCrypto.Account.Asset.Code == asset.Code)
                {
                    cryptoAccount = new CryptoAccount(dbContext, this, addressCrypto.Account);
                    return cryptoAccount.AddInternalMoveTx(cryptoTx);
                }

                // Check if there is already an account for that asset with same users
                account = asset.FindAccountWithSameUsers(dbContext, addressCrypto.Account);

                // Creare a new account for this asset
                if (account == null) account = asset.CreateAccountWithSameUsers(dbContext, addressCrypto.Account);

                cryptoAccount = new CryptoAccount(dbContext, this, account);

                return cryptoAccount.AddInternalMoveTx(cryptoTx);
            }

            // If the address is not in the DB, record this in the unclaimed account
            account = asset.FindAdminAccount(dbContext, Account.NAME_UNCLAIMED);
            cryptoAccount = new CryptoAccount(dbContext, this, account);

            return cryptoAccount.AddInternalMoveTx(cryptoTx);
        }

        protected void RepairSyncTransaction(DatabaseContext dbContext, TransactionCrypto transactionCrypto, VirtualCryptoTx cryptoTx)
        {
            if (transactionCrypto.Account == null) dbContext.Entry(transactionCrypto).Reference("Account").Load();

            var account = transactionCrypto.Account;

            if (account != null) dbContext.LockAndUpdate(ref transactionCrypto);

            transactionCrypto.MinerFee = -Math.Abs(cryptoTx.Fee);
            transactionCrypto.CryptoTxId = cryptoTx.Txid;
            if (cryptoTx.Vout != null) transactionCrypto.CryptoTxIndex = cryptoTx.Vout;
            transactionCrypto.TimeExecuted = DateTimeEx.RoundToSeconds(cryptoTx.Time); // important!
            transactionCrypto.Status = Database.STATUS_SUCCESS;

            if (account != null)
            {
                var cryptoAccount = new CryptoAccount(dbContext, this, account);
                cryptoAccount.FinalizeTransaction(transactionCrypto);
            }

            StatsCounter.IncrementVolume(dbContext, StatsVolume.TYPE_WALLET_CRYPTO, transactionCrypto.Asset, transactionCrypto.Amount);

            dbContext.SaveChanges();
        }

        protected void ConfirmTransaction(DatabaseContext dbContext, TransactionCrypto transactionCrypto)
        {
            int confirms = 0;
            bool isFailed = false;

            if (string.IsNullOrEmpty(transactionCrypto.CryptoTxId))
            {
                isFailed = true;
            }

            if (!isFailed)
            {
                confirms = GetTxConfrims(transactionCrypto.CryptoTxId, transactionCrypto.TimeExecuted);

                if (confirms < 0)
                {
                    isFailed = true;
                }
            }

            // Log.Information($"VirtuaCore.ConfirmTransaction checking tx {transactionCrypto.Id}, confirms: {confirms}");

            if (isFailed)
            {
                FailCryptoTransaction(dbContext, transactionCrypto);
                return;
            }

            if (MinConfirms <= confirms)
            {
                ConfirmCryptoTransaction(dbContext, transactionCrypto);
            }
        }

        protected virtual void ConfirmCryptoTransaction(DatabaseContext dbContext, TransactionCrypto transactionCrypto)
        {
            Log.Information($"VirtuaCryptoCore.ConfirmCryptoTransaction {transactionCrypto.Id}");

            var dbTransaction = dbContext.Database.BeginTransaction();

            try
            {
                var account = transactionCrypto.Account;

                if (account != null)
                {
                    var cryptoAccount = new CryptoAccount(dbContext, this, account);
                    cryptoAccount.ConfirmTransaction(transactionCrypto);
                }

                ConfirmSystemTransactions(dbContext, transactionCrypto);

                dbContext.SaveChanges();
                dbTransaction.Commit();
            }
            catch (Exception e)
            {
                dbTransaction.Rollback();
                Log.Error($"VirtuaCryptoCore.ConfirmCryptoTransaction could not confirm transaction {transactionCrypto.Id}. Error: \"{e.Message}\"");
            }
        }

        protected virtual void FailCryptoTransaction(DatabaseContext dbContext, TransactionCrypto transactionCrypto)
        {
            Log.Warning($"VirtuaCryptoCore.FailCryptoTransaction FAILED {transactionCrypto.Id}");

            var dbTransaction = dbContext.Database.BeginTransaction();

            try
            {
                transactionCrypto.Status = Database.STATUS_FAILED;

                var account = transactionCrypto.Account;

                if (account != null)
                {
                    var cryptoAccount = new CryptoAccount(dbContext, this, account);
                    cryptoAccount.FailTransaction(transactionCrypto);
                }

                if (transactionCrypto.IsInternal || transactionCrypto.Amount < 0) ResendCryptoTransaction(dbContext, transactionCrypto);
                else FailSystemAndChildTransactions(dbContext, transactionCrypto);

                dbContext.SaveChanges();
                dbTransaction.Commit();
            }
            catch (Exception e)
            {
                dbTransaction.Rollback();
                Log.Error($"VirtuaCryptoCore.FailCryptoTransaction could not fail transaction {transactionCrypto.Id}. Error: \"{e.Message}\"");
            }
        }

        protected virtual void ResendCryptoTransaction(DatabaseContext dbContext, TransactionCrypto transactionCrypto)
        {
            // By default, just fail all system and child transactions
            FailSystemAndChildTransactions(dbContext, transactionCrypto);
        }

        protected virtual void FailSystemAndChildTransactions(DatabaseContext dbContext, TransactionCrypto transactionCrypto)
        {
            // Check if the transaction had an internal miner fee transaction
            if (transactionCrypto.InternalTx == null) dbContext.Entry(transactionCrypto).Reference(a => a.InternalTx).Load();
            if (transactionCrypto.InternalTx != null) transactionCrypto.InternalTx.Status = Database.STATUS_FAILED;

            // Check if the transaction had a related system transaction
            var transactionSystem = dbContext.TransactionSystem.FirstOrDefault(a => a.CryptoTx == transactionCrypto);
            if (transactionSystem != null)
            {
                transactionSystem.Status = Database.STATUS_FAILED;

                var transactionSystemMinerFee = dbContext.TransactionSystem.FirstOrDefault(a => a.Parent == transactionSystem);
                if (transactionSystemMinerFee != null) transactionSystemMinerFee.Status = Database.STATUS_FAILED;
            }

            // Check if the transaction was a parent tx for any other transactions
            var transactionChild = dbContext.TransactionCrypto.FirstOrDefault(a => a.Parent == transactionCrypto);
            if (transactionChild != null)
            {
                transactionChild.Status = Database.STATUS_FAILED;
                FailSystemAndChildTransactions(dbContext, transactionChild);
            }
        }

        protected void ConfirmSystemTransactions(DatabaseContext dbContext, TransactionCrypto transactionCrypto)
        {
            transactionCrypto.Status = Database.STATUS_SUCCESS;

            // Sometimes, the correct miner fee is only returned after the transaction is confirmed
            if (transactionCrypto.Amount < 0) transactionCrypto.MinerFee = -GetTxFee(transactionCrypto.CryptoTxId);

            // Check if there is a system transaction
            var transactionSystem = dbContext.TransactionSystem.FirstOrDefault(a => a.CryptoTx == transactionCrypto);
            if (transactionSystem != null)
            {
                transactionSystem.Status = Database.STATUS_SUCCESS;

                var transactionSystemMinerFee = dbContext.TransactionSystem.FirstOrDefault(a => a.Parent == transactionSystem);
                if (transactionSystemMinerFee != null)
                {
                    transactionSystemMinerFee.Status = Database.STATUS_SUCCESS;
                    transactionSystemMinerFee.Amount = transactionCrypto.MinerFee;
                }
            }

            StatsCounter.IncrementVolume(dbContext, StatsVolume.TYPE_WALLET_CRYPTO, transactionCrypto.Asset, transactionCrypto.Amount);
        }

        protected void SaveBalance()
        {
            try
            {
                if (DataCore.Node != DataCore.Name) return; // Only for main Cores

                var balance = GetBalance(null);
                using (var db = Database.GetContext())
                {
                    db.ExecuteStatement($"INSERT INTO CoreBalance(CoreId, Balance) Values({DataCore.Id},{balance.ToString().Replace(',', '.')})");
                    db.SaveChanges();
                }
            }
            catch(Exception e)
            {
                Log.Error($"Crypto #{DataCore.Id} {DataCore.Node} SaveBalance Error: {e.Message}");
            }

        }
    }
}

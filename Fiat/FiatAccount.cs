using System;
using System.Linq;
using DataAccess;
using DataModels;
using Events;
using LiveUpdate;
using VerificationLib;
using WebSocket;
using HelpersLib;
using MessageLib;
using StatsLib;
using LogAccess;

namespace Assets.Fiat
{
    public abstract class FiatAccount : ProtoAccount
    {
        protected VirtualFiatCore Core;

        public FiatAccount(DatabaseContext dbContext, VirtualFiatCore core, Account account) : base(dbContext, account)
        {
            Core = core;
        }

        // TODO: account param is not used?
        public TransactionFiat AddTransaction(Asset asset, Account account, PaymentMethod paymentMethod, VirtualFiatTx fiatTx)
        {
            Transaction transactionFee = null;
            TransactionInternal transactionInternalFee = null, transactionInternalWithdrawalFee = null;
            TransactionSystem transactionSystem = null, transactionSystemFee = null;

            fiatTx.TimeAdded = DateTimeEx.RoundToSeconds(fiatTx.TimeAdded);

            var txFee = paymentMethod.ComputeFee(DbContext, fiatTx.Amount);

            var exRateAsset = AssetQuoter.Get(Account.Asset, MainAsset);

            DbContext.LockAndUpdate(ref Account);

            // Log.Information($"AddFiatTx: account {Account.Id} locked, balance = {Account.BalanceTotal}");

            // Create main user transaction
            var transactionMain = new Transaction();

            transactionMain.Account = Account;
            transactionMain.Asset = Account.Asset;
            transactionMain.Amount = fiatTx.Amount;
            transactionMain.BalanceAfter = 0;   // this will be computed later
            transactionMain.ExRate = exRateAsset;
            transactionMain.Number = Transaction.GetEntityNumber(DbContext);
            transactionMain.CreatedBy = null;   // system
            transactionMain.TimeExecuted = fiatTx.TimeAdded;
            transactionMain.Type = Transaction.TYPE_FIAT;

            DbContext.Transaction.Add(transactionMain);

            // Create deposit/withdrawal fee user transaction
            if (0 < txFee)
            {
                transactionFee = new Transaction();

                transactionFee.Account = Account;
                transactionFee.Asset = Account.Asset;
                transactionFee.Parent = transactionMain;
                transactionFee.Amount = -txFee;
                transactionFee.BalanceAfter = 0;   // this will be computed later
                transactionFee.ExRate = exRateAsset;
                transactionFee.Number = Transaction.GetEntityNumber(DbContext);
                transactionFee.CreatedBy = null;    // system
                transactionFee.TimeExecuted = fiatTx.TimeAdded;
                transactionFee.Type = Transaction.TYPE_FEE;

                DbContext.Transaction.Add(transactionFee);
            }

            // Create the internal core fee transaction
            if (0 < fiatTx.Fee && fiatTx.Status != Database.STATUS_FAILED)
            {
                transactionInternalFee = new TransactionInternal();

                transactionInternalFee.Type = TransactionInternal.TYPE_WALLET_FIAT;
                transactionInternalFee.Account = Account;
                transactionInternalFee.UserTx = transactionMain;
                transactionInternalFee.Asset = asset;
                transactionInternalFee.Amount = -fiatTx.Fee;
                transactionInternalFee.ExRate = exRateAsset;
                transactionInternalFee.TimeExecuted = fiatTx.TimeAdded;

                DbContext.TransactionInternal.Add(transactionInternalFee);
            }

            // Create the internal deposit/withdrawal fee transaction
            if (transactionFee != null && fiatTx.Status != Database.STATUS_FAILED)
            {
                transactionInternalWithdrawalFee = new TransactionInternal();

                transactionInternalWithdrawalFee.Type = TransactionInternal.TYPE_WALLET_FIAT;
                transactionInternalWithdrawalFee.Account = Account;
                transactionInternalWithdrawalFee.UserTx = transactionFee;
                transactionInternalWithdrawalFee.Asset = Account.Asset;
                if (transactionInternalFee != null) transactionInternalWithdrawalFee.Parent = transactionInternalFee;
                transactionInternalWithdrawalFee.Amount = txFee;
                transactionInternalWithdrawalFee.ExRate = exRateAsset;
                transactionInternalWithdrawalFee.TimeExecuted = fiatTx.TimeAdded;

                DbContext.TransactionInternal.Add(transactionInternalWithdrawalFee);
            }

            // Create the fiat transaction
            var transactionFiat = new TransactionFiat();

            transactionFiat.Core = Core.GetDataCore(DbContext);
            transactionFiat.Account = Account;
            transactionFiat.Asset = Account.Asset;
            transactionFiat.UserTx = transactionMain;
            if (transactionInternalFee != null) transactionFiat.InternalTx = transactionInternalFee;
            transactionFiat.Amount = fiatTx.Amount;
            transactionFiat.Fee = -fiatTx.Fee;
            transactionFiat.PaymentMethod = paymentMethod;
            transactionFiat.ExternalId = fiatTx.Txid;
            transactionFiat.TimeExecuted = fiatTx.TimeAdded;
            transactionFiat.AddedBy = TransactionFiat.ADDEDBY_SYNCHRONIZAITON;

            DbContext.TransactionFiat.Add(transactionFiat);

            if (fiatTx.Status != Database.STATUS_FAILED)
            {
                // Create system transaction
                transactionSystem = new TransactionSystem();

                transactionSystem.Type = TransactionSystem.TYPE_USER;
                transactionSystem.Asset = Account.Asset;
                transactionSystem.Amount = transactionFiat.Amount;
                transactionSystem.ExRate = exRateAsset;
                transactionSystem.FiatTx = transactionFiat;

                DbContext.TransactionSystem.Add(transactionSystem);

                if (0 < fiatTx.Fee)
                {
                    transactionSystemFee = new TransactionSystem();

                    transactionSystemFee.Type = TransactionSystem.TYPE_USER;
                    transactionSystemFee.Asset = Account.Asset;
                    transactionSystemFee.Amount = -fiatTx.Fee;
                    transactionSystemFee.ExRate = exRateAsset;
                    transactionSystemFee.Parent = transactionSystem;

                    DbContext.TransactionSystem.Add(transactionSystemFee);
                }
            }

            switch (fiatTx.Status)
            {
                case VirtualFiatTx.STATUS_COMPLETED:
                    transactionMain.Status = Database.STATUS_SUCCESS;
                    if (transactionFee != null) transactionFee.Status = Database.STATUS_SUCCESS;
                    if (transactionInternalFee != null) transactionInternalFee.Status = Database.STATUS_SUCCESS;
                    if (transactionInternalWithdrawalFee != null) transactionInternalWithdrawalFee.Status = Database.STATUS_SUCCESS;
                    transactionFiat.Status = Database.STATUS_SUCCESS;
                    if (transactionSystem != null) transactionSystem.Status = Database.STATUS_SUCCESS;
                    if (transactionSystemFee != null) transactionSystemFee.Status = Database.STATUS_SUCCESS;

                    StatsCounter.IncrementVolume(DbContext, StatsVolume.TYPE_WALLET_FIAT, transactionFiat.Asset, transactionFiat.Amount);
                    UpdateUserRating(transactionFiat);

                    break;
                case VirtualFiatTx.STATUS_PENDING:
                    transactionMain.Status = Database.STATUS_PENDING;
                    if (transactionFee != null) transactionFee.Status = Database.STATUS_PENDING;
                    if (transactionInternalFee != null) transactionInternalFee.Status = Database.STATUS_NEW;
                    if (transactionInternalWithdrawalFee != null) transactionInternalWithdrawalFee.Status = Database.STATUS_NEW;
                    transactionFiat.Status = Database.STATUS_PENDING;
                    if (transactionSystem != null) transactionSystem.Status = Database.STATUS_PENDING;
                    if (transactionSystemFee != null) transactionSystemFee.Status = Database.STATUS_PENDING;

                    Account.BalancePending += fiatTx.Amount - txFee;

                    break;
                case VirtualFiatTx.STATUS_FAILED:
                    transactionMain.Status = Database.STATUS_FAILED;
                    if (transactionFee != null) transactionFee.Status = Database.STATUS_FAILED;
                    if (transactionInternalFee != null) transactionInternalFee.Status = Database.STATUS_FAILED;
                    if (transactionInternalWithdrawalFee != null) transactionInternalWithdrawalFee.Status = Database.STATUS_FAILED;
                    transactionFiat.Status = Database.STATUS_FAILED;
                    Core.ProcessTransactionFailure(DbContext, transactionFiat);
                    if (transactionSystem != null) transactionSystem.Status = Database.STATUS_FAILED;
                    if (transactionSystemFee != null) transactionSystemFee.Status = Database.STATUS_FAILED;
                    break;
                default:
                    throw new ApplicationException($"Unknown fiat tx status {fiatTx.Status} for {fiatTx.Txid}.");
            }

            DbContext.SaveChanges();

            if (fiatTx.Status == VirtualFiatTx.STATUS_COMPLETED)
            {
                EventHub.TriggerFunding(DbContext, transactionMain);
            }

            UpdateBalance(transactionMain.TimeExecuted);

            if (fiatTx.Status != VirtualFiatTx.STATUS_FAILED)
            {
                SocketPusher.SendAccountUpdate(DbContext, transactionMain, true);
            }

            if (Account.CreatedBy == null) DbContext.Entry(Account).Reference(a => a.CreatedBy).Load();
            var userName = (Account.CreatedBy == null) ? "Unknown" : Account.CreatedBy.GetName();
            var message = $"Fiat tx detected for {fiatTx.Amount} {asset.Ticker} from {userName}.";
            Messenger.SendTextToAdmins(DbContext, null, message, "Assets");

            return transactionFiat;
        }

        public abstract PaymentInstructions ReceivePush(decimal amount, PaymentMethod paymentMethod, User createdBy);

        public void Send(decimal amount, PaymentMethod paymentMethod, User createdBy)
        {
            Transaction transactionFee = null;
            TransactionInternal transactionInternalSendFee = null;

            var withdrawalFee = paymentMethod.ComputeFee(DbContext, - amount);

            if (Account.BalanceTotal - Account.BalanceReserved - Account.BalancePending < amount + withdrawalFee)
                throw new ApplicationException($"Insufficient available balance on account {Account.Number} to send {amount} plus {withdrawalFee} withdrawal fee.");

            var sendFee = Core.GetFee(DbContext, -amount, paymentMethod);

            var exRate = AssetQuoter.Get(Account.Asset, MainAsset);

            // Account is already locked in the controller
            Account.BalanceReserved += amount + withdrawalFee;

            // Create the user send transaction
            var transactionSend = new Transaction();

            transactionSend.Account = Account;
            transactionSend.Asset = Account.Asset;
            transactionSend.Amount = -amount;
            transactionSend.BalanceAfter = Account.BalanceTotal;
            transactionSend.ExRate = exRate;
            transactionSend.CreatedBy = createdBy;
            transactionSend.Number = Transaction.GetEntityNumber(DbContext);
            transactionSend.Type = Transaction.TYPE_FIAT;
            transactionSend.Status = Database.STATUS_NEW;

            var isVerification = CheckVerificationRequired(transactionSend);
            if (isVerification) transactionSend.IsVerified = false;

            DbContext.Transaction.Add(transactionSend);

            // Create the user withdrawal fee transaction
            if (0 < withdrawalFee)
            {
                transactionFee = new Transaction();

                transactionFee.Account = Account;
                transactionFee.Asset = Account.Asset;
                transactionFee.Parent = transactionSend;
                transactionFee.Amount = -withdrawalFee;
                transactionFee.BalanceAfter = Account.BalanceTotal;
                transactionFee.ExRate = exRate;
                transactionFee.CreatedBy = createdBy;
                transactionFee.Number = Transaction.GetEntityNumber(DbContext);
                transactionFee.Type = Transaction.TYPE_FEE;
                transactionFee.Status = Database.STATUS_NEW;

                DbContext.Transaction.Add(transactionFee);
            }

            // Create the internal send fee transaction
            if (0 < sendFee)
            {
                transactionInternalSendFee = new TransactionInternal();

                transactionInternalSendFee.Type = TransactionInternal.TYPE_WALLET_FIAT;
                transactionInternalSendFee.Account = Account;
                transactionInternalSendFee.UserTx = transactionSend;
                transactionInternalSendFee.Asset = Asset;
                transactionInternalSendFee.Amount = -sendFee;
                transactionInternalSendFee.ExRate = exRate;
                transactionInternalSendFee.Status = Database.STATUS_NEW;

                DbContext.TransactionInternal.Add(transactionInternalSendFee);
            }

            // Create the internal withdrawal fee transaction
            if (transactionFee != null)
            {
                var transactionInternalWithdrawalFee = new TransactionInternal();

                transactionInternalWithdrawalFee.Type = TransactionInternal.TYPE_WALLET_FIAT;
                transactionInternalWithdrawalFee.Account = Account;
                transactionInternalWithdrawalFee.UserTx = transactionFee;
                transactionInternalWithdrawalFee.Asset = Asset;
                if (transactionInternalSendFee != null) transactionInternalWithdrawalFee.Parent = transactionInternalSendFee;
                transactionInternalWithdrawalFee.Amount = withdrawalFee;
                transactionInternalWithdrawalFee.ExRate = exRate;
                transactionInternalWithdrawalFee.Status = Database.STATUS_NEW;

                DbContext.TransactionInternal.Add(transactionInternalWithdrawalFee);
            }

            // Create the fiat send transaction
            var transactionFiat = new TransactionFiat();

            transactionFiat.Core = Core.GetDataCore(DbContext);
            transactionFiat.Account = Account;
            transactionFiat.Asset = Asset;
            transactionFiat.PaymentMethod = paymentMethod;
            transactionFiat.UserTx = transactionSend;
            transactionFiat.InternalTx = transactionInternalSendFee;
            transactionFiat.Amount = -amount;
            transactionFiat.Fee = -sendFee;
            transactionFiat.SendAttempts = 0;
            transactionFiat.AddedBy = TransactionFiat.ADDEDBY_USER;

            if (isVerification) transactionFiat.Status = Database.STATUS_PENDING_ADMIN;
            else transactionFiat.Status = Database.STATUS_NEW;

            DbContext.TransactionFiat.Add(transactionFiat);

            // Create system transaction
            var transactionSystem = new TransactionSystem();

            transactionSystem.Type = TransactionSystem.TYPE_USER;
            transactionSystem.Asset = Asset;
            transactionSystem.Amount = -amount;
            transactionSystem.ExRate = exRate;
            transactionSystem.FiatTx = transactionFiat;
            transactionSystem.Status = Database.STATUS_PENDING;

            DbContext.TransactionSystem.Add(transactionSystem);

            if (0 < sendFee)
            {
                var transactionSystemMinerFee = new TransactionSystem();

                transactionSystemMinerFee.Type = TransactionSystem.TYPE_FEE;
                transactionSystemMinerFee.Asset = Asset;
                transactionSystemMinerFee.Amount = -sendFee;
                transactionSystemMinerFee.ExRate = exRate;
                transactionSystemMinerFee.Parent = transactionSystem;
                transactionSystemMinerFee.Status = Database.STATUS_PENDING;

                DbContext.TransactionSystem.Add(transactionSystemMinerFee);
            }

            DbContext.SaveChanges();

            SocketPusher.SendAccountUpdate(DbContext, transactionSend, true);

            if (isVerification) new VerificationHandler(DbContext).SubmitTransaction(transactionSend, null, createdBy);

            var userName = (createdBy == null) ? "Unknown" : createdBy.GetName();
            var message = $"Fiat send for {amount} {Asset.Ticker} from {userName}.";
            Messenger.SendTextToAdmins(DbContext, null, message, "Assets");
        }

        public void ReceivePull(decimal amount, PaymentMethod paymentMethod, User createdBy)
        {
            Transaction transactionFee = null;
            TransactionInternal transactionInternalReceiveFee = null;

            var depositFee = paymentMethod.ComputeFee(DbContext, amount, false);

            var receiveFee = Core.GetFee(DbContext, amount, paymentMethod);

            var exRate = AssetQuoter.Get(Account.Asset, MainAsset);

            // Create the user receive transaction
            var transactionReceive = new Transaction();

            transactionReceive.Account = Account;
            transactionReceive.Asset = Account.Asset;
            transactionReceive.Amount = amount;
            transactionReceive.BalanceAfter = Account.BalanceTotal;
            transactionReceive.ExRate = exRate;
            transactionReceive.CreatedBy = createdBy;
            transactionReceive.Number = Transaction.GetEntityNumber(DbContext);
            transactionReceive.Type = Transaction.TYPE_FIAT;
            transactionReceive.Status = Database.STATUS_NEW;

            DbContext.Transaction.Add(transactionReceive);

            // Create the user withdrawal fee transaction
            if (0 < depositFee)
            {
                transactionFee = new Transaction();

                transactionFee.Account = Account;
                transactionFee.Asset = Account.Asset;
                transactionFee.Parent = transactionReceive;
                transactionFee.Amount = -depositFee;
                transactionFee.BalanceAfter = Account.BalanceTotal;
                transactionFee.ExRate = exRate;
                transactionFee.CreatedBy = createdBy;
                transactionFee.Number = Transaction.GetEntityNumber(DbContext);
                transactionFee.Type = Transaction.TYPE_FEE;
                transactionFee.Status = Database.STATUS_NEW;

                DbContext.Transaction.Add(transactionFee);
            }

            // Create the internal send fee transaction
            if (0 < receiveFee)
            {
                transactionInternalReceiveFee = new TransactionInternal();

                transactionInternalReceiveFee.Type = TransactionInternal.TYPE_WALLET_FIAT;
                transactionInternalReceiveFee.Account = Account;
                transactionInternalReceiveFee.UserTx = transactionReceive;
                transactionInternalReceiveFee.Asset = Asset;
                transactionInternalReceiveFee.Amount = -receiveFee;
                transactionInternalReceiveFee.ExRate = exRate;
                transactionInternalReceiveFee.Status = Database.STATUS_NEW;

                DbContext.TransactionInternal.Add(transactionInternalReceiveFee);
            }

            // Create the internal deposit fee transaction
            if (transactionFee != null)
            {
                var transactionInternalDepositFee = new TransactionInternal();

                transactionInternalDepositFee.Type = TransactionInternal.TYPE_WALLET_FIAT;
                transactionInternalDepositFee.Account = Account;
                transactionInternalDepositFee.UserTx = transactionFee;
                transactionInternalDepositFee.Asset = Asset;
                if (transactionInternalDepositFee != null) transactionInternalDepositFee.Parent = transactionInternalReceiveFee;
                transactionInternalDepositFee.Amount = depositFee;
                transactionInternalDepositFee.ExRate = exRate;
                transactionInternalDepositFee.Status = Database.STATUS_NEW;

                DbContext.TransactionInternal.Add(transactionInternalDepositFee);
            }

            // Create the fiat send transaction
            var transactionFiat = new TransactionFiat();

            transactionFiat.Core = Core.GetDataCore(DbContext);
            transactionFiat.Account = Account;
            transactionFiat.Asset = Asset;
            transactionFiat.PaymentMethod = paymentMethod;
            transactionFiat.UserTx = transactionReceive;
            transactionFiat.InternalTx = transactionInternalReceiveFee;
            transactionFiat.Amount = amount;
            transactionFiat.Fee = -receiveFee;
            transactionFiat.AddedBy = TransactionFiat.ADDEDBY_USER;
            transactionFiat.Status = Database.STATUS_NEW;

            DbContext.TransactionFiat.Add(transactionFiat);

            // Create system transaction
            var transactionSystem = new TransactionSystem();

            transactionSystem.Type = TransactionSystem.TYPE_USER;
            transactionSystem.Asset = Asset;
            transactionSystem.Amount = amount;
            transactionSystem.ExRate = exRate;
            transactionSystem.FiatTx = transactionFiat;
            transactionSystem.Status = Database.STATUS_PENDING;

            DbContext.TransactionSystem.Add(transactionSystem);

            if (0 < receiveFee)
            {
                var transactionSystemMinerFee = new TransactionSystem();

                transactionSystemMinerFee.Type = TransactionSystem.TYPE_FEE;
                transactionSystemMinerFee.Asset = Asset;
                transactionSystemMinerFee.Amount = -receiveFee;
                transactionSystemMinerFee.ExRate = exRate;
                transactionSystemMinerFee.Parent = transactionSystem;
                transactionSystemMinerFee.Status = Database.STATUS_PENDING;

                DbContext.TransactionSystem.Add(transactionSystemMinerFee);
            }

            DbContext.SaveChanges();

            SocketPusher.SendAccountUpdate(DbContext, transactionReceive, true);

            var userName = (createdBy == null) ? "Unknown" : createdBy.GetName();
            var message = $"Fiat receive pull for {amount} {Asset.Ticker} from {userName}.";
            Messenger.SendTextToAdmins(DbContext, null, message, "Assets");
        }

        public void FinalizeTransaction(TransactionFiat transactionFiat)
        {
            Transaction transactionMain, transactionFee;
            decimal txFee = 0;

            if (transactionFiat.UserTx == null) DbContext.Entry(transactionFiat).Reference(a => a.UserTx).Load();
            transactionMain = transactionFiat.UserTx;

            if (transactionMain == null) return;

            transactionFee = DbContext.Transaction.Where(a => a.Parent == transactionMain && a.Type == Transaction.TYPE_FEE).FirstOrDefault();

            if (transactionFee != null) txFee = -transactionFee.Amount;

            // Update the user transaction
            transactionMain.TimeExecuted = transactionFiat.TimeExecuted;
            transactionMain.Status = Database.STATUS_PENDING;

            // Update the user withdrawal fee transaction
            if (transactionFee != null)
            {
                transactionFee.TimeExecuted = transactionFiat.TimeExecuted;
                transactionFee.Status = Database.STATUS_PENDING;

                // Update the internal withdrawal fee transaction
                var transactionInternalTxFee = DbContext.TransactionInternal.Where(a => a.UserTx == transactionFee).FirstOrDefault();

                if (transactionInternalTxFee != null)
                {
                    transactionInternalTxFee.TimeExecuted = transactionFiat.TimeExecuted;
                }
            }

            DbContext.LockAndUpdate(ref Account);

            if (transactionFiat.Amount < 0)
            {
                Account.BalanceReserved -= -transactionFiat.Amount + txFee;
            }
            else
            {
                Account.BalancePending += transactionFiat.Amount - txFee;
            }

            DbContext.SaveChanges();
            UpdateBalance(transactionFiat.TimeExecuted);

            // Update the internal miner fee transaction
            if (transactionFiat.InternalTx == null) DbContext.Entry(transactionFiat).Reference(a => a.InternalTx).Load();
            var transactionInternalFee = transactionFiat.InternalTx;

            if (transactionInternalFee != null)
            {
                transactionInternalFee.Amount = transactionFiat.Fee;
                transactionInternalFee.TimeExecuted = transactionFiat.TimeExecuted;
            }

            SocketPusher.SendAccountUpdate(DbContext, transactionMain);
        }

        public void ConfirmTransaction(TransactionFiat transactionFiat)
        {
            TransactionInternal transactionInternalTxFee = null;
            decimal txFee = 0;

            transactionFiat.Status = Database.STATUS_SUCCESS;

            if (transactionFiat.UserTx == null) DbContext.Entry(transactionFiat).Reference(a => a.UserTx).Load();
            var transactionMain = transactionFiat.UserTx;

            if (transactionMain == null) return;

            // Update the main transaction
            transactionMain.Status = Database.STATUS_SUCCESS;

            var transactionFee = DbContext.Transaction.Where(a => a.Parent == transactionMain && a.Type == Transaction.TYPE_FEE).FirstOrDefault();

            if (transactionFee != null)
            {
                transactionFee.Status = Database.STATUS_SUCCESS;
                txFee = -transactionFee.Amount;

                // Update the internal withdrawal/deposit fee transaction
                transactionInternalTxFee = DbContext.TransactionInternal.Where(a => a.UserTx == transactionFee).FirstOrDefault();
                if (transactionInternalTxFee != null) transactionInternalTxFee.Status = Database.STATUS_SUCCESS;
            }

            DbContext.LockAndUpdate(ref Account);

            // Update balances
            if (transactionMain.Amount < 0)
            {
                // Nothing
            }
            else
            {
                Account.BalancePending -= transactionMain.Amount - txFee;
            }

            // Update the internal fee transaction
            if (transactionFiat.InternalTx == null) DbContext.Entry(transactionFiat).Reference(a => a.InternalTx).Load();
            var transactionInternalFee = transactionFiat.InternalTx;
            if (transactionInternalFee != null)
            {
                transactionInternalFee.Amount = transactionFiat.Fee;
                transactionInternalFee.Status = Database.STATUS_SUCCESS;
            }
            else if (transactionFiat.Fee != 0)
            {
                // Create the internal fee transaction
                var exRate = AssetQuoter.Get(Account.Asset, MainAsset);

                transactionInternalFee = new TransactionInternal();

                transactionInternalFee.Type = TransactionInternal.TYPE_WALLET_FIAT;
                transactionInternalFee.Account = Account;
                transactionInternalFee.UserTx = transactionMain;
                transactionInternalFee.Asset = Asset;
                transactionInternalFee.Amount = transactionFiat.Fee;
                transactionInternalFee.ExRate = exRate;
                transactionInternalFee.TimeExecuted = transactionFiat.TimeExecuted;
                transactionInternalFee.Status = Database.STATUS_SUCCESS;

                DbContext.TransactionInternal.Add(transactionInternalFee);

                if (transactionInternalTxFee != null)
                {
                    transactionInternalTxFee.Parent = transactionInternalFee;
                    transactionInternalTxFee.Status = Database.STATUS_SUCCESS;
                }
            }

            StatsCounter.IncrementVolume(DbContext, StatsVolume.TYPE_WALLET_FIAT, transactionFiat.Asset, transactionFiat.Amount);
            UpdateUserRating(transactionFiat);

            EventHub.TriggerFunding(DbContext, transactionMain);
            SocketPusher.SendAccountUpdate(DbContext, transactionMain);
        }

        public void FailTransaction(TransactionFiat transactionFiat)
        {
            Transaction transactionMain, transactionFee;
            var amount = transactionFiat.Amount;
            decimal txFee = 0;

            if (transactionFiat.UserTx == null) DbContext.Entry(transactionFiat).Reference(a => a.UserTx).Load();
            transactionMain = transactionFiat.UserTx;

            if (transactionMain == null) return;

            transactionFee = DbContext.Transaction.Where(a => a.Parent == transactionMain && a.Type == Transaction.TYPE_FEE).FirstOrDefault();

            if (transactionFee != null) txFee = -transactionFee.Amount;

            // Update the user transaction
            transactionMain.Status = Database.STATUS_FAILED;

            // Update the user withdrawal fee transaction
            if (transactionFee != null)
            {
                transactionFee.Status = Database.STATUS_FAILED;

                // Update the internal withdrawal fee transaction
                var transactionInternalWithdrawalFee = DbContext.TransactionInternal.Where(a => a.UserTx == transactionFee).FirstOrDefault();

                if (transactionInternalWithdrawalFee != null) transactionInternalWithdrawalFee.Status = Database.STATUS_FAILED;
            }

            // There are two possibilities:
            // - the transaction failed to send
            // - the transaction was sent, but failed to complete

            var isBalanceChange = false;

            DbContext.LockAndUpdate(ref Account);

            // Update account balance
            if (transactionFiat.TimeExecuted == null)
            {
                if (amount < 0)
                {
                    Account.BalanceReserved -= -amount + txFee;

                    DbContext.SaveChanges();
                    isBalanceChange = true;
                }
                else
                {
                    // Nothing
                }
            }
            else
            {
                if (amount < 0)
                {
                    // Nothing
                }
                else
                {
                    Account.BalancePending -= amount - txFee;
                    isBalanceChange = true;
                }

                DbContext.SaveChanges();
                UpdateBalance(transactionFiat.TimeExecuted);
                isBalanceChange = true;
            }

            if (isBalanceChange)
            {
                SocketPusher.SendAccountUpdate(DbContext, transactionMain);
            }

            // Update the internal fee transaction
            if (transactionFiat.InternalTx == null) DbContext.Entry(transactionFiat).Reference(a => a.InternalTx).Load();
            var transactionInternalFee = transactionFiat.InternalTx;

            if (transactionInternalFee != null) transactionInternalFee.Status = Database.STATUS_FAILED;
        }

        // Can only cancel if the status is STATUS_READY
        public void CancelTransaction(TransactionFiat transactionFiat, byte status = Database.STATUS_CANCELED)
        {
            Transaction transactionMain, transactionFee;
            var amount = transactionFiat.Amount;
            decimal txFee = 0;

            if (transactionFiat.UserTx == null) DbContext.Entry(transactionFiat).Reference(a => a.UserTx).Load();
            transactionMain = transactionFiat.UserTx;

            if (transactionMain == null) return;

            transactionFee = DbContext.Transaction.Where(a => a.Parent == transactionMain && a.Type == Transaction.TYPE_FEE).FirstOrDefault();

            if (transactionFee != null) txFee = -transactionFee.Amount;

            // Update the user transaction
            transactionMain.Status = status;

            // Update the user withdrawal fee transaction
            if (transactionFee != null)
            {
                transactionFee.Status = status;

                // Update the internal withdrawal fee transaction
                var transactionInternalWithdrawalFee = DbContext.TransactionInternal.Where(a => a.UserTx == transactionFee).FirstOrDefault();

                if (transactionInternalWithdrawalFee != null) transactionInternalWithdrawalFee.Status = Database.STATUS_FAILED;
            }

            if (amount < 0)
            {
                DbContext.LockAndUpdate(ref Account);

                Account.BalanceReserved -= -amount + txFee;
                DbContext.SaveChanges();
            }
            else
            {
                // Nothing
            }

            // Update the internal fee transaction
            if (transactionFiat.InternalTx == null) DbContext.Entry(transactionFiat).Reference(a => a.InternalTx).Load();
            var transactionInternalFee = transactionFiat.InternalTx;

            if (transactionInternalFee != null) transactionInternalFee.Status = Database.STATUS_FAILED;
        }

        // Update user rating if frist reversible deposit
        private void UpdateUserRating(TransactionFiat transactionFiat)
        {
            if (transactionFiat.UserTx == null) DbContext.Entry(transactionFiat).Reference(a => a.UserTx).Load();
            var transactionMain = transactionFiat.UserTx;
            if (transactionMain == null) return;

            if (transactionMain.CreatedBy == null) DbContext.Entry(transactionMain).Reference(a => a.CreatedBy).Load();
            if (transactionMain.CreatedBy == null) return;

            if (transactionMain.CreatedBy.Rating != User.RATING_NEW || transactionMain.Amount < 0) return;

            if (transactionFiat.PaymentMethod == null) DbContext.Entry(transactionFiat).Reference(a => a.PaymentMethod).Load();
            if (transactionFiat.PaymentMethod == null) return;

            if (transactionFiat.PaymentMethod.PaymentMethodType == null) DbContext.Entry(transactionFiat.PaymentMethod).Reference(a => a.PaymentMethodType).Load();
            if (transactionFiat.PaymentMethod.PaymentMethodType == null) return;

            if (!transactionFiat.PaymentMethod.PaymentMethodType.IsReversible) return;

            transactionMain.CreatedBy.Rating = 1;
            transactionMain.CreatedBy.RatingUpdated = DateTime.Now;
        }

        public void ApproveTransaction(TransactionFiat transactionFiat)
        {
            DbContext.LockAndUpdate(ref transactionFiat);

            if (transactionFiat.Status != Database.STATUS_PENDING_ADMIN)
            {
                Log.Warning($"FiatAccount.ApproveTransaction, fiat transaction {transactionFiat.Id} is not waiting for approval.");
                return;
            }

            transactionFiat.Status = Database.STATUS_NEW;
        }
    }
}

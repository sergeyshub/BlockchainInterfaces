using System;
using System.Linq;
using DataAccess;
using Events;
using LiveUpdate;
using VerificationLib;
using WebSocket;
using HelpersLib;
using StatsLib;
using LogAccess;

namespace Assets.Crypto
{
    public class CryptoAccount : ProtoAccount
    {
        protected VirtualCryptoCore Core;

        public CryptoAccount(DatabaseContext dbContext, VirtualCryptoCore core, Account account) : base(dbContext, account)
        {
            Core = core;
        }

        public string CreateNewAddress()
        {
            var address = Core.CreateNewAddress();
            AddAddress(address);
            return address;
        }

        public AddressCrypto AddAddress(string address)
        {
            var addressCrypto = new AddressCrypto();

            addressCrypto.Type = AddressCrypto.TYPE_USER;
            addressCrypto.Core = Core.GetDataCore(DbContext);
            addressCrypto.Asset = Asset;
            addressCrypto.Account = Account;
            addressCrypto.Address = address;

            DbContext.AddressCrypto.Add(addressCrypto);
            DbContext.SaveChanges();

            return addressCrypto;
        }

        public TransactionCrypto AddTransaction(VirtualCryptoTx cryptoTx)
        {
            Transaction transactionFee = null;
            TransactionInternal transactionInternalMinerFee = null;

            cryptoTx.Time = DateTimeEx.RoundToSeconds(cryptoTx.Time);

            if (0 < Math.Abs(cryptoTx.Fee) && cryptoTx.Amount < 0) cryptoTx.Fee = -Math.Abs(cryptoTx.Fee);
            else cryptoTx.Fee = 0;

            var isConfirmed = Core.MinConfirms <= cryptoTx.Confirmations;

            var txFee = Account.Asset.ComputeFee(cryptoTx.Amount);

            var minerFeeAsset = DbContext.Asset.Find(cryptoTx.FeeAssetId);

            var exRateAsset = AssetQuoter.Get(Account.Asset, MainAsset);
            var exRateMinerFeeAsset = AssetQuoter.Get(minerFeeAsset, MainAsset);

            DbContext.LockAndUpdate(ref Account);

            // Log.Information($"AddCryptoTx: account {Account.Id} locked, balance = {Account.BalanceTotal}");

            // Create main user transaction
            var transactionMain = new Transaction();

            transactionMain.Account = Account;
            transactionMain.Asset = Account.Asset;
            transactionMain.Amount = cryptoTx.Amount;
            transactionMain.BalanceAfter = 0;   // this will be computed later
            transactionMain.ExRate = exRateAsset;
            transactionMain.Number = Transaction.GetEntityNumber(DbContext);
            transactionMain.CreatedBy = null;   // system
            transactionMain.TimeExecuted = cryptoTx.Time;
            transactionMain.Type = Transaction.TYPE_CRYPTO;
            transactionMain.Status = (isConfirmed) ? Database.STATUS_SUCCESS : Database.STATUS_PENDING;

            DbContext.Transaction.Add(transactionMain);
            // DbContext.SaveChanges();

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
                transactionFee.TimeExecuted = cryptoTx.Time;
                transactionFee.Type = Transaction.TYPE_FEE;
                transactionFee.Status = (isConfirmed) ? Database.STATUS_SUCCESS : Database.STATUS_PENDING;

                DbContext.Transaction.Add(transactionFee);
                // DbContext.SaveChanges();
            }

            // Create the internal miner fee transaction
            if (cryptoTx.Fee < 0)
            {
                transactionInternalMinerFee = new TransactionInternal();

                transactionInternalMinerFee.Type = TransactionInternal.TYPE_WALLET_CRYPTO;
                transactionInternalMinerFee.Account = Account;
                transactionInternalMinerFee.UserTx = transactionMain;
                transactionInternalMinerFee.Asset = minerFeeAsset;
                transactionInternalMinerFee.Amount = cryptoTx.Fee;
                transactionInternalMinerFee.ExRate = exRateMinerFeeAsset;
                transactionInternalMinerFee.TimeExecuted = cryptoTx.Time;
                transactionInternalMinerFee.Status = Database.STATUS_SUCCESS;

                DbContext.TransactionInternal.Add(transactionInternalMinerFee);
                // DbContext.SaveChanges();
            }

            // Create the internal deposit/withdrawal fee transaction
            if (transactionFee != null)
            {
                var transactionInternalWithdrawalFee = new TransactionInternal();

                transactionInternalWithdrawalFee.Type = TransactionInternal.TYPE_WALLET_CRYPTO;
                transactionInternalWithdrawalFee.Account = Account;
                transactionInternalWithdrawalFee.UserTx = transactionFee;
                transactionInternalWithdrawalFee.Asset = Account.Asset;
                if (transactionInternalMinerFee != null) transactionInternalWithdrawalFee.Parent = transactionInternalMinerFee;
                transactionInternalWithdrawalFee.Amount = txFee;
                transactionInternalWithdrawalFee.ExRate = exRateAsset;
                transactionInternalWithdrawalFee.TimeExecuted = cryptoTx.Time;
                transactionInternalWithdrawalFee.Status = Database.STATUS_SUCCESS;

                DbContext.TransactionInternal.Add(transactionInternalWithdrawalFee);
                // DbContext.SaveChanges();
            }

            // Create the crypto send transaction
            var transactionCrypto = new TransactionCrypto();

            transactionCrypto.Account = Account;
            transactionCrypto.Core = Core.GetDataCore(DbContext);
            transactionCrypto.Asset = Account.Asset;
            transactionCrypto.UserTx = transactionMain;
            if (transactionInternalMinerFee != null) transactionCrypto.InternalTx = transactionInternalMinerFee;
            transactionCrypto.Amount = cryptoTx.Amount;
            transactionCrypto.MinerFee = cryptoTx.Fee;
            transactionCrypto.MinerFeeAsset = minerFeeAsset;
            if (cryptoTx.Address != null) transactionCrypto.Address = cryptoTx.Address;
            if (cryptoTx.AddressExt != null) transactionCrypto.AddressExt = cryptoTx.AddressExt;
            transactionCrypto.CryptoTxId = cryptoTx.Txid;
            if (cryptoTx.Vout != null) transactionCrypto.CryptoTxIndex = cryptoTx.Vout;
            transactionCrypto.TimeExecuted = cryptoTx.Time;
            transactionCrypto.AddedBy = cryptoTx.AddedBy;
            transactionCrypto.IsInternal = false;

            if (isConfirmed)
            {
                transactionCrypto.Status = Database.STATUS_SUCCESS;

                // Increment AmountReceived in AddressCrypto
                if (0 < transactionCrypto.Amount && !string.IsNullOrEmpty(transactionCrypto.Address))
                {
                    var addressCrypto = DbContext.AddressCrypto.FirstOrDefault(a => a.Address == transactionCrypto.Address);
                    if (addressCrypto != null) addressCrypto.AmountReceived += transactionCrypto.Amount;
                }

                StatsCounter.IncrementVolume(DbContext, StatsVolume.TYPE_WALLET_CRYPTO, transactionCrypto.Asset, transactionCrypto.Amount);
            }
            else
            {
                // Log.Information($"CryptoAccount.AddCryptoTx update pending balance, account: {Account.Id}, balance: {Account.BalancePending}, new balance: {Account.BalancePending + cryptoTx.Amount - txFee}, txid: {cryptoTx.Txid}");
                transactionCrypto.Status = Database.STATUS_PENDING;
                Account.BalancePending += cryptoTx.Amount - txFee;
            }

            DbContext.TransactionCrypto.Add(transactionCrypto);

            // Sometimes, a system transaction may not exist, if unidentified outgoing send
            if (cryptoTx.Amount < 0)
            {
                // Create system transaction
                var transactionSystem = new TransactionSystem();

                transactionSystem.Type = TransactionSystem.TYPE_USER;
                transactionSystem.Asset = Account.Asset;
                transactionSystem.Amount = transactionCrypto.Amount;
                transactionSystem.ExRate = exRateAsset;
                transactionSystem.CryptoTx = transactionCrypto;
                transactionSystem.Status = (isConfirmed) ? Database.STATUS_SUCCESS : Database.STATUS_PENDING;

                DbContext.TransactionSystem.Add(transactionSystem);

                var transactionSystemMinerFee = new TransactionSystem();

                transactionSystemMinerFee.Type = TransactionSystem.TYPE_FEE;
                transactionSystemMinerFee.Asset = minerFeeAsset;
                transactionSystemMinerFee.Amount = transactionCrypto.MinerFee;
                transactionSystemMinerFee.ExRate = exRateMinerFeeAsset;
                transactionSystemMinerFee.Parent = transactionSystem;
                transactionSystem.Status = (isConfirmed) ? Database.STATUS_SUCCESS : Database.STATUS_PENDING;

                DbContext.TransactionSystem.Add(transactionSystemMinerFee);
            }

            DbContext.SaveChanges();

            UpdateBalance(transactionMain.TimeExecuted);

            if (isConfirmed)
            {
                EventHub.TriggerFunding(DbContext, transactionMain);
            }

            // WebSocket
            SocketPusher.SendAccountUpdate(DbContext, transactionMain, true);

            return transactionCrypto;
        }

        public TransactionCrypto AddInternalMoveTx(VirtualCryptoTx cryptoTx)
        {
            TransactionInternal transactionInternalMinerFee = null;

            cryptoTx.Time = DateTimeEx.RoundToSeconds(cryptoTx.Time);

            if (0 < Math.Abs(cryptoTx.Fee) && cryptoTx.Amount < 0) cryptoTx.Fee = -Math.Abs(cryptoTx.Fee);
            else cryptoTx.Fee = 0;

            var txFee = Account.Asset.ComputeFee(cryptoTx.Amount);

            var minerFeeAsset = DbContext.Asset.Find(cryptoTx.FeeAssetId);

            var exRateAsset = AssetQuoter.Get(Account.Asset, MainAsset);
            var exRateMinerFeeAsset = AssetQuoter.Get(minerFeeAsset, MainAsset);

            DbContext.LockAndUpdate(ref Account);

            // Create the internal miner fee transaction
            if (cryptoTx.Fee < 0)
            {
                transactionInternalMinerFee = new TransactionInternal();

                transactionInternalMinerFee.Type = TransactionInternal.TYPE_WALLET_CRYPTO;
                transactionInternalMinerFee.Account = Account;
                transactionInternalMinerFee.Asset = minerFeeAsset;
                transactionInternalMinerFee.Amount = cryptoTx.Fee;
                transactionInternalMinerFee.ExRate = exRateMinerFeeAsset;
                transactionInternalMinerFee.TimeExecuted = cryptoTx.Time;
                transactionInternalMinerFee.Status = Database.STATUS_SUCCESS;

                DbContext.TransactionInternal.Add(transactionInternalMinerFee);
                // DbContext.SaveChanges();
            }

            // Create the crypto send transaction
            var transactionCrypto = new TransactionCrypto();

            transactionCrypto.Account = Account;
            transactionCrypto.Core = Core.GetDataCore(DbContext);
            transactionCrypto.Asset = Account.Asset;
            if (transactionInternalMinerFee != null) transactionCrypto.InternalTx = transactionInternalMinerFee;
            transactionCrypto.Amount = cryptoTx.Amount;
            transactionCrypto.MinerFee = cryptoTx.Fee;
            transactionCrypto.MinerFeeAsset = minerFeeAsset;
            if (cryptoTx.Address != null) transactionCrypto.Address = cryptoTx.Address;
            if (cryptoTx.AddressExt != null) transactionCrypto.AddressExt = cryptoTx.AddressExt;
            transactionCrypto.CryptoTxId = cryptoTx.Txid;
            if (cryptoTx.Vout != null) transactionCrypto.CryptoTxIndex = cryptoTx.Vout;
            transactionCrypto.TimeExecuted = cryptoTx.Time;
            transactionCrypto.AddedBy = cryptoTx.AddedBy;
            transactionCrypto.IsInternal = true;
            transactionCrypto.Status = (Core.MinConfirms < cryptoTx.Confirmations) ? Database.STATUS_SUCCESS : Database.STATUS_PENDING;

            DbContext.TransactionCrypto.Add(transactionCrypto);

            DbContext.SaveChanges();

            return transactionCrypto;
        }

        public void Send(decimal amount, string address, User createdBy)
        {
            Transaction transactionFee = null;
            TransactionInternal transactionInternalMinerFee = null;

            var withdrawalFee = Account.Asset.ComputeFee(-amount);

            if (Account.BalanceTotal - Account.BalanceReserved - Account.BalancePending < amount + withdrawalFee)
                throw new ApplicationException($"Insufficient available balance on account {Account.Number} to send {amount} plus {withdrawalFee} withdrawal fee.");

            var mainAddress = Core.GetMainAddress();
            var feeOptions = Core.GetFeeOptions(Asset, amount, mainAddress, address, 1);
            var minerFee = feeOptions[0].Fee;

            var minerFeeAsset = DbContext.Asset.Find(feeOptions[0].AssetId);

            var exRateAsset = AssetQuoter.Get(Account.Asset, MainAsset);
            var exRateMinerFeeAsset = AssetQuoter.Get(minerFeeAsset, MainAsset);

            // It is already locked in the controller
            Account.BalanceReserved += amount + withdrawalFee;

            // Create the user send transaction
            var transactionSend = new Transaction();

            transactionSend.Account = Account;
            transactionSend.Asset = Account.Asset;
            transactionSend.Amount = -amount;
            transactionSend.BalanceAfter = Account.BalanceTotal;
            transactionSend.ExRate = exRateAsset;
            transactionSend.CreatedBy = createdBy;
            transactionSend.Number = Transaction.GetEntityNumber(DbContext);
            transactionSend.Type = Transaction.TYPE_CRYPTO;
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
                transactionFee.ExRate = exRateAsset;
                transactionFee.CreatedBy = createdBy;
                transactionFee.Number = Transaction.GetEntityNumber(DbContext);
                transactionFee.Type = Transaction.TYPE_FEE;
                transactionFee.Status = Database.STATUS_NEW;

                DbContext.Transaction.Add(transactionFee);
            }

            // Create the internal miner fee transaction
            if (0 < minerFee)
            {
                transactionInternalMinerFee = new TransactionInternal();

                transactionInternalMinerFee.Type = TransactionInternal.TYPE_WALLET_CRYPTO;
                transactionInternalMinerFee.Account = Account;
                transactionInternalMinerFee.UserTx = transactionSend;
                transactionInternalMinerFee.Asset = minerFeeAsset;
                transactionInternalMinerFee.Amount = -minerFee;
                transactionInternalMinerFee.ExRate = exRateMinerFeeAsset;
                transactionInternalMinerFee.Status = Database.STATUS_NEW;

                DbContext.TransactionInternal.Add(transactionInternalMinerFee);
            }

            // Create the internal withdrawal fee transaction
            if (transactionFee != null)
            {
                var transactionInternalWithdrawalFee = new TransactionInternal();

                transactionInternalWithdrawalFee.Type = TransactionInternal.TYPE_WALLET_CRYPTO;
                transactionInternalWithdrawalFee.Account = Account;
                transactionInternalWithdrawalFee.UserTx = transactionFee;
                transactionInternalWithdrawalFee.Asset = Asset;
                if (transactionInternalMinerFee != null) transactionInternalWithdrawalFee.Parent = transactionInternalMinerFee;
                transactionInternalWithdrawalFee.Amount = withdrawalFee;
                transactionInternalWithdrawalFee.ExRate = exRateAsset;
                transactionInternalWithdrawalFee.Status = Database.STATUS_NEW;

                DbContext.TransactionInternal.Add(transactionInternalWithdrawalFee);
            }

            // Create the crypto send transaction
            var transactionCrypto = new TransactionCrypto();

            transactionCrypto.Account = Account;
            transactionCrypto.Core = Core.GetDataCore(DbContext);
            transactionCrypto.Asset = Asset;
            transactionCrypto.UserTx = transactionSend;
            transactionCrypto.InternalTx = transactionInternalMinerFee;
            transactionCrypto.Amount = -amount;
            transactionCrypto.MinerFee = -minerFee;
            transactionCrypto.MinerFeeAsset = minerFeeAsset;
            transactionCrypto.AddressExt = address;
            transactionCrypto.SendAttempts = 0;
            transactionCrypto.AddedBy = TransactionCrypto.ADDEDBY_USER;
            transactionCrypto.IsInternal = false;

            if (isVerification) transactionCrypto.Status = Database.STATUS_PENDING_ADMIN;
            else transactionCrypto.Status = Database.STATUS_NEW;

            DbContext.TransactionCrypto.Add(transactionCrypto);

            // Create system transaction
            var transactionSystem = new TransactionSystem();

            transactionSystem.Type = TransactionSystem.TYPE_USER;
            transactionSystem.Asset = Asset;
            transactionSystem.Amount = -amount;
            transactionSystem.ExRate = exRateAsset;
            transactionSystem.CryptoTx = transactionCrypto;
            transactionSystem.Status = Database.STATUS_PENDING;

            DbContext.TransactionSystem.Add(transactionSystem);

            if (0 < minerFee)
            {
                var transactionSystemMinerFee = new TransactionSystem();

                transactionSystemMinerFee.Type = TransactionSystem.TYPE_FEE;
                transactionSystemMinerFee.Asset = minerFeeAsset;
                transactionSystemMinerFee.Amount = -minerFee;
                transactionSystemMinerFee.ExRate = exRateMinerFeeAsset;
                transactionSystemMinerFee.Parent = transactionSystem;
                transactionSystemMinerFee.Status = Database.STATUS_PENDING;

                DbContext.TransactionSystem.Add(transactionSystemMinerFee);
            }

            DbContext.SaveChanges();

            if (isVerification) new VerificationHandler(DbContext).SubmitTransaction(transactionSend, null, createdBy);

            // WebSocket
            SocketPusher.SendAccountUpdate(DbContext, transactionSend, true);
        }

        public void FinalizeTransaction(TransactionCrypto transactionCrypto)
        {
            Transaction transactionSend, transactionFee;
            decimal txFee = 0;

            if (transactionCrypto.UserTx == null) DbContext.Entry(transactionCrypto).Reference("UserTx").Load();
            transactionSend = transactionCrypto.UserTx;

            if (transactionSend != null)
            {
                transactionFee = DbContext.Transaction.Where(a => a.Parent == transactionSend && a.Type == Transaction.TYPE_FEE).FirstOrDefault();

                if (transactionFee != null) txFee = -transactionFee.Amount;

                // Update the user transaction
                transactionSend.TimeExecuted = transactionCrypto.TimeExecuted;
                transactionSend.Status = Database.STATUS_PENDING; // Changed from New to Pending (both counted in BalanceReserved)

                // Update the user withdrawal fee transaction
                if (transactionFee != null)
                {
                    transactionFee.TimeExecuted = transactionCrypto.TimeExecuted;
                    transactionFee.Status = Database.STATUS_PENDING;

                    // Update the internal withdrawal fee transaction
                    var transactionInternalWithdrawalFee = DbContext.TransactionInternal.Where(a => a.UserTx == transactionFee).FirstOrDefault();

                    if (transactionInternalWithdrawalFee != null)
                    {
                        transactionInternalWithdrawalFee.TimeExecuted = transactionCrypto.TimeExecuted;
                        transactionInternalWithdrawalFee.Status = Database.STATUS_SUCCESS;
                    }
                }

                // We do not change BalanceReserved after sent to blockchain. We do in after confirmation
                /*DbContext.LockAndUpdate(ref Account);
                Account.BalanceReserved -= -transactionSend.Amount + txFee;*/

                DbContext.SaveChanges();
                UpdateBalance(transactionSend.TimeExecuted);

                // WebSocket
                SocketPusher.SendAccountUpdate(DbContext, transactionSend);
            }

            // Update the internal miner fee transaction
            if (transactionCrypto.InternalTx == null) DbContext.Entry(transactionCrypto).Reference("InternalTx").Load();
            var transactionInternalMinerFee = transactionCrypto.InternalTx;

            if (transactionInternalMinerFee != null)
            {
                transactionInternalMinerFee.Amount = transactionCrypto.MinerFee;
                transactionInternalMinerFee.TimeExecuted = transactionCrypto.TimeExecuted;
                transactionInternalMinerFee.Status = Database.STATUS_SUCCESS;
            }
        }

        public void ConfirmTransaction(TransactionCrypto transactionCrypto)
        {
            decimal txFee = 0;

            if (transactionCrypto.UserTx == null) DbContext.Entry(transactionCrypto).Reference("UserTx").Load();
            var transactionMain = transactionCrypto.UserTx;

            // Update the main transaction
            if (transactionMain != null)
            {
                transactionMain.Status = Database.STATUS_SUCCESS;

                var transactionFee = DbContext.Transaction.Where(a => a.Parent == transactionMain && a.Type == Transaction.TYPE_FEE).FirstOrDefault();

                if (transactionFee != null)
                {
                    transactionFee.Status = Database.STATUS_SUCCESS;
                    txFee = -transactionFee.Amount;
                }

                DbContext.LockAndUpdate(ref Account);

                // Update balances
                if (0 < transactionMain.Amount)
                {
                    // TODO: Will not work if there is Fee for deposit, because we count only Amount>0 for BalancePending:
                    Account.BalancePending -= transactionMain.Amount + txFee;
                }
                else
                {
                    Account.BalanceReserved -= -transactionMain.Amount + txFee;
                }
                DbContext.SaveChanges();
                //Log.Warning($"-Calling UpdateBalance for acc {Account.Id}");
                UpdateBalance(transactionCrypto.TimeExecuted);
                //Log.Warning($"-Calling UpdateBalance for acc {Account.Id} finished");
            }

            // Update the internal miner fee transaction
            if (transactionCrypto.InternalTx == null) DbContext.Entry(transactionCrypto).Reference("InternalTx").Load();
            var transactionInternalMinerFee = transactionCrypto.InternalTx;
            if (transactionInternalMinerFee != null)
            {
                transactionInternalMinerFee.Amount = transactionCrypto.MinerFee;
                transactionInternalMinerFee.Status = Database.STATUS_SUCCESS;
            }

            // Increment AmountReceived in AddressCrypto
            if (0 < transactionCrypto.Amount && !string.IsNullOrEmpty(transactionCrypto.Address))
            {
                var addressCrypto = DbContext.AddressCrypto.FirstOrDefault(a => a.Address == transactionCrypto.Address);
                if (addressCrypto != null) addressCrypto.AmountReceived += transactionCrypto.Amount;
            }

            // Send updates
            if (transactionMain != null)
            {
                EventHub.TriggerFunding(DbContext, transactionMain);
                SocketPusher.SendAccountUpdate(DbContext, transactionMain);
            }
        }

        public void FailTransaction(TransactionCrypto transactionCrypto)
        {
            Transaction transactionMain, transactionFee;
            decimal txFee = 0;

            if (transactionCrypto.UserTx == null) DbContext.Entry(transactionCrypto).Reference("UserTx").Load();
            transactionMain = transactionCrypto.UserTx;

            if (transactionMain != null)
            {
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

                DbContext.LockAndUpdate(ref Account);

                // Update account balance
                if (transactionMain.Amount < 0)
                {
                    Account.BalanceReserved -= -transactionMain.Amount + txFee;
                }
                else
                {
                    Log.Information($"CryptoAccount.FailTransaction update pending balance, account: {Account.Id}, balance: {Account.BalancePending}, new balance: {Account.BalancePending - transactionMain.Amount + txFee}, txid: {transactionCrypto.CryptoTxId}");
                    Account.BalancePending -= transactionMain.Amount + txFee; // TODO: Will not work if we take Fee for deposits. Trx with amount < 0 is not counted in BalancePending
                }
                DbContext.SaveChanges();
                UpdateBalance(transactionCrypto.TimeExecuted);
            }

            // Update the internal miner fee transaction
            if (transactionCrypto.InternalTx == null) DbContext.Entry(transactionCrypto).Reference("InternalTx").Load();
            var transactionInternalMinerFee = transactionCrypto.InternalTx;

            if (transactionInternalMinerFee != null) transactionInternalMinerFee.Status = Database.STATUS_FAILED;
        }

        // Can only cancel if the status is STATUS_READY or verification
        public void CancelTransaction(TransactionCrypto transactionCrypto, byte status = Database.STATUS_CANCELED)
        {
            Transaction transactionMain, transactionFee;
            decimal txFee = 0;

            if (transactionCrypto.UserTx == null) DbContext.Entry(transactionCrypto).Reference(a => a.UserTx).Load();
            transactionMain = transactionCrypto.UserTx;

            if (transactionMain != null)
            {
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

                // Update account balance
                if (transactionMain.Amount < 0)
                {
                    DbContext.LockAndUpdate(ref Account);

                    Account.BalanceReserved -= -transactionMain.Amount + txFee;
                    DbContext.SaveChanges();

                    SocketPusher.SendAccountUpdate(DbContext, transactionMain);
                }
                else
                {
                    // Nothing
                }
            }

            // Update the internal miner fee transaction
            if (transactionCrypto.InternalTx == null) DbContext.Entry(transactionCrypto).Reference("InternalTx").Load();
            var transactionInternalMinerFee = transactionCrypto.InternalTx;

            if (transactionInternalMinerFee != null) transactionInternalMinerFee.Status = Database.STATUS_FAILED;
        }

        public void ApproveTransaction(TransactionCrypto transactionCrypto)
        {
            DbContext.LockAndUpdate(ref transactionCrypto);

            if (transactionCrypto.Status != Database.STATUS_PENDING_ADMIN)
            {
                Log.Warning($"CryptoAccount.ApproveTransaction, crypto transaction {transactionCrypto.Id} is not waiting for approval.");
                return;
            }

            transactionCrypto.Status = Database.STATUS_NEW;
        }
    }
}

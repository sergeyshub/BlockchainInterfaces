using System;
using DataAccess;

namespace Assets.Fiat
{
    public class VirtualFiatTx
    {
        public const byte TYPE_PAYMENT = 1;
        public const byte TYPE_EXCHANGE = 2;
        public const byte TYPE_TRANSFER = 3;

        // DO NOT CHANGE: these statuses have to match TransactionExternal statuses!
        public const byte STATUS_READY = 0;
        public const byte STATUS_PENDING = 1;
        public const byte STATUS_COMPLETED = 2;
        public const byte STATUS_FAILED = 3;

        public string Txid;   // Set to external Id for exteral tx
        public byte Type;
        public string AssetCode;
        public string AccountNumber;
        public string PaymentMethod;
        public decimal Amount;
        public decimal Fee;
        public DateTime TimeAdded;
        public DateTime? TimeExecuted;
        public byte Status;
        public string FailureReason;
    }
}

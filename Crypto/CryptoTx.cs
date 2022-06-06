//using RippleDotNet.Responses.Transaction.TransactionTypes;
using System;

namespace Assets.Crypto
{
    public class CryptoTx
    {
        public string Txid;
        public string Address;
        public string AddressExt;
        public decimal Amount;
        public decimal Fee;
        public string Token;
        public int Confirmations;
        public ulong BlockNumber;
        public DateTime Time;
        public long? Nonce;
        public int? Vout;

        public CryptoTx(string txid, string address, string addressExt, decimal amount, decimal fee, string token, int confirmations, ulong blockNumber, DateTime time, long nonce)
        {
            Txid = txid;
            Address = address;
            AddressExt = addressExt;
            Amount = amount;
            Fee = fee;
            Token = token;
            Confirmations = confirmations;
            BlockNumber = blockNumber;
            Time = time;
            Nonce = nonce;
        }

        public CryptoTx(string txid, string address, string addressExt, decimal amount, decimal fee, string token, int confirmations, ulong blockNumber, DateTime time)
        {
            Txid = txid;
            Address = address;
            AddressExt = addressExt;
            Amount = amount;
            Fee = fee;
            Token = token;
            Confirmations = confirmations;
            BlockNumber = blockNumber;
            Time = time;
        }

        public CryptoTx(string txid, string address, string addressExt, decimal amount, decimal fee, int confirmations, ulong blockNumber, DateTime time, long nonce)
        {
            Txid = txid;
            Address = address;
            AddressExt = addressExt;
            Amount = amount;
            Fee = fee;
            Confirmations = confirmations;
            BlockNumber = blockNumber;
            Time = time;
            Nonce = nonce;
        }

        public CryptoTx(string txid, string address, string addressExt, decimal amount, decimal fee, int confirmations, DateTime time, int? vout)
        {
            Txid = txid;
            Address = address;
            AddressExt = addressExt;
            Amount = amount;
            Fee = fee;
            Confirmations = confirmations;
            Time = time;
            Vout = vout;
        }

        public CryptoTx()
        {

        }

        public static double ConvertToUnixTimestamp(DateTime date)
        {
            DateTime origin = new DateTime(1970, 1, 1, 0, 0, 0, 0);
            TimeSpan diff = date - origin;
            return Math.Floor(diff.TotalSeconds);
        }
    }
}

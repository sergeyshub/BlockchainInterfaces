using System;

namespace Assets.Crypto
{
    public class VirtualCryptoTx
    {
        public string Txid;
        public string Address;
        public string AddressExt;
        public decimal Amount;
        public decimal Fee;
        public int FeeAssetId;
        public string Token;
        public int Confirmations;
        public ulong BlockNumber;
        public long? Nonce;
        public int? Vout;
        public DateTime Time;
        public byte AddedBy = 0;
    }
}

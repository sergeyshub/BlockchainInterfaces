namespace Assets.Crypto
{
    public class VirtualFeeOption
    {
        public int AssetId;
        public decimal Fee;
        public int Blocks;
        public int Seconds;

        public VirtualFeeOption(int assetId, decimal fee, int blocks, int seconds)
        {
            Fee = fee;
            AssetId = assetId;
            Blocks = blocks;
            Seconds = seconds;
        }
    }
}

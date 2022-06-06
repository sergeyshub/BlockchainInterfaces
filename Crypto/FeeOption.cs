namespace Assets.Crypto
{
    public class FeeOption
    {
        public decimal Fee;
        public int Blocks;
        public int Seconds;

        public FeeOption(decimal fee, int blocks, int seconds)
        {
            Fee = fee;
            Blocks = blocks;
            Seconds = seconds;
        }
    }
}

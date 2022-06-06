namespace Assets
{
    public class Balance
    {
        // Total balance in the account
        public decimal Total = 0;

        // Balance reserved for delayed operations, i.e. sendTo
        public decimal Reserved = 0;

        // Balance with confimations below minimum
        public decimal Pending = 0;

        public Balance(decimal total, decimal reserved, decimal pending)
        {
            Total = total;
            Reserved = reserved;
            Pending = pending;
        }
    }
}

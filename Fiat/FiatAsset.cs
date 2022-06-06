using DataAccess;

namespace Assets.Fiat
{
    public abstract class FiatAsset : ProtoAsset
    {
        public FiatAsset(DatabaseContext dbContext, string assetCode) : base(dbContext, assetCode)
        {
        }
    }
}

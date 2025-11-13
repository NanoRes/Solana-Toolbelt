using System.Collections.Generic;
namespace Solana.Unity.Toolbelt
{
    internal sealed class LevelPricingCatalogAdapter : IPricingCatalogService
    {
        public LevelPricingCatalogAdapter(ILevelPricingData pricingData)
        {
            _ = pricingData;
        }

        public IEnumerable<object> GetRewardCoinPackages()
        {
            return System.Array.Empty<object>();
        }

        public IEnumerable<object> GetTokenMachinePrices()
        {
            return System.Array.Empty<object>();
        }
    }
}

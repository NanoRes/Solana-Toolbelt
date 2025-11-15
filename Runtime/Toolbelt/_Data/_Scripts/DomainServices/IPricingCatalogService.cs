using System.Collections.Generic;

namespace Solana.Unity.Toolbelt
{
    public interface IPricingCatalogService
    {
        IEnumerable<object> GetRewardCoinPackages();
        IEnumerable<object> GetTokenMachinePrices();
    }
}

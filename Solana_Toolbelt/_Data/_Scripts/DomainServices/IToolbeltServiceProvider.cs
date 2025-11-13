namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Provides read/write access to the services that bridge Solana Toolbelt runtime systems
    /// with the host game's domain layer.
    /// </summary>
    public interface IToolbeltServiceProvider
    {
        IToolbeltWalletService WalletService { get; }
        INftInventoryService NftInventoryService { get; }
        INftMetadataService NftMetadataService { get; }
        IPricingCatalogService PricingCatalogService { get; }
        ISolanaStorageService StorageService { get; }
        INftAccessService NftAccessService { get; }

        void NotifyTokenAccountsChanged(string ownerPublicKey = null, string collectionMint = null);

        void RegisterWalletService(IToolbeltWalletService service);
        void RegisterNftInventoryService(INftInventoryService service);
        void RegisterNftMetadataService(INftMetadataService service);
        void RegisterPricingCatalogService(IPricingCatalogService service);
        void RegisterStorageService(ISolanaStorageService service);
        void RegisterNftAccessService(INftAccessService service);
    }
}

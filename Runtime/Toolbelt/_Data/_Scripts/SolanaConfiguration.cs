using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using Solana.Unity.SDK;
using Solana.Unity.Toolbelt;
using Solana.Unity.Toolbelt.Wallet;
using Solana.Unity.Rpc;
using Solana.Unity.Rpc.Core.Http;
using Solana.Unity.Rpc.Messages;
using Solana.Unity.Rpc.Models;
using Solana.Unity.Rpc.Types;
using Solana.Unity.Wallet;
using Solana.Unity.Metaplex.Utilities;

[CreateAssetMenu(fileName = "Solana_Configuration", menuName = "Solana Toolbelt/Solana Configuration")]
public class SolanaConfiguration : ScriptableObject
{
    [System.Serializable]
    public class RpcEndpointConfig
    {
        [Tooltip("RPC endpoint URL")]
        public string url = "https://api.mainnet-beta.solana.com";

        [Tooltip("Lower values indicate higher priority when selecting an endpoint.")]
        public int priority = 0;

        [Tooltip("Timeout in seconds before a request is considered failed for this endpoint.")]
        public float requestTimeoutSeconds = 10f;

        [Tooltip("Maximum number of retry attempts before failing over to the next endpoint.")]
        public int maxRetries = 2;

        [Tooltip("Initial delay in seconds used for exponential backoff between retries.")]
        public float initialBackoffSeconds = 0.5f;

        [Tooltip("Maximum random jitter in seconds added to the retry delay.")]
        public float retryJitterSeconds = 0.25f;

        public RpcEndpointConfig Clone()
        {
            return new RpcEndpointConfig
            {
                url = url,
                priority = priority,
                requestTimeoutSeconds = requestTimeoutSeconds,
                maxRetries = maxRetries,
                initialBackoffSeconds = initialBackoffSeconds,
                retryJitterSeconds = retryJitterSeconds
            };
        }
    }

    [System.Serializable]
    public class CreatorShareConfig
    {
        [Tooltip("Creator address that should receive royalties on minted levels.")]
        public string address;

        [Tooltip("Royalty share allocated to this creator (0-100).")]
        [Range(0, 100)]
        public byte share = 10;

        [Tooltip("Whether this creator entry should be marked as verified in metadata.")]
        public bool verified = false;
    }

    public enum RegistryPauseExpectation
    {
        Unspecified = 0,
        Paused = 1,
        Unpaused = 2
    }

    [Header("RPC Settings")]
    public string[] rpcUrls = new string[]
    {
        "https://api.mainnet-beta.solana.com"
    };

    [Tooltip("Optional list that defines priority and retry behaviour per RPC endpoint. Leave empty to derive values from rpcUrls.")]
    public List<RpcEndpointConfig> rpcEndpointPriorityList = new();

    public string[] streamingRpcUrls = new string[]
    {
        "https://api.mainnet-beta.solana.com"
    };

    public int currentRPCIndex = 0;
    public int rpcRateLimit = 30;

    public List<RpcEndpointConfig> BuildRuntimeRpcEndpointConfigs()
    {
        var configs = new List<(RpcEndpointConfig Endpoint, int Order)>();

        if (rpcEndpointPriorityList != null && rpcEndpointPriorityList.Count > 0)
        {
            for (int i = 0; i < rpcEndpointPriorityList.Count; i++)
            {
                var endpoint = rpcEndpointPriorityList[i];
                if (endpoint == null || string.IsNullOrWhiteSpace(endpoint.url))
                    continue;

                configs.Add((endpoint.Clone(), i));
            }
        }
        else if (rpcUrls != null && rpcUrls.Length > 0)
        {
            for (int i = 0; i < rpcUrls.Length; i++)
            {
                string url = rpcUrls[i];
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                configs.Add((new RpcEndpointConfig
                {
                    url = url,
                    priority = i == currentRPCIndex ? int.MinValue : i + 1
                }, i));
            }
        }

        return configs
            .OrderBy(entry => entry.Endpoint.priority)
            .ThenBy(entry => entry.Order)
            .Select(entry => entry.Endpoint)
            .ToList();
    }

    public List<string> GetOrderedRpcUrls()
    {
        return BuildRuntimeRpcEndpointConfigs()
            .Select(endpoint => endpoint.url)
            .ToList();
    }

    [Header("Studio Wallets")]
    public string studioSOLWalletPublicKey;
    public string studioGhibliWalletPublicKey;
    public string studioRewardsTokenWalletPublicKey;

    [Header("Token Purchase Links")]
    [Tooltip("URL where players can acquire more SOL for on-chain purchases.")]
    public string solPurchaseUrl = string.Empty;

    [Tooltip("URL where players can acquire additional Ghibli tokens.")]
    public string ghibliPurchaseUrl = string.Empty;

    [Tooltip("URL where players can acquire additional reward tokens.")]
    public string rewardTokenPurchaseUrl = string.Empty;

    [Header("Token Mint Addresses")]
    public string ghibliTokenMint;
    public string rewardTokenMint;

    [Header("Currency Icons")]
    public Sprite ghibliTokenIcon;
    public Sprite solTokenIcon;
    public Sprite rewardCoinIcon;

    [Header("NFT Collections")]
    public NFTCollectionConfig alphaCollection;
    public NFTCollectionConfig betaCollection;
    public NFTCollectionConfig levelCreatorCollection;

    [Header("Owner-Governed Asset Ledger")]
    [Tooltip("Program ID of the OwnerGovernedAssetLedger Anchor program")]
    public string ownerGovernedAssetLedgerProgramId;
    [Tooltip("Collection mint address that all level NFTs verify against")]
    public string levelsCollectionMint;
    [Tooltip("Namespace public key used to derive the registry configuration PDA")]
    public string ownerGovernedAssetLedgerNamespace;
    [Tooltip("Optional override for the registry config PDA if you don't want to derive it from a namespace.")]
    public string ownerGovernedAssetLedgerConfigAccount;
    [Tooltip("Optional override for the registry mint authority PDA if you don't want to derive it from a namespace.")]
    public string ownerGovernedAssetLedgerAuthorityAccount;
    [Tooltip("Optional expected config authority public key used for UI display or validation.")]
    public string ownerGovernedAssetLedgerExpectedConfigAuthority;
    [Tooltip("Optional expected paused status for the registry configuration.")]
    public RegistryPauseExpectation ownerGovernedAssetLedgerExpectedPauseState = RegistryPauseExpectation.Unspecified;
    [Tooltip("Compute unit limit requested for OGAL mint transactions. Values <= 0 fall back to the default limit.")]
    public uint ownerGovernedAssetLedgerMintComputeUnitLimit = 400000;
    [Tooltip("Optional compute unit price (in microlamports) for OGAL mint transactions. Set to 0 to omit the price instruction.")]
    public ulong ownerGovernedAssetLedgerMintComputeUnitPriceMicroLamports = 0;
    [Tooltip("Number of transport retries to attempt on the primary RPC endpoint before failing over.")]
    public int ownerGovernedAssetLedgerMintTransportRetryCount = 2;
    [Tooltip("Initial backoff delay in milliseconds used when retrying mint submissions after transport failures.")]
    public int ownerGovernedAssetLedgerMintTransportRetryDelayMilliseconds = 500;
    [Tooltip("Optional secondary RPC endpoint used for mint submissions after primary transport retries are exhausted.")]
    public string ownerGovernedAssetLedgerSecondaryRpcUrl;
    public OwnerGovernedAssetLedgerService ownerGovernedAssetLedgerService;

    [Tooltip("Discount applied on the special day (e.g. 0.25 = 25%)")]
    public float thirteenthDiscountPercent = 0.25f;

    [Tooltip("Calendar day that triggers the special discount")] 
    public int specialDiscountDay = 13;

    [Tooltip("Default discount for token skins (e.g. 0.13 = 13%)")]
    public float defaultDiscountPercent = 0.13f;

    [Header("Transaction Memo Settings")]
    [Tooltip("Memo strings used when scanning transaction history.")]
    public string[] transactionMemoSearchStrings = Array.Empty<string>();
    [Tooltip("Optional program IDs that must be present when scanning memos. Leave empty to match any program.")]
    public string[] transactionMemoProgramFilters;
    [Tooltip("Optional destination accounts that must be present when scanning memos. Leave empty to match any destination.")]
    public string[] transactionMemoDestinationFilters;
    [Tooltip("Default maximum number of signatures to inspect when querying transaction history.")]
    public int transactionMemoSignatureLimit = 50;
    [Tooltip("If true, memo comparisons are case sensitive when scanning transaction history.")]
    public bool transactionMemoCaseSensitive = false;
    [Tooltip("Memo text attached to level editor unlock transactions.")]
    public string levelEditorUnlockMemo = string.Empty;
    [Tooltip("Memo format for reward coin bundles. {0} is replaced with the bundle index.")]
    public string rewardBundleMemoFormat = string.Empty;
    [Tooltip("Memo text used when the token machine spins via SOL/SPL transactions.")]
    public string tokenMachineSpinMemo = string.Empty;
    [Tooltip("Memo format for token skin unlocks. {0} is the token index, {1} is the variation identifier.")]
    public string tokenSkinUnlockMemoFormat = string.Empty;
    [Tooltip("Memo text attached to user-generated NFT mint transactions.")]
    public string userGeneratedMintMemo = string.Empty;
    [Tooltip("Memo displayed when requesting wallet ownership verification.")]
    public string walletVerificationMemo = string.Empty;

    private const int DefaultMetadataAccountDataLength = 607;

    [Header("Mint Cost Settings (Lamports)")]
    public ulong mintAccountRentDepositLamports = 1461600;
    public ulong ataRentDepositLamports = 2039280;
    public ulong metadataRentDepositLamports = 5115600;
    [Tooltip("Rent deposit (in lamports) required to initialize an object manifest account.")]
    public ulong manifestRentDepositLamports = 2895360;
    public ulong transactionFeeLamports = 5000;

    [Header("Caching Settings (in hours)")]
    public float nftCacheDurationHours = 6f;
    public float nftCountCacheDurationHours = 12f;

    [Header("Wallet Inventory Cache")]
    [Tooltip("Time in seconds that wallet token account lookups remain cached before refreshing from RPC.")]
    [Min(0f)]
    [SerializeField]
    private float tokenAccountCacheTtlSeconds = 15f;
    private float mintDetailsCacheTtlSeconds = 15f;

    [Header("Transaction Cache")]
    [Tooltip("How long (in seconds) recent blockhash lookups remain cached before requesting a fresh value. Set to 0 to disable caching.")]
    [Min(0f)]
    [SerializeField]
    private float recentBlockhashCacheTtlSeconds = 120f;

    [Header("Toolbelt Services")]
    [SerializeField] private WalletManager walletManager;
    [SerializeField] private SolanaNFTMintService tokenMintService;
    [SerializeField] private MetadataQueryService metadataQueryService;
    [SerializeField] private SolanaNftAccessManager nftAccessManager;
    [Tooltip("Component responsible for uploading level JSON off-chain")]
    public ILevelJsonUploader jsonUploader;
    [Tooltip("Component that uploads NFT media and metadata off-chain")]
    public INftStorageUploader nftStorageUploader;
    [Header("Level Minting")]
    [Tooltip("Royalty share allocated to the player/level creator when minting levels (0-100).")]
    [Range(0, 100)]
    public byte levelCreatorPrimaryShare = 90;
    [Tooltip("When enabled the player/level creator entry is marked as verified in the minted metadata.")]
    public bool verifyLevelCreator = true;
    [Tooltip("Android-specific override for level creator verification. Enable only when the active Android wallet can sign creator metadata.")]
    public bool verifyLevelCreatorOnAndroid = true;
    [Tooltip("Additional creators and royalty splits applied to minted levels.")]
    public List<CreatorShareConfig> levelStudioCreators = new();
    public UserGeneratedNftMintService userGeneratedNftMintService;

    [NonSerialized]
    private IToolbeltUiBridge uiBridge;

    [NonSerialized]
    private ILevelPricingData pricingData;

    [NonSerialized]
    private IPricingCatalogService pricingCatalogService;

    [NonSerialized]
    private ISolanaStorageService runtimeStorageService;

    [NonSerialized]
    private ILevelMintPaymentLedger runtimeMintPaymentLedger;

    private ISolanaStorageService ActiveStorageService => runtimeStorageService;

    public ILevelMintPaymentLedger MintPaymentLedger => runtimeMintPaymentLedger;

    [Header("Events")]
    public UnityEvent onWalletConnected;
    public UnityEvent onWalletDisconnected;
    public UnityEvent onNFTMinted;
    public UnityEvent onTransactionFailed;
    public UnityEvent<string> onWalletConnectionFailed;
    public UnityEvent<string> onWalletVerified;
    public UnityEvent<ulong> onSolBalanceChanged;
    public UnityEvent<bool> onWalletStreamingHealthChanged;

    public WalletManager WalletManagerInstance => walletManager;
    internal SolanaNFTMintService TokenMintServiceInstance => tokenMintService;
    internal MetadataQueryService MetadataQueryServiceInstance => metadataQueryService;
    internal SolanaNftAccessManager NftAccessManagerInstance => nftAccessManager;

    public SolanaNFTMintService TokenMintService => tokenMintService;
    public SolanaNftAccessManager NftAccessManager => nftAccessManager;

    public INftAccessService NftAccessService => serviceProvider?.NftAccessService ?? nftAccessManager;

    public TimeSpan TokenAccountCacheTimeToLive =>
        TimeSpan.FromSeconds(Mathf.Max(0f, tokenAccountCacheTtlSeconds));

    public TimeSpan MintDetailsCacheTimeToLive =>
        TimeSpan.FromSeconds(Mathf.Max(0f, mintDetailsCacheTtlSeconds));

    public int RecentBlockhashCacheMaxSeconds =>
        Mathf.RoundToInt(Mathf.Max(0f, recentBlockhashCacheTtlSeconds));

    private bool walletConnectedListenerRegistered;
    private bool walletDisconnectedListenerRegistered;
    [NonSerialized]
    private IToolbeltServiceProvider serviceProvider;

    [NonSerialized]
    private readonly SemaphoreSlim runtimeServicesLock = new(1, 1);

    [NonSerialized]
    private int? runtimeMetadataAccountDataLength;

    [NonSerialized]
    private Task runtimeServicesTask = Task.CompletedTask;

    [NonSerialized]
    private IWeb3Facade web3Facade = new Web3Facade();

    [NonSerialized]
    private RpcEndpointManager rpcEndpointManager;

    private readonly object rpcEndpointManagerLock = new();

    private IWeb3Facade Web3Facade => web3Facade ??= new Web3Facade();

    public IToolbeltServiceProvider ServiceProvider => serviceProvider;

    public IToolbeltUiBridge UiBridge => uiBridge;

    public IToolbeltUiBridge PopupPresenter => uiBridge;

    public ILevelPricingData LevelPricingData => pricingData;

    [Obsolete("Use GetRpcClientAsync to avoid blocking the Unity main thread.")]
    public IRpcClient GetRpcClient()
    {
        throw new InvalidOperationException("Use GetRpcClientAsync to avoid blocking the Unity main thread.");
    }

    public Task<IRpcClient> GetRpcClientAsync()
    {
        return Task.FromResult(Web3Facade.RpcClient);
    }

    internal IRpcEndpointManager GetOrCreateRpcEndpointManager()
    {
        if (rpcEndpointManager != null)
        {
            return rpcEndpointManager;
        }

        lock (rpcEndpointManagerLock)
        {
            rpcEndpointManager ??= new RpcEndpointManager(this);
            return rpcEndpointManager;
        }
    }

    public void ApplyRpcRateLimit()
    {
        if (Web3.Instance == null)
        {
            return;
        }

        Web3.Instance.RpcMaxHits = rpcRateLimit;
        Web3.Instance.RpcMaxHitsPerSeconds = rpcRateLimit;
    }

    public void SetServiceProvider(IToolbeltServiceProvider provider)
    {
        if (serviceProvider == provider)
        {
            return;
        }

        if (serviceProvider != null)
        {
            serviceProvider.RegisterWalletService(null);
            serviceProvider.RegisterNftInventoryService(null);
            serviceProvider.RegisterNftMetadataService(null);
            serviceProvider.RegisterPricingCatalogService(null);
            serviceProvider.RegisterStorageService(null);
            serviceProvider.RegisterNftAccessService(null);
        }

        serviceProvider = provider;

        if (serviceProvider != null)
        {
            RefreshDomainAdapters();
        }
    }

    private void OnEnable()
    {
        if (!HasCoreServicesConfigured())
        {
            return;
        }

        RegisterWalletEventHandlers();
        RebuildRuntimeServices();
    }

    private void OnDisable()
    {
        UnregisterWalletEventHandlers();
        ClearMintServices();
        ownerGovernedAssetLedgerService = null;
        if (serviceProvider != null)
        {
            serviceProvider.RegisterWalletService(null);
            serviceProvider.RegisterNftInventoryService(null);
            serviceProvider.RegisterNftMetadataService(null);
            serviceProvider.RegisterPricingCatalogService(null);
            serviceProvider.RegisterStorageService(null);
            serviceProvider.RegisterNftAccessService(null);
        }
        runtimeMintPaymentLedger?.Dispose();
        runtimeMintPaymentLedger = null;
        runtimeStorageService = null;
    }

    private void RegisterWalletEventHandlers()
    {
        if (onWalletConnected != null && !walletConnectedListenerRegistered)
        {
            onWalletConnected.AddListener(HandleWalletConnected);
            walletConnectedListenerRegistered = true;
        }

        if (onWalletDisconnected != null && !walletDisconnectedListenerRegistered)
        {
            onWalletDisconnected.AddListener(HandleWalletDisconnected);
            walletDisconnectedListenerRegistered = true;
        }
    }

    private void UnregisterWalletEventHandlers()
    {
        if (onWalletConnected != null && walletConnectedListenerRegistered)
        {
            onWalletConnected.RemoveListener(HandleWalletConnected);
            walletConnectedListenerRegistered = false;
        }

        if (onWalletDisconnected != null && walletDisconnectedListenerRegistered)
        {
            onWalletDisconnected.RemoveListener(HandleWalletDisconnected);
            walletDisconnectedListenerRegistered = false;
        }
    }

    private void HandleWalletConnected()
    {
        RebuildRuntimeServices();
    }

    private void HandleWalletDisconnected()
    {
        ClearMintServices();
    }

    private void RebuildRuntimeServices()
    {
        var task = RebuildRuntimeServicesAsync();
        runtimeServicesTask = task;
        task.ContinueWith(
            t => Debug.LogError($"[SolanaConfiguration] Failed to rebuild runtime services: {t.Exception?.GetBaseException().Message}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private async Task RebuildRuntimeServicesAsync()
    {
        if (!HasCoreServicesConfigured())
        {
            return;
        }

        await runtimeServicesLock.WaitAsync();
        try
        {
            ClearMintServices();

            IRpcClient rpcClient = Web3Facade.RpcClient;

            if (rpcClient == null)
            {
                Debug.LogWarning("[SolanaConfiguration] Solana RPC client is unavailable; runtime services will be limited.");
                metadataQueryService = null;
                RefreshDomainAdapters();
                return;
            }

            try
            {
                metadataQueryService = await MetadataQueryService.CreateAsync(rpcClient);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SolanaConfiguration] Failed to initialize MetadataQueryService: {ex.Message}");
                metadataQueryService = null;
            }

            bool isPlaying = Application.isPlaying;

            await RefreshMetadataRentDepositAsync(rpcClient, CancellationToken.None);

            RefreshDomainAdapters();

            tokenMintService = TryCreateTokenMintService(rpcClient, isPlaying);

            if (tokenMintService != null && nftStorageUploader != null)
            {
                userGeneratedNftMintService = new UserGeneratedNftMintService(tokenMintService, nftStorageUploader);
            }
            else
            {
                userGeneratedNftMintService = null;
            }

            await BuildOwnerGovernedAssetLedgerServiceAsync(rpcClient);
        }
        finally
        {
            runtimeServicesLock.Release();
        }
    }

    public bool HasCoreServicesConfigured()
    {
        return walletManager != null &&
               nftAccessManager != null &&
               jsonUploader != null &&
               uiBridge != null &&
               ActiveStorageService != null;
    }

    public void SetUiBridge(IToolbeltUiBridge bridge)
    {
        uiBridge = bridge;

        if (nftAccessManager != null)
        {
            nftAccessManager.SetUiBridge(bridge);
        }
    }

    public void SetPopupPresenter(IToolbeltUiBridge bridge)
    {
        SetUiBridge(bridge);
    }

    public void SetLevelPricingData(ILevelPricingData data)
    {
        pricingData = data;

        if (nftAccessManager != null)
        {
            nftAccessManager.SetLevelPricingData(data);
        }

        pricingCatalogService = data as IPricingCatalogService ??
            (data != null ? new LevelPricingCatalogAdapter(data) : null);

        RefreshDomainAdapters();
    }

    public void SetStorageService(ISolanaStorageService service)
    {
        if (ReferenceEquals(runtimeStorageService, service))
        {
            runtimeStorageService = service;
            ApplyStorageService();
            return;
        }

        runtimeStorageService = service;
        ResetMintPaymentLedger(service);
        ApplyStorageService();
    }

    public void InitializeToolbeltServices(
        WalletManager walletManager,
        SolanaNftAccessManager nftAccessManager,
        ILevelJsonUploader jsonUploader,
        IToolbeltUiBridge uiBridge,
        ISolanaStorageService storageOverride = null,
        INftStorageUploader nftStorageUploader = null,
        MetadataQueryService metadataQueryService = null)
    {
        this.walletManager = walletManager;
        this.nftAccessManager = nftAccessManager;
        this.jsonUploader = jsonUploader;
        SetUiBridge(uiBridge);

        if (storageOverride != null)
        {
            SetStorageService(storageOverride);
        }
        else
        {
            ApplyStorageService();
        }

        if (pricingData != null && this.nftAccessManager != null)
        {
            this.nftAccessManager.SetLevelPricingData(pricingData);
        }

        if (nftStorageUploader != null)
        {
            this.nftStorageUploader = nftStorageUploader;
        }

        if (metadataQueryService != null)
        {
            this.metadataQueryService = metadataQueryService;
        }

        RefreshDomainAdapters();

        if (!HasCoreServicesConfigured())
        {
            return;
        }

        RegisterWalletEventHandlers();
        RebuildRuntimeServices();
    }

    [Obsolete("Use EnsureMetadataQueryServiceAsync to avoid blocking the Unity main thread.")]
    public MetadataQueryService EnsureMetadataQueryService()
    {
        throw new InvalidOperationException("Use EnsureMetadataQueryServiceAsync instead.");
    }

    public async Task<MetadataQueryService> EnsureMetadataQueryServiceAsync()
    {
        if (metadataQueryService != null)
        {
            return metadataQueryService;
        }

        var rpcClient = Web3Facade.RpcClient;
        if (rpcClient == null)
        {
            return null;
        }

        try
        {
            metadataQueryService = await MetadataQueryService.CreateAsync(rpcClient);
            RefreshDomainAdapters();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SolanaConfiguration] Failed to initialize MetadataQueryService: {ex.Message}");
            metadataQueryService = null;
            RefreshDomainAdapters();
        }

        return metadataQueryService;
    }

    public IMintRequestFactory CreateLevelMintRequestFactory()
    {
        return new DefaultMintRequestFactory();
    }

    public ulong GetMintPriorityFeeLamports()
    {
        if (ownerGovernedAssetLedgerMintComputeUnitLimit == 0 ||
            ownerGovernedAssetLedgerMintComputeUnitPriceMicroLamports == 0)
        {
            return 0UL;
        }

        decimal microLamports =
            (decimal)ownerGovernedAssetLedgerMintComputeUnitLimit *
            (decimal)ownerGovernedAssetLedgerMintComputeUnitPriceMicroLamports;

        if (microLamports <= 0m)
        {
            return 0UL;
        }

        decimal lamports = microLamports / 1_000_000m;

        if (lamports <= 0m)
        {
            return 0UL;
        }

        if (lamports >= ulong.MaxValue)
        {
            return ulong.MaxValue;
        }

        return (ulong)lamports;
    }

    public bool ShouldVerifyLevelCreatorForActiveWallet()
    {
        return ShouldVerifyLevelCreatorForSession(walletManager?.Session, Application.platform);
    }

    internal bool ShouldVerifyLevelCreatorForSession(IWalletSessionService session, RuntimePlatform platform)
    {
        if (!verifyLevelCreator)
        {
            LogCreatorVerificationRejection(platform, "level creator verification is disabled in configuration.");
            return false;
        }

        if (platform == RuntimePlatform.Android && !verifyLevelCreatorOnAndroid)
        {
            LogCreatorVerificationRejection(platform, "Android verification has been disabled in configuration.");
            return false;
        }

        if (session == null)
        {
            LogCreatorVerificationRejection(platform, "no wallet session is currently active.");
            return false;
        }

        var wallet = session.WalletBase;
        if (wallet == null)
        {
            LogCreatorVerificationRejection(platform, "no active wallet is available for the current session.");
            return false;
        }

        if (wallet is SessionWallet)
        {
            var externalAuthority = SessionWallet.ExternalAuthorityPublicKey;
            var currentPublicKey = session.CurrentPublicKey;

            if (externalAuthority == null || currentPublicKey == null)
            {
                LogCreatorVerificationRejection(platform, "the session wallet does not expose a co-signing external authority.");
                return false;
            }

            if (externalAuthority.Equals(currentPublicKey))
            {
                LogCreatorVerificationRejection(platform, "the session wallet external authority matches the session public key and cannot co-sign.");
                return false;
            }
        }

        if (!session.IsWalletVerified)
        {
            LogCreatorVerificationRejection(platform, "the active wallet has not completed verification.");
            return false;
        }

        return true;
    }

    private static void LogCreatorVerificationRejection(RuntimePlatform platform, string reason)
    {
        if (platform != RuntimePlatform.Android)
        {
            return;
        }

        Debug.LogWarning($"[SolanaConfiguration] Skipping level creator verification because {reason}");
    }

    private Task BuildOwnerGovernedAssetLedgerServiceAsync(IRpcClient rpcClient)
    {
        ownerGovernedAssetLedgerService = null;
        bool hasNamespace = !string.IsNullOrEmpty(ownerGovernedAssetLedgerNamespace);
        bool hasConfigAccount = !string.IsNullOrEmpty(ownerGovernedAssetLedgerConfigAccount);

        if (string.IsNullOrEmpty(ownerGovernedAssetLedgerProgramId) ||
            string.IsNullOrEmpty(levelsCollectionMint) ||
            (!hasNamespace && !hasConfigAccount))
        {
            return Task.CompletedTask;
        }

        if (rpcClient == null)
        {
            rpcClient = Web3Facade.RpcClient;
        }

        if (rpcClient == null)
        {
            Debug.LogWarning("[SolanaConfiguration] OwnerGovernedAssetLedgerService unavailable: RPC client is not ready.");
            return Task.CompletedTask;
        }

        IRpcClient secondaryRpcClient = null;
        if (!string.IsNullOrWhiteSpace(ownerGovernedAssetLedgerSecondaryRpcUrl))
        {
            try
            {
                secondaryRpcClient = ClientFactory.GetClient(ownerGovernedAssetLedgerSecondaryRpcUrl);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SolanaConfiguration] Failed to initialize OwnerGovernedAssetLedgerService secondary RPC client: {ex.Message}");
            }
        }

        try
        {
            ownerGovernedAssetLedgerService = new OwnerGovernedAssetLedgerService(
                rpcClient,
                () => walletManager.Session?.WalletBase,
                ownerGovernedAssetLedgerProgramId,
                levelsCollectionMint,
                ownerGovernedAssetLedgerNamespace,
                ownerGovernedAssetLedgerConfigAccount,
                ownerGovernedAssetLedgerAuthorityAccount,
                RecentBlockhashCacheMaxSeconds,
                ownerGovernedAssetLedgerMintComputeUnitLimit,
                ownerGovernedAssetLedgerMintComputeUnitPriceMicroLamports > 0
                    ? ownerGovernedAssetLedgerMintComputeUnitPriceMicroLamports
                    : (ulong?)null,
                secondaryRpcClient,
                ownerGovernedAssetLedgerMintTransportRetryCount,
                ownerGovernedAssetLedgerMintTransportRetryDelayMilliseconds);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SolanaConfiguration] Failed to initialize OwnerGovernedAssetLedgerService: {ex.Message}");
            ownerGovernedAssetLedgerService = null;
        }

        return Task.CompletedTask;
    }

    private void ClearMintServices()
    {
        tokenMintService = null;
        userGeneratedNftMintService = null;
    }

    private void RefreshDomainAdapters()
    {
        ApplyStorageService();

        if (serviceProvider == null)
        {
            return;
        }

        serviceProvider.RegisterWalletService(
            walletManager != null ? new WalletManagerToolbeltAdapter(walletManager) : null);

        var rpcManager = GetOrCreateRpcEndpointManager();
        serviceProvider.RegisterNftInventoryService(
            new RpcNftInventoryAdapter(
                () => rpcManager.TryGetCurrentClient() ?? Web3Facade.RpcClient,
                TokenAccountCacheTimeToLive,
                MintDetailsCacheTimeToLive,
                message => Debug.Log($"[RpcNftInventoryAdapter] {message}"),
                rpcEndpointManager: rpcManager));

        serviceProvider.RegisterNftMetadataService(
            metadataQueryService != null ? new MetadataQueryServiceAdapter(metadataQueryService) : null);

        serviceProvider.RegisterPricingCatalogService(pricingCatalogService);
        serviceProvider.RegisterNftAccessService(nftAccessManager);
    }

    private void ApplyStorageService()
    {
        var active = ActiveStorageService;
        if (nftAccessManager != null)
        {
            nftAccessManager.SetStorageService(active);
        }

        if (runtimeMintPaymentLedger == null && active != null)
        {
            runtimeMintPaymentLedger = new LevelMintPaymentLedger(active);
        }

        serviceProvider?.RegisterStorageService(active);
    }

    private void ResetMintPaymentLedger(ISolanaStorageService storageService)
    {
        runtimeMintPaymentLedger?.Dispose();
        runtimeMintPaymentLedger = storageService != null
            ? new LevelMintPaymentLedger(storageService)
            : null;
    }

    private async Task RefreshMetadataRentDepositAsync(IRpcClient rpcClient, CancellationToken cancellationToken)
    {
        if (rpcClient == null)
        {
            return;
        }

        try
        {
            int dataLength = await DetermineMetadataAccountDataLengthAsync(rpcClient, cancellationToken)
                .ConfigureAwait(false);
            var rentResult = await rpcClient
                .GetMinimumBalanceForRentExemptionAsync(dataLength, Commitment.Confirmed)
                .ConfigureAwait(false);

            if (!rentResult.WasSuccessful)
            {
                Debug.LogWarning(
                    $"[SolanaConfiguration] Failed to refresh metadata rent deposit: {rentResult.Reason ?? "RPC request failed."}");
                return;
            }

            ulong lamports = rentResult.Result;
            runtimeMetadataAccountDataLength = dataLength;
            metadataRentDepositLamports = lamports;

            Debug.Log(
                $"[SolanaConfiguration] Metadata rent deposit updated to {lamports} lamports (data length {dataLength}).");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            Debug.LogWarning(
                $"[SolanaConfiguration] Metadata rent refresh timed out: {ex.Message}. Continuing with existing values.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SolanaConfiguration] Failed to refresh metadata rent deposit: {ex.Message}");
        }
    }

    private async Task<int> DetermineMetadataAccountDataLengthAsync(IRpcClient rpcClient, CancellationToken cancellationToken)
    {
        if (rpcClient == null)
        {
            return DefaultMetadataAccountDataLength;
        }

        if (!string.IsNullOrWhiteSpace(levelsCollectionMint))
        {
            try
            {
                var collectionMint = new PublicKey(levelsCollectionMint);
                var metadataPda = PDALookupExtensions.FindMetadataPDA(collectionMint);
                var accountInfo = await rpcClient
                    .GetAccountInfoAsync(metadataPda.Key, Commitment.Confirmed)
                    .ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                var data = accountInfo?.Result?.Value?.Data;
                if (data != null && data.Count > 0 && !string.IsNullOrEmpty(data[0]))
                {
                    try
                    {
                        var raw = Convert.FromBase64String(data[0]);
                        if (raw?.Length > 0)
                        {
                            return raw.Length;
                        }
                    }
                    catch (FormatException ex)
                    {
                        Debug.LogWarning(
                            $"[SolanaConfiguration] Unable to decode collection metadata account data: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[SolanaConfiguration] Failed to determine metadata account size from collection '{levelsCollectionMint}': {ex.Message}");
            }
        }

        return runtimeMetadataAccountDataLength ?? DefaultMetadataAccountDataLength;
    }

    private SolanaNFTMintService TryCreateTokenMintService(IRpcClient rpcClient, bool isPlaying)
    {
        if (rpcClient == null)
        {
            return null;
        }

        var walletBase = walletManager != null ? walletManager.Session?.WalletBase : null;
        if (walletBase == null)
        {
            if (isPlaying)
                Debug.LogWarning("[SolanaConfiguration] Unable to create SolanaNFTMintService because no wallet is connected.");
            return null;
        }

        try
        {
            return new SolanaNFTMintService(rpcClient, walletBase, RecentBlockhashCacheMaxSeconds);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SolanaConfiguration] Failed to initialize SolanaNFTMintService: {ex.Message}");
            return null;
        }
    }

    public string GetLevelEditorUnlockMemo() => RequireConfiguredString(levelEditorUnlockMemo, nameof(levelEditorUnlockMemo));

    public string FormatRewardBundleMemo(int index)
    {
        string format = RequireConfiguredString(rewardBundleMemoFormat, nameof(rewardBundleMemoFormat));
        return string.Format(format, index);
    }

    public string GetTokenMachineMemo() => RequireConfiguredString(tokenMachineSpinMemo, nameof(tokenMachineSpinMemo));

    public string FormatTokenSkinMemo(int tokenIndex, string variationLabel)
    {
        string format = RequireConfiguredString(tokenSkinUnlockMemoFormat, nameof(tokenSkinUnlockMemoFormat));
        return string.Format(format, tokenIndex, variationLabel ?? string.Empty);
    }

    public async Task<BundlrFundingSnapshot> GetBundlrFundingSnapshotAsync(CancellationToken cancellationToken)
    {
        var bundlrUploader = jsonUploader as BundlrUploader
            ?? throw new InvalidOperationException("Bundlr uploader is not configured.");

        var walletService = serviceProvider?.WalletService;
        if (walletService != null && walletService.IsWalletConnected)
        {
            await BundlrWalletKeyStore.EnsureBundlrSignerAsync(bundlrUploader, walletService.CurrentPublicKey)
                .ConfigureAwait(false);
        }

        string bundlrWallet = bundlrUploader.GetUploaderWalletAddress();
        ulong walletLamports = 0UL;

        var rpcClient = Web3Facade.RpcClient;
        if (!string.IsNullOrWhiteSpace(bundlrWallet) && rpcClient != null)
        {
            walletLamports = await bundlrUploader
                .GetUploaderWalletLamportsAsync(rpcClient, Commitment.Confirmed, cancellationToken)
                .ConfigureAwait(false);
        }

        BigInteger bundlrBalance = await bundlrUploader
            .GetBundlrBalanceAsync(cancellationToken)
            .ConfigureAwait(false);

        return new BundlrFundingSnapshot(bundlrWallet, walletLamports, bundlrBalance, bundlrUploader.GetEstimatedFundingFeeLamports());
    }

    public async Task<BundlrTopUpResult> TopUpBundlrAsync(
        double amountSol,
        RpcCommitment commitment,
        string memo,
        CancellationToken cancellationToken)
    {
        var bundlrUploader = jsonUploader as BundlrUploader;
        if (bundlrUploader == null)
        {
            return new BundlrTopUpResult(false, null, "Bundlr uploader is not configured.");
        }

        var walletService = serviceProvider?.WalletService;
        if (walletService == null)
        {
            return new BundlrTopUpResult(false, null, "Wallet service unavailable.");
        }

        if (!walletService.IsWalletConnected)
        {
            return new BundlrTopUpResult(false, null, "Connect your wallet before funding Bundlr.");
        }

        await BundlrWalletKeyStore.EnsureBundlrSignerAsync(bundlrUploader, walletService.CurrentPublicKey)
            .ConfigureAwait(false);

        string bundlrWallet = bundlrUploader.GetUploaderWalletAddress();
        if (string.IsNullOrWhiteSpace(bundlrWallet))
        {
            return new BundlrTopUpResult(false, null, "Bundlr wallet address is unavailable.");
        }

        ulong transferLamports = SolanaValueUtils.SolToLamports(amountSol);
        string fundingMemo = memo ?? BuildBundlrFundingMemo(transferLamports);

        BigInteger initialBalance = await bundlrUploader.GetBundlrBalanceAsync(cancellationToken).ConfigureAwait(false);

        var transferResult = await walletService.TransferSolAsync(
            bundlrWallet,
            transferLamports,
            commitment,
            fundingMemo).ConfigureAwait(false);

        if (transferResult == null || !transferResult.Success)
        {
            string reason = transferResult?.Error ?? "Unknown error";
            return new BundlrTopUpResult(false, transferResult?.Signature, $"Bundlr funding transaction failed: {reason}");
        }

        var rpcClient = Web3Facade.RpcClient;

        if (rpcClient != null)
        {
            ulong depositLamports = CalculateBundlrDepositLamports(transferLamports, bundlrUploader);
            try
            {
                await bundlrUploader.TryDepositAsync(rpcClient, depositLamports, cancellationToken, throwOnFailure: true)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new BundlrTopUpResult(false, transferResult.Signature, $"Bundlr deposit failed: {ex.Message}");
            }
        }

        bool credited = await WaitForBundlrCreditAsync(bundlrUploader, initialBalance, cancellationToken)
            .ConfigureAwait(false);

        if (!credited)
        {
            return new BundlrTopUpResult(false, transferResult.Signature, "Bundlr balance did not update after funding.");
        }

        return new BundlrTopUpResult(true, transferResult.Signature, null);
    }

    public async Task<string> UploadJsonWithBundlrAsync(
        string fileName,
        string json,
        RpcCommitment commitment,
        double fundingBufferPercent,
        ulong minimumFundingLamports,
        CancellationToken cancellationToken)
    {
        Debug.Log(
            $"[SolanaConfiguration] UploadJsonWithBundlrAsync invoked. fileName='{fileName}', jsonLength={json?.Length ?? 0}, " +
            $"commitment={commitment}, bufferPercent={fundingBufferPercent}, minimumFundingLamports={minimumFundingLamports}.");
        if (jsonUploader == null)
        {
            throw new InvalidOperationException("JSON uploader is not configured.");
        }

        if (jsonUploader is not BundlrUploader bundlrUploader)
        {
            Debug.Log("[SolanaConfiguration] JSON uploader is not Bundlr. Delegating to configured uploader.");
            string uri = await jsonUploader.UploadJsonAsync(fileName, json).ConfigureAwait(false);
            Debug.Log($"[SolanaConfiguration] Non-Bundlr upload completed. uri='{uri}'.");
            return uri;
        }

        var walletService = serviceProvider?.WalletService
            ?? throw new InvalidOperationException("Wallet service unavailable for Bundlr uploads.");

        if (!walletService.IsWalletConnected)
        {
            throw new InvalidOperationException("Connect your wallet before uploading metadata to Bundlr.");
        }

        await BundlrWalletKeyStore.EnsureBundlrSignerAsync(bundlrUploader, walletService.CurrentPublicKey)
            .ConfigureAwait(false);

        var rpcClient = await RequireRpcClientAsync().ConfigureAwait(false);
        if (rpcClient == null)
        {
            throw new InvalidOperationException("Solana RPC service is unavailable for Bundlr uploads.");
        }

        int dataSizeBytes = System.Text.Encoding.UTF8.GetByteCount(json);
        Debug.Log($"[SolanaConfiguration] Preparing Bundlr funding. dataSizeBytes={dataSizeBytes}.");

        await EnsureBundlrFundingAsync(
            bundlrUploader,
            rpcClient,
            walletService,
            dataSizeBytes,
            fundingBufferPercent,
            minimumFundingLamports,
            commitment,
            cancellationToken).ConfigureAwait(false);
        Debug.Log("[SolanaConfiguration] Bundlr funding ensured. Uploading JSON.");
        string result = await bundlrUploader.UploadJsonAsync(fileName, json).ConfigureAwait(false);
        Debug.Log($"[SolanaConfiguration] Bundlr upload completed. uri='{result}'.");
        return result;
    }

    public string GetUserGeneratedMintMemo() => RequireConfiguredString(userGeneratedMintMemo, nameof(userGeneratedMintMemo));

    public string GetWalletVerificationMemo() => RequireConfiguredString(walletVerificationMemo, nameof(walletVerificationMemo));

    private Task<IRpcClient> RequireRpcClientAsync() => GetRpcClientAsync();

    private async Task EnsureBundlrFundingAsync(
        BundlrUploader bundlrUploader,
        IRpcClient rpcClient,
        IToolbeltWalletService walletService,
        int dataSizeBytes,
        double fundingBufferPercent,
        ulong minimumFundingLamports,
        RpcCommitment commitment,
        CancellationToken cancellationToken)
    {
        if (bundlrUploader == null)
            throw new ArgumentNullException(nameof(bundlrUploader));
        if (rpcClient == null)
            throw new ArgumentNullException(nameof(rpcClient));
        if (walletService == null)
            throw new ArgumentNullException(nameof(walletService));

        BigInteger price = await bundlrUploader.GetUploadPriceAsync(dataSizeBytes, cancellationToken).ConfigureAwait(false);
        if (price <= BigInteger.Zero)
        {
            return;
        }

        BigInteger balance = await bundlrUploader.GetBundlrBalanceAsync(cancellationToken).ConfigureAwait(false);
        if (balance >= price)
        {
            return;
        }

        BigInteger deficit = price - balance;
        BigInteger buffer = new BigInteger(minimumFundingLamports);

        if (fundingBufferPercent > 0)
        {
            BigInteger cappedDeficit = BigInteger.Min(deficit, new BigInteger(ulong.MaxValue));
            decimal scaled = (decimal)cappedDeficit * (decimal)fundingBufferPercent;
            if (scaled > 1m)
            {
                var percentageBuffer = new BigInteger(Math.Ceiling(scaled));
                if (percentageBuffer > buffer)
                {
                    buffer = percentageBuffer;
                }
            }
        }

        BigInteger desiredDeposit = deficit + buffer;
        ulong depositLamports = ClampBigIntegerToUlong(desiredDeposit);
        if (depositLamports < minimumFundingLamports)
        {
            depositLamports = minimumFundingLamports;
        }

        bool depositSubmitted = await bundlrUploader
            .TryDepositAsync(rpcClient, depositLamports, cancellationToken, throwOnFailure: false)
            .ConfigureAwait(false);

        if (!depositSubmitted)
        {
            string bundlrWallet = bundlrUploader.GetUploaderWalletAddress();
            if (string.IsNullOrWhiteSpace(bundlrWallet))
            {
                throw new InvalidOperationException("Bundlr uploader wallet address is unavailable.");
            }

            ulong walletLamports = await bundlrUploader
                .GetUploaderWalletLamportsAsync(rpcClient, Commitment.Confirmed, cancellationToken)
                .ConfigureAwait(false);

            ulong requiredLamports = depositLamports + bundlrUploader.GetEstimatedFundingFeeLamports();
            ulong shortfall = requiredLamports > walletLamports ? requiredLamports - walletLamports : 0UL;

            if (shortfall > 0)
            {
                var transferResult = await walletService.TransferSolAsync(
                    bundlrWallet,
                    shortfall,
                    commitment,
                    BuildBundlrFundingMemo(depositLamports)).ConfigureAwait(false);

                if (transferResult == null || !transferResult.Success)
                {
                    string reason = transferResult?.Error ?? "Unknown error";
                    throw new InvalidOperationException($"Failed to fund Bundlr uploader wallet: {reason}");
                }
            }

            await bundlrUploader
                .TryDepositAsync(rpcClient, depositLamports, cancellationToken, throwOnFailure: true)
                .ConfigureAwait(false);
        }

        for (int attempt = 0; attempt < 4; attempt++)
        {
            balance = await bundlrUploader.GetBundlrBalanceAsync(cancellationToken).ConfigureAwait(false);
            if (balance >= price)
            {
                return;
            }

            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Bundlr account remains underfunded after attempting to provision upload credits.");
    }

    private static async Task<bool> WaitForBundlrCreditAsync(
        BundlrUploader bundlrUploader,
        BigInteger initialBalance,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            BigInteger balance = await bundlrUploader.GetBundlrBalanceAsync(cancellationToken).ConfigureAwait(false);
            if (balance > initialBalance)
            {
                return true;
            }
        }

        return false;
    }

    private static ulong CalculateBundlrDepositLamports(ulong transferLamports, BundlrUploader bundlrUploader)
    {
        if (bundlrUploader == null)
            return 0UL;

        ulong fee = bundlrUploader.GetEstimatedFundingFeeLamports();
        if (transferLamports <= fee)
            return 0UL;

        return transferLamports - fee;
    }

    private static ulong ClampBigIntegerToUlong(BigInteger value)
    {
        if (value <= BigInteger.Zero)
            return 0UL;

        if (value > new BigInteger(ulong.MaxValue))
            return ulong.MaxValue;

        return (ulong)value;
    }

    private static string BuildBundlrFundingMemo(ulong depositLamports)
    {
        decimal sol = SolanaValueUtils.LamportsToSolDecimal(depositLamports);
        return $"Token Toss Bundlr funding ({sol:F6} SOL)";
    }

    public async Task<TransactionConfirmationResult> WaitForTransactionConfirmationAsync(
        string signature,
        TimeSpan timeout,
        double initialDelaySeconds,
        double maxDelaySeconds,
        double backoffMultiplier,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(signature))
            throw new ArgumentException("Transaction signature is required.", nameof(signature));

        var rpcClient = Web3Facade.RpcClient;
        if (rpcClient == null)
            return new TransactionConfirmationResult(false, "Solana RPC client is unavailable.");

        double delaySeconds = Math.Max(0.01, initialDelaySeconds);
        double maxDelay = Math.Max(delaySeconds, maxDelaySeconds);
        double multiplier = backoffMultiplier <= 1 ? 1.5 : backoffMultiplier;
        DateTime startTime = DateTime.UtcNow;

        var signatures = new List<string> { signature };
        string lastKnownIssue = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            RequestResult<ResponseValue<List<SignatureStatusInfo>>> statusResult =
                await rpcClient.GetSignatureStatusesAsync(signatures, true).ConfigureAwait(false);

            if (statusResult == null)
            {
                lastKnownIssue = "No response from the Solana RPC service.";
            }
            else if (!statusResult.WasSuccessful)
            {
                lastKnownIssue = string.IsNullOrWhiteSpace(statusResult.Reason)
                    ? "The Solana RPC service reported an unknown error."
                    : statusResult.Reason;
            }
            else
            {
                var value = statusResult.Result?.Value;
                if (value != null && value.Count > 0)
                {
                    var info = value[0];
                    if (info != null)
                    {
                        if (info.Error != null)
                        {
                            string error = info.Error.ToString();
                            return new TransactionConfirmationResult(false, $"The transaction was rejected: {error}");
                        }

                        if (!string.IsNullOrEmpty(info.ConfirmationStatus))
                        {
                            string confirmationStatus = info.ConfirmationStatus;
                            if (string.Equals(confirmationStatus, "finalized", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(confirmationStatus, "confirmed", StringComparison.OrdinalIgnoreCase))
                            {
                                return new TransactionConfirmationResult(true, null);
                            }
                        }

                        if (info.Confirmations.HasValue && info.Confirmations.Value > 0)
                        {
                            return new TransactionConfirmationResult(true, null);
                        }
                    }
                }
            }

            if ((DateTime.UtcNow - startTime) >= timeout)
            {
                break;
            }

            double clampedDelay = Math.Min(delaySeconds, maxDelay);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(clampedDelay), cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                throw new OperationCanceledException("Transaction confirmation cancelled.");
            }

            delaySeconds = Math.Min(delaySeconds * multiplier, maxDelay);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Transaction confirmation cancelled.");
        }

        string message = lastKnownIssue ?? "Timed out waiting for confirmation from the Solana network.";
        return new TransactionConfirmationResult(false, message);
    }

    private static string RequireConfiguredString(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"[{nameof(SolanaConfiguration)}] {fieldName} has not been configured.");

        return value;
    }
}

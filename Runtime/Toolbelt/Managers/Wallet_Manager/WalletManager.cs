using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Solana.Unity.Metaplex.NFT.Library;
using Solana.Unity.Programs;
using Solana.Unity.Rpc.Core.Http;
using Solana.Unity.Rpc.Messages;
using Solana.Unity.Rpc.Models;
using Solana.Unity.Rpc.Types;
using Solana.Unity.SDK;
using Solana.Unity.Toolbelt;
using Solana.Unity.Toolbelt.Wallet;
using Solana.Unity.Wallet;
using UnityEngine;
using RpcTokenBalance = Solana.Unity.Rpc.Models.TokenBalance;
using NftAsset = Solana.Unity.SDK.Nft.Nft;

/// <summary>
/// Coordinates wallet session, transaction and verification services while
/// exposing Unity events through <see cref="SolanaConfiguration"/>.
/// </summary>
[DisallowMultipleComponent]
public class WalletManager : MonoBehaviour, IWalletManager
{
    [Header("Config")]
    [SerializeField] private SolanaConfiguration config;

    [Header("Simulation Settings")]
    [SerializeField] private bool simulateAlwaysConnected = false;
    [SerializeField] private bool simulatePaymentSuccess = false;
    [SerializeField] private string simulatedPublicKey = "GhibVT1e5GTp2eEjtJFkgpfovYdFHpb9NTxjcj2o7a2D";

#if UNITY_EDITOR
    [Header("Editor Testing")]
    [SerializeField] private bool useEditorPrivateKey = false;
#endif

    private readonly DeferredWalletSessionService sessionProxy = new DeferredWalletSessionService();
    private IWalletSessionService sessionService;
    private IWalletTransactionService transactionService;
    private IWalletVerificationService verificationService;
    private IWeb3Facade web3Facade;
    private IPlayerPrefsStore playerPrefsStore;
    private IWalletLogger logger;
    private Action<IReadOnlyList<NftAsset>> web3NftUpdateHandler;
    private HashSet<string> nftCollectionsRequiringTextures = new HashSet<string>(StringComparer.Ordinal);

    private Coroutine toolbeltInitializationCoroutine;
    private bool walletServicesInitialized;

    private bool isAutoVerifyingWallet;
#if UNITY_WEBGL && !UNITY_EDITOR
    private TaskCompletionSource<bool> webGlVerificationCompletionSource;
#endif

    public IWalletSessionService Session => sessionProxy;
    public IWalletTransactionService Transactions => transactionService;
    public IWalletVerificationService Verification => verificationService;

    public Account CurrentAccount => sessionProxy.CurrentAccount;
    public PublicKey CurrentPublicKey => sessionProxy.CurrentPublicKey;
    public bool IsWalletConnected => sessionProxy.IsWalletConnected;
    public bool IsWalletVerified => sessionProxy.IsWalletVerified;
    public bool IsWalletStreamingDegraded => sessionProxy.IsStreamingDegraded;

    private void Awake()
    {
        if (config == null)
        {
            Debug.LogError("[WalletManager] Missing SolanaConfiguration reference. Wallet services will not be initialised.");
            enabled = false;
            return;
        }

        if (AreToolbeltServicesReady())
        {
            InitializeWalletServices();
        }
        else
        {
            Debug.LogWarning("[WalletManager] Waiting for SolanaConfiguration toolbelt services before starting wallet bootstrap.");
            toolbeltInitializationCoroutine = StartCoroutine(WaitForToolbeltServicesCoroutine());
        }
    }

    private void OnDisable()
    {
        _ = sessionService?.ShutdownAsync();
    }

    private void OnDestroy()
    {
        if (toolbeltInitializationCoroutine != null)
        {
            StopCoroutine(toolbeltInitializationCoroutine);
            toolbeltInitializationCoroutine = null;
        }

        if (sessionService != null)
        {
            sessionService.Connected -= OnSessionConnected;
            sessionService.Disconnected -= OnSessionDisconnected;
            sessionService.ConnectionFailed -= OnSessionConnectionFailed;
            sessionService.WalletVerified -= OnWalletVerified;
            sessionService.SolBalanceChanged -= OnSolBalanceChanged;
            sessionService.StreamingHealthChanged -= OnStreamingHealthChanged;
            _ = sessionService.ShutdownAsync();
        }

        if (web3Facade != null && web3NftUpdateHandler != null)
        {
            web3Facade.NftsUpdated -= web3NftUpdateHandler;
            web3NftUpdateHandler = null;
        }
    }

    private void OnSessionConnected()
    {
        config.onWalletConnected?.Invoke();

        TryTriggerAutomaticVerification();
    }

    private void OnSessionDisconnected()
    {
        config.onWalletDisconnected?.Invoke();
    }

    private void OnSessionConnectionFailed(string reason)
    {
        config.onWalletConnectionFailed?.Invoke(reason);
    }

    private void OnWalletVerified(string publicKey)
    {
        config.onWalletVerified?.Invoke(publicKey);
    }

    private void OnSolBalanceChanged(ulong lamports)
    {
        config.onSolBalanceChanged?.Invoke(lamports);
    }

    private void OnStreamingHealthChanged(bool isDegraded)
    {
        config.onWalletStreamingHealthChanged?.Invoke(isDegraded);
    }

    public Task<bool> ConnectAsync() => sessionProxy.ConnectAsync();

    public Task DisconnectAsync() => sessionProxy.DisconnectAsync();

    public Task<ulong> GetSolBalanceAsync(Commitment commitment = Commitment.Finalized) =>
        transactionService.GetSolBalanceAsync(commitment);

    public Task<RequestResult<ResponseValue<RpcTokenBalance>>> GetTokenBalanceAsync(string tokenMint, Commitment commitment = Commitment.Finalized) =>
        transactionService.GetTokenBalanceAsync(tokenMint, commitment);

    public Task<RequestResult<string>> TransferSolAsync(string destination, ulong lamports, string memo = null, Commitment commitment = Commitment.Finalized) =>
        transactionService.TransferSolAsync(destination, lamports, memo, commitment);

    public async Task<RequestResult<string>> TransferSplTokenAsync(string tokenMint, string destination, ulong amount, string memo = null, Commitment commitment = Commitment.Finalized)
    {
        var result = await transactionService.TransferSplTokenAsync(tokenMint, destination, amount, memo, commitment);

        if (result?.WasSuccessful == true)
        {
            NotifyTokenAccountsChanged(sessionProxy.CurrentPublicKey?.Key, tokenMint);
        }

        return result;
    }

    public Task<string> GetRecentBlockhashAsync(Commitment commitment = Commitment.Finalized) =>
        transactionService.GetRecentBlockhashAsync(commitment);

    public Task<bool> SignMessageAndVerifyOwnership(string message) =>
        verificationService.VerifyOwnershipAsync(message);

    public void RefreshVerificationStatus() => sessionProxy.RefreshVerificationStatus();

    public Task<SessionWallet> GetSessionWalletAsync(PublicKey targetProgram, string password)
    {
        if (targetProgram == null)
        {
            throw new ArgumentNullException(nameof(targetProgram));
        }

        if (web3Facade == null)
        {
            throw new InvalidOperationException("Wallet services have not been initialised yet.");
        }

        var baseWallet = sessionProxy.WalletBase;
        if (baseWallet == null)
        {
            throw new InvalidOperationException("Wallet not connected; unable to create a session wallet.");
        }

        return web3Facade.GetSessionWalletAsync(targetProgram, password, baseWallet);
    }

    private void NotifyTokenAccountsChanged(string ownerPublicKey, string collectionMint = null)
    {
        if (string.IsNullOrWhiteSpace(ownerPublicKey))
        {
            return;
        }

        var serviceProvider = config?.ServiceProvider;
        var inventoryService = serviceProvider?.NftInventoryService;
        inventoryService?.InvalidateTokenAccountsCache(ownerPublicKey, collectionMint);

        serviceProvider?.NotifyTokenAccountsChanged(ownerPublicKey, collectionMint);
    }

    private IEnumerator WaitForToolbeltServicesCoroutine()
    {
        while (!AreToolbeltServicesReady())
        {
            yield return null;
        }

        Debug.Log("[WalletManager] Detected SolanaConfiguration toolbelt services. Initializing wallet services.");
        toolbeltInitializationCoroutine = null;
        InitializeWalletServices();
    }

    private bool AreToolbeltServicesReady()
    {
        if (config == null)
        {
            return false;
        }

        return true;
    }

    private void InitializeWalletServices()
    {
        if (walletServicesInitialized)
        {
            return;
        }

        if (!AreToolbeltServicesReady())
        {
            Debug.LogError("[WalletManager] Attempted to initialize wallet services before SolanaConfiguration was ready.");
            return;
        }

        walletServicesInitialized = true;

        logger = new UnityWalletLogger();
        playerPrefsStore = new UnityPlayerPrefsStore();
        web3Facade = new Web3Facade();
        Web3.LoadNftsTextureByDefault = false;
        nftCollectionsRequiringTextures = BuildCollectionsRequiringTextures();

        web3NftUpdateHandler = OnWeb3NftsUpdated;
        web3Facade.NftsUpdated += web3NftUpdateHandler;

        var options = new WalletSessionOptions
        {
            SimulateAlwaysConnected = simulateAlwaysConnected,
            SimulatePaymentSuccess = simulatePaymentSuccess,
            SimulatedPublicKey = string.IsNullOrWhiteSpace(simulatedPublicKey)
                ? "GhibVT1e5GTp2eEjtJFkgpfovYdFHpb9NTxjcj2o7a2D"
                : simulatedPublicKey,
#if UNITY_EDITOR
            UseEditorPrivateKey = useEditorPrivateKey,
            EditorInGameWalletCluster = Solana.Unity.SDK.RpcCluster.MainNet,
            EditorInGameWalletCustomRpc = GetPrimaryHttpRpcUrl(),
            EditorInGameWalletStreamingRpc = GetPrimaryStreamingRpcUrl()
#endif
        };

        try
        {
            sessionService = new WalletSessionService(web3Facade, playerPrefsStore, logger, options);
            transactionService = new WalletTransactionService(
                sessionService,
                web3Facade,
                config.RecentBlockhashCacheMaxSeconds,
                config.GetOrCreateRpcEndpointManager());
            verificationService = new WalletVerificationService(sessionService, web3Facade, logger);
            sessionProxy.Attach(sessionService);
        }
        catch (Exception ex)
        {
            walletServicesInitialized = false;
            if (web3NftUpdateHandler != null)
            {
                web3Facade.NftsUpdated -= web3NftUpdateHandler;
                web3NftUpdateHandler = null;
            }
            Debug.LogError($"[WalletManager] Failed to create wallet services: {ex.Message}");
            return;
        }

        sessionService.Connected += OnSessionConnected;
        sessionService.Disconnected += OnSessionDisconnected;
        sessionService.ConnectionFailed += OnSessionConnectionFailed;
        sessionService.WalletVerified += OnWalletVerified;
        sessionService.SolBalanceChanged += OnSolBalanceChanged;
        sessionService.StreamingHealthChanged += OnStreamingHealthChanged;

        _ = sessionService.InitializeAsync();
    }

    private void OnWeb3NftsUpdated(IReadOnlyList<NftAsset> nfts)
    {
        _ = EnsureTexturesForEligibleCollectionsAsync(nfts);
        NotifyTokenAccountsChanged(sessionProxy.CurrentPublicKey?.Key);
    }

    private HashSet<string> BuildCollectionsRequiringTextures()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        if (config == null)
        {
            return result;
        }

        string levelCollectionMint = string.IsNullOrWhiteSpace(config.levelsCollectionMint)
            ? null
            : config.levelsCollectionMint.Trim();

        void TryAdd(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            var normalized = candidate.Trim();
            if (!string.IsNullOrEmpty(levelCollectionMint) &&
                string.Equals(normalized, levelCollectionMint, StringComparison.Ordinal))
            {
                return;
            }

            result.Add(normalized);
        }

        TryAdd(config.alphaCollection?.collectionMint);
        TryAdd(config.betaCollection?.collectionMint);
        TryAdd(config.levelCreatorCollection?.collectionMint);

        return result;
    }

    private async Task EnsureTexturesForEligibleCollectionsAsync(IReadOnlyList<NftAsset> nfts)
    {
        if (nfts == null || nftCollectionsRequiringTextures == null || nftCollectionsRequiringTextures.Count == 0)
        {
            return;
        }

        var processedMints = new HashSet<string>(StringComparer.Ordinal);

        foreach (var nft in nfts)
        {
            if (nft?.metaplexData?.data == null)
            {
                continue;
            }

            string mint = nft.metaplexData.data.mint;
            if (string.IsNullOrWhiteSpace(mint) || !processedMints.Add(mint.Trim()))
            {
                continue;
            }

            var metadata = nft.metaplexData.data.metadata;
            if (metadata == null)
            {
                continue;
            }

            var collectionMint = MetaplexMetadataUtility.GetCollectionKey(metadata);
            if (string.IsNullOrWhiteSpace(collectionMint))
            {
                continue;
            }

            collectionMint = collectionMint.Trim();
            if (!nftCollectionsRequiringTextures.Contains(collectionMint))
            {
                continue;
            }

            if (nft.metaplexData.nftImage?.file != null)
            {
                continue;
            }

            try
            {
                await nft.LoadTexture().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WalletManager] Failed to load NFT texture for {mint}: {ex.Message}");
            }
        }
    }

#if UNITY_EDITOR
    private string GetPrimaryHttpRpcUrl()
    {
        if (config == null)
        {
            return string.Empty;
        }

        var ordered = config.GetOrderedRpcUrls();
        if (ordered == null || ordered.Count == 0)
        {
            return string.Empty;
        }

        return ordered.FirstOrDefault(url => !string.IsNullOrWhiteSpace(url)) ?? string.Empty;
    }

    private string GetPrimaryStreamingRpcUrl()
    {
        if (config?.streamingRpcUrls == null || config.streamingRpcUrls.Length == 0)
        {
            return string.Empty;
        }

        return config.streamingRpcUrls.FirstOrDefault(url => !string.IsNullOrWhiteSpace(url)) ?? string.Empty;
    }
#endif

    public async Task<string> MintLevelEditorUnlockNftAsync(string memo = null)
    {
        if (!IsWalletConnected)
        {
            throw new Exception("Wallet not connected");
        }

        var levelCollection = config.levelCreatorCollection;

        var mintRequest = new MintRequest
        {
            CollectionMint = levelCollection.collectionMint,
            MetadataUri = levelCollection.metadataUri,
            Name = levelCollection.name,
            Symbol = levelCollection.symbol,
            SellerFeeBasisPoints = levelCollection.sellerFeeBasisPoints,
            IsMutable = true,
            Quantity = 1,
            Creators = new List<Creator>()
        };

        var result = await config.TokenMintService.MintAndVerifyAsync(mintRequest, memo);
        if (!result.Success)
        {
            Debug.LogError("[WalletManager] NFT Mint Failed: " + result.ErrorMessage);
            throw new Exception("Minting Failed: " + result.ErrorMessage);
        }

        NotifyTokenAccountsChanged(sessionProxy.CurrentPublicKey?.Key);

        return result.Signatures.Count > 0 ? result.Signatures[0] : string.Empty;
    }

    private void TryTriggerAutomaticVerification()
    {
        sessionProxy.RefreshVerificationStatus();
        if (sessionProxy.IsWalletVerified)
        {
            return;
        }

        if (simulateAlwaysConnected || verificationService == null)
        {
            return;
        }

        if (isAutoVerifyingWallet)
        {
            return;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        webGlVerificationCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
#endif
        isAutoVerifyingWallet = true;
        _ = AutoVerifyWalletOwnershipAsync();
    }

    private async Task AutoVerifyWalletOwnershipAsync()
    {
        try
        {
            sessionProxy.RefreshVerificationStatus();
            if (sessionProxy.IsWalletVerified)
            {
                CompleteAutoVerificationTask(true);
                return;
            }

            string memo;
            try
            {
                memo = config.GetWalletVerificationMemo();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WalletManager] Wallet verification memo unavailable: {ex.Message}");
                CompleteAutoVerificationTask(false);
                return;
            }

            bool verified = await verificationService.VerifyOwnershipAsync(memo);
            CompleteAutoVerificationTask(verified);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WalletManager] Automatic wallet verification failed: {ex.Message}");
            CompleteAutoVerificationTask(false);
        }
        finally
        {
            isAutoVerifyingWallet = false;
        }
    }

    private void CompleteAutoVerificationTask(bool result)
    {
        sessionProxy.RefreshVerificationStatus();
#if UNITY_WEBGL && !UNITY_EDITOR
        var completionSource = webGlVerificationCompletionSource;
        if (completionSource != null)
        {
            bool finalResult = result && sessionProxy.IsWalletVerified;
            completionSource.TrySetResult(finalResult);
            webGlVerificationCompletionSource = null;
        }
#endif
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    public Task<bool> WaitForWebGlVerificationAsync(TimeSpan timeout)
    {
        sessionProxy.RefreshVerificationStatus();
        if (sessionProxy.IsWalletVerified)
        {
            return Task.FromResult(true);
        }

        var completionSource = webGlVerificationCompletionSource;
        if (completionSource == null)
        {
            return Task.FromResult(false);
        }

        if (timeout <= TimeSpan.Zero)
        {
            return completionSource.Task;
        }

        return WaitForWebGlVerificationWithTimeoutAsync(completionSource, timeout);
    }

    private async Task<bool> WaitForWebGlVerificationWithTimeoutAsync(TaskCompletionSource<bool> completionSource, TimeSpan timeout)
    {
        var verificationTask = completionSource.Task;
        var completedTask = await Task.WhenAny(verificationTask, Task.Delay(timeout));
        if (completedTask == verificationTask)
        {
            return await verificationTask;
        }

        sessionProxy.RefreshVerificationStatus();
        return sessionProxy.IsWalletVerified;
    }
#endif

    private sealed class DeferredWalletSessionService : IWalletSessionService
    {
        private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(5);

        private readonly TaskCompletionSource<IWalletSessionService> readinessSource =
            new TaskCompletionSource<IWalletSessionService>(TaskCreationOptions.RunContinuationsAsynchronously);

        private IWalletSessionService inner;

        private Action connectedHandlers;
        private Action disconnectedHandlers;
        private Action<string> connectionFailedHandlers;
        private Action<string> walletVerifiedHandlers;
        private Action<ulong> solBalanceChangedHandlers;
        private Action<bool> streamingHealthChangedHandlers;
        private bool isStreamingDegraded;

        public event Action Connected
        {
            add
            {
                connectedHandlers += value;
                if (inner != null)
                {
                    inner.Connected += value;
                }
            }
            remove
            {
                connectedHandlers -= value;
                if (inner != null)
                {
                    inner.Connected -= value;
                }
            }
        }

        public event Action Disconnected
        {
            add
            {
                disconnectedHandlers += value;
                if (inner != null)
                {
                    inner.Disconnected += value;
                }
            }
            remove
            {
                disconnectedHandlers -= value;
                if (inner != null)
                {
                    inner.Disconnected -= value;
                }
            }
        }

        public event Action<string> ConnectionFailed
        {
            add
            {
                connectionFailedHandlers += value;
                if (inner != null)
                {
                    inner.ConnectionFailed += value;
                }
            }
            remove
            {
                connectionFailedHandlers -= value;
                if (inner != null)
                {
                    inner.ConnectionFailed -= value;
                }
            }
        }

        public event Action<string> WalletVerified
        {
            add
            {
                walletVerifiedHandlers += value;
                if (inner != null)
                {
                    inner.WalletVerified += value;
                }
            }
            remove
            {
                walletVerifiedHandlers -= value;
                if (inner != null)
                {
                    inner.WalletVerified -= value;
                }
            }
        }

        public event Action<ulong> SolBalanceChanged
        {
            add
            {
                solBalanceChangedHandlers += value;
                if (inner != null)
                {
                    inner.SolBalanceChanged += value;
                }
            }
            remove
            {
                solBalanceChangedHandlers -= value;
                if (inner != null)
                {
                    inner.SolBalanceChanged -= value;
                }
            }
        }

        public event Action<bool> StreamingHealthChanged
        {
            add
            {
                streamingHealthChangedHandlers += value;
                if (inner != null)
                {
                    inner.StreamingHealthChanged += value;
                }
            }
            remove
            {
                streamingHealthChangedHandlers -= value;
                if (inner != null)
                {
                    inner.StreamingHealthChanged -= value;
                }
            }
        }

        public PublicKey CurrentPublicKey => inner?.CurrentPublicKey;

        public Account CurrentAccount => inner?.CurrentAccount;

        public WalletBase WalletBase => inner?.WalletBase;

        public bool IsWalletConnected => inner?.IsWalletConnected ?? false;

        public bool IsWalletVerified => inner?.IsWalletVerified ?? false;

        public bool IsStreamingDegraded => inner?.IsStreamingDegraded ?? isStreamingDegraded;

        public bool SimulatePaymentSuccess => inner?.SimulatePaymentSuccess ?? false;

        public async Task InitializeAsync()
        {
            var service = await WaitForServiceAsync();
            if (service == null)
            {
                return;
            }

            await service.InitializeAsync();
        }

        public async Task<bool> ConnectAsync()
        {
            var service = await WaitForServiceAsync();
            if (service == null)
            {
                return false;
            }

            return await service.ConnectAsync();
        }

        public async Task DisconnectAsync()
        {
            var service = await WaitForServiceAsync();
            if (service == null)
            {
                return;
            }

            await service.DisconnectAsync();
        }

        public Task ShutdownAsync()
        {
            if (inner == null)
            {
                return Task.CompletedTask;
            }

            return inner.ShutdownAsync();
        }

        public void RefreshVerificationStatus()
        {
            if (inner == null)
            {
                return;
            }

            inner.RefreshVerificationStatus();
        }

        public void MarkWalletVerified(string publicKey)
        {
            if (inner == null)
            {
                return;
            }

            inner.MarkWalletVerified(publicKey);
        }

        public void ClearVerificationStatus()
        {
            if (inner == null)
            {
                return;
            }

            inner.ClearVerificationStatus();
        }

        public void Attach(IWalletSessionService service)
        {
            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }

            if (inner != null)
            {
                Debug.LogWarning("[WalletManager] Attempted to attach wallet session service more than once.");
                return;
            }

            inner = service;

            if (connectedHandlers != null)
            {
                inner.Connected += connectedHandlers;
            }

            if (disconnectedHandlers != null)
            {
                inner.Disconnected += disconnectedHandlers;
            }

            if (connectionFailedHandlers != null)
            {
                inner.ConnectionFailed += connectionFailedHandlers;
            }

            if (walletVerifiedHandlers != null)
            {
                inner.WalletVerified += walletVerifiedHandlers;
            }

            if (solBalanceChangedHandlers != null)
            {
                inner.SolBalanceChanged += solBalanceChangedHandlers;
            }

            if (streamingHealthChangedHandlers != null)
            {
                inner.StreamingHealthChanged += streamingHealthChangedHandlers;
            }

            isStreamingDegraded = inner.IsStreamingDegraded;

            readinessSource.TrySetResult(service);
        }

        private async Task<IWalletSessionService> WaitForServiceAsync()
        {
            if (inner != null)
            {
                return inner;
            }

            var completedTask = await Task.WhenAny(readinessSource.Task, Task.Delay(ReadinessTimeout));
            if (completedTask == readinessSource.Task)
            {
                return readinessSource.Task.Result;
            }

            Debug.LogWarning("[WalletManager] Wallet session requested before services were ready.");
            return null;
        }
    }
}

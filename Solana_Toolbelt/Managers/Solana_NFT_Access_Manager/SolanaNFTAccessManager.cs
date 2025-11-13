using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using Solana.Unity.Wallet;
using Solana.Unity.Programs;
using Solana.Unity.Toolbelt.Wallet;
using Solana.Unity.Toolbelt;

/// <summary>
/// Handles NFT-based gated access to any game feature. Designed to be extended or used as a component
/// for domain-specific systems such as level editor unlocks. Caches unlock state locally and revalidates on interval.
/// </summary>
public class SolanaNftAccessManager : MonoBehaviour, INftAccessService
{
    [Header("Dependencies")]
    [SerializeField] private SolanaConfiguration solanaConfig;
    [SerializeField] private WalletManager walletManager;

    [Header("NFT Collection Mints")]
    [SerializeField] private LevelEditorAccessData accessData;

    [Header("Polling & Expiration")]
    [SerializeField] private float ownershipCheckIntervalHours = 6f;
    [SerializeField] private bool autoPollEnabled = true;

    [Header("Local Caching")]
    [SerializeField] private string playerPrefsKey = "SolanaNFTFeatureUnlocked";

    [Header("Events")]
    [SerializeField] private UnityEvent accessGranted = new UnityEvent();
    [SerializeField] private UnityEvent accessRevoked = new UnityEvent();

    private float _nextCheckTime;
    private bool _isUnlocked;
    private readonly List<string> _subscribedAccounts = new();

    private IToolbeltUiBridge uiBridge;
    private ILevelPricingData pricingData;
    private ISolanaStorageService storageService;

    private IEnumerable<string> ValidMintAddresses => accessData?.collectionMints ?? Array.Empty<string>();
    private IWalletSessionService WalletSession => walletManager != null ? walletManager.Session : null;
    private IWalletVerificationService WalletVerification => walletManager != null ? walletManager.Verification : null;

    private void OnEnable()
    {
        solanaConfig.onWalletConnected.AddListener(WalletConnectedHandler);
        solanaConfig.onWalletDisconnected.AddListener(WalletDisconnectedHandler);
        _ = CheckInitialState();
    }

    private void OnDisable()
    {
        solanaConfig.onWalletConnected.RemoveListener(WalletConnectedHandler);
        solanaConfig.onWalletDisconnected.RemoveListener(WalletDisconnectedHandler);
        _ = UnsubscribeFromCollectionAccountsAsync();
    }

    private void WalletConnectedHandler()
    {
        _ = OnWalletConnected();
    }

    private void WalletDisconnectedHandler()
    {
        _ = OnWalletDisconnected();
    }

    private async Task CheckInitialState()
    {
        if (await GetStoredUnlockStateAsync())
        {
            await UnlockLocallyAsync(persist: false);
        }
        else if (WalletSession != null && WalletSession.IsWalletConnected)
        {
            await RefreshOwnershipAsync();
        }

        if (autoPollEnabled)
        {
            _nextCheckTime = Time.time + ownershipCheckIntervalHours * 3600f;
        }
    }

    private void Update()
    {
        if (autoPollEnabled && Time.time > _nextCheckTime)
        {
            _ = RefreshOwnershipAsync();
            _nextCheckTime = Time.time + ownershipCheckIntervalHours * 3600f;
        }
    }

    private async Task OnWalletConnected()
    {
        await DeleteUnlockFlagAsync();
        _isUnlocked = false;

        await SubscribeToCollectionAccountsAsync();

        await RefreshOwnershipAsync();
    }

    private async Task OnWalletDisconnected()
    {
        await UnsubscribeFromCollectionAccountsAsync();
    }

    public bool IsLocallyUnlocked() => _isUnlocked;

    public UnityEvent AccessGranted => accessGranted;

    public UnityEvent AccessRevoked => accessRevoked;

    public async Task RefreshOwnershipAsync()
    {
        if (WalletSession == null || !WalletSession.IsWalletConnected) return;

        var pubKey = WalletSession.CurrentPublicKey;
        if (accessData == null || accessData.collectionMints == null) return;

        var inventoryService = solanaConfig?.ServiceProvider?.NftInventoryService;
        if (inventoryService == null)
        {
            Debug.LogWarning("[SolanaNftAccessManager] Inventory service unavailable; unable to refresh ownership.");
            return;
        }

        string ownerKey = pubKey?.Key;
        if (string.IsNullOrWhiteSpace(ownerKey))
            return;

        foreach (var mint in accessData.collectionMints)
        {
            var accounts = await inventoryService.GetTokenAccountsByOwnerAsync(ownerKey, mint, RpcCommitment.Confirmed);
            if (accounts == null)
                continue;

            foreach (var account in accounts)
            {
                if (account != null && account.HasBalance)
                {
                    await UnlockLocallyAsync();
                    return;
                }
            }
        }

        await LockLocallyAsync();
    }

    private ISolanaStorageService GetStorageService()
    {
        if (storageService != null)
        {
            return storageService;
        }

        storageService = solanaConfig?.ServiceProvider?.StorageService ??
            ToolbeltRuntime.Services?.StorageService ??
            ToolbeltRuntime.Instance?.StorageService;

        return storageService;
    }

    private async Task<bool> GetStoredUnlockStateAsync()
    {
        var service = GetStorageService();
        if (service == null)
        {
            return false;
        }

        try
        {
            return await service.GetFlagAsync(playerPrefsKey);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SolanaNftAccessManager] Failed to read unlock state: {ex.Message}");
            return false;
        }
    }

    private async Task PersistUnlockFlagAsync(bool value)
    {
        var service = GetStorageService();
        if (service == null)
        {
            return;
        }

        try
        {
            await service.SetFlagAsync(playerPrefsKey, value);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SolanaNftAccessManager] Failed to persist unlock state: {ex.Message}");
        }
    }

    private async Task DeleteUnlockFlagAsync()
    {
        var service = GetStorageService();
        if (service == null)
        {
            return;
        }

        try
        {
            await service.DeleteFlagAsync(playerPrefsKey);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SolanaNftAccessManager] Failed to clear unlock state: {ex.Message}");
        }
    }

    private async Task UnlockLocallyAsync(bool invokeEvents = true, bool persist = true)
    {
        if (persist)
        {
            await PersistUnlockFlagAsync(true);
        }

        if (_isUnlocked)
        {
            return;
        }

        _isUnlocked = true;
        if (invokeEvents)
        {
            accessGranted?.Invoke();
        }
    }

    private async Task LockLocallyAsync(bool persist = true)
    {
        if (persist)
        {
            await PersistUnlockFlagAsync(false);
        }

        if (!_isUnlocked)
        {
            return;
        }

        _isUnlocked = false;
        accessRevoked?.Invoke();
    }

    public async Task AttemptUnlockFlow(Action onFailure = null, Action onCancel = null)
    {
        if (!await EnsureWalletConnectedAndVerifiedAsync())
        {
            onCancel?.Invoke();
            return;
        }

        await RefreshOwnershipAsync();

        if (!_isUnlocked)
        {
            var data = pricingData ?? solanaConfig?.LevelPricingData;
            var prices = data?.LevelEditorPricingOptions;
            if (prices != null && prices.Count > 0)
            {
                GetUiBridge()?.ShowPricePopup(prices, selected =>
                {
                    GetUiBridge()?.ShowMintCostPopup(
                        selected,
                        solanaConfig,
                        async () => await MintUnlockNFT(),
                        onCancel != null ? (Func<Task>)(() => { onCancel(); return Task.CompletedTask; }) : null);
                });
            }
            else
            {
                GetUiBridge()?.ShowConfirmCancelPopup(
                    "You do not own a required NFT to unlock this feature. Would you like to mint one now?",
                    onConfirm: async () => await MintUnlockNFT(),
                    onCancel: onCancel != null ? (Func<Task>)(() => { onCancel(); return Task.CompletedTask; }) : null
                );
            }
        }
    }

    private async Task<bool> EnsureWalletConnectedAndVerifiedAsync()
    {
        if (walletManager == null)
        {
            Debug.LogError("[SolanaNftAccessManager] WalletManager reference missing.");
            return false;
        }

        var session = WalletSession;
        var verification = WalletVerification;
        if (session == null || verification == null)
        {
            Debug.LogError("[SolanaNftAccessManager] Wallet services unavailable. Check Solana configuration.");
            return false;
        }

        if (!session.IsWalletConnected)
        {
            var popupManager = GetUiBridge();
            if (popupManager == null)
            {
                Debug.LogError("[SolanaNftAccessManager] UI bridge reference missing; cannot prompt for wallet connection.");
                return false;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            popupManager.ShowWalletConnectPopup(
                solanaConfig,
                () => tcs.TrySetResult(true),
                () => tcs.TrySetResult(false));

            bool connected = await tcs.Task;
            if (!connected)
            {
                Debug.LogWarning("[SolanaNftAccessManager] Wallet connection was cancelled.");
                return false;
            }
        }

        session.RefreshVerificationStatus();
        if (session.IsWalletVerified)
        {
            return true;
        }

        if (Application.isEditor)
        {
            Debug.LogWarning("[SolanaNftAccessManager] Unity Editor detected; skipping wallet verification.");
            return session.IsWalletConnected;
        }

        if (solanaConfig == null)
        {
            Debug.LogError("[SolanaNftAccessManager] SolanaConfiguration reference missing; cannot determine wallet verification memo.");
            return false;
        }

        string memo;
        try
        {
            memo = solanaConfig.GetWalletVerificationMemo();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SolanaNftAccessManager] Wallet verification memo is not configured: {ex.Message}");
            return false;
        }

        bool verified = await verification.VerifyOwnershipAsync(memo);
        if (!verified)
        {
            Debug.LogWarning("[SolanaNftAccessManager] Wallet ownership verification failed or was cancelled.");
        }

        return verified;
    }

    private async Task MintUnlockNFT()
    {
        try
        {
            if (!await EnsureWalletConnectedAndVerifiedAsync())
            {
                Debug.LogWarning("[SolanaNftAccessManager] Aborting mint; wallet not connected or verified.");
                return;
            }

            if (solanaConfig == null)
            {
                Debug.LogError("[SolanaNftAccessManager] SolanaConfiguration reference missing; cannot determine unlock memo.");
                return;
            }

            string memo;
            try
            {
                memo = solanaConfig.GetLevelEditorUnlockMemo();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SolanaNftAccessManager] Level editor unlock memo is not configured: {ex.Message}");
                return;
            }

            if (walletManager == null)
            {
                Debug.LogError("[SolanaNftAccessManager] WalletManager reference missing; cannot mint unlock NFT.");
                return;
            }

            var mintSignature = await walletManager.MintLevelEditorUnlockNftAsync(memo);
            Debug.Log($"[SolanaNftAccessManager] NFT minted: {mintSignature}");
            await UnlockLocallyAsync();
        }
        catch (Exception e)
        {
            Debug.LogError($"[SolanaNftAccessManager] Minting failed: {e.Message}");
            GetUiBridge()?.ShowConfirmCancelPopup(
                $"Mint Failed: {e.Message}", null, null
            );
        }
    }

    private async Task SubscribeToCollectionAccountsAsync()
    {
        var session = WalletSession;
        if (session == null || !session.IsWalletConnected) return;

        foreach (var mint in ValidMintAddresses)
        {
            var ata = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(session.CurrentPublicKey, new PublicKey(mint));
            if (_subscribedAccounts.Contains(ata.Key))
            {
                continue;
            }

            _subscribedAccounts.Add(ata.Key);
        }

        await RefreshOwnershipAsync();
    }

    private Task UnsubscribeFromCollectionAccountsAsync()
    {
        _subscribedAccounts.Clear();
        return Task.CompletedTask;
    }

    internal void SetUiBridge(IToolbeltUiBridge bridge)
    {
        uiBridge = bridge;
    }

    internal void SetLevelPricingData(ILevelPricingData data)
    {
        pricingData = data;
    }

    internal void SetStorageService(ISolanaStorageService service)
    {
        storageService = service;
    }

    private IToolbeltUiBridge GetUiBridge()
    {
        return uiBridge ?? solanaConfig?.UiBridge;
    }
}

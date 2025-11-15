using System;
using System.Threading.Tasks;
using Solana.Unity.Rpc.Models;
using Solana.Unity.Rpc.Types;
using Solana.Unity.SDK;
using Solana.Unity.Wallet;

namespace Solana.Unity.Toolbelt.Wallet
{
    /// <summary>
    /// Handles wallet login/logout flows, persistence of the last selected
    /// wallet provider, optional simulation modes and balance events.
    /// </summary>
    public class WalletSessionService : IWalletSessionService
    {
        private const string LastWalletTypeKey = "LastWalletType";
        private const string VerifiedWalletKey = "VerifiedWalletKey";
        private const double LamportsPerSol = 1_000_000_000d;

        private readonly IWeb3Facade web3;
        private readonly IPlayerPrefsStore playerPrefs;
        private readonly IWalletLogger logger;
        private readonly WalletSessionOptions options;

        private bool isWalletVerified;
        private ulong? lastKnownBalance;
        private bool isStreamingDegraded;
        private bool web3EventsRegistered;
        private bool hasActiveSession;
        private string lastConnectedPublicKey;

        public WalletSessionService(
            IWeb3Facade web3,
            IPlayerPrefsStore playerPrefs,
            IWalletLogger logger,
            WalletSessionOptions options)
        {
            this.web3 = web3 ?? throw new ArgumentNullException(nameof(web3));
            this.playerPrefs = playerPrefs ?? throw new ArgumentNullException(nameof(playerPrefs));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.options = options ?? new WalletSessionOptions();
        }

        public event Action Connected;
        public event Action Disconnected;
        public event Action<string> ConnectionFailed;
        public event Action<string> WalletVerified;
        public event Action<ulong> SolBalanceChanged;
        public event Action<bool> StreamingHealthChanged;

        public PublicKey CurrentPublicKey => options.SimulateAlwaysConnected
            ? new PublicKey(options.SimulatedPublicKey)
            : web3.Account?.PublicKey;

        public Account CurrentAccount => web3.Account;

        public WalletBase WalletBase => web3.WalletBase;

        public bool IsWalletConnected => options.SimulateAlwaysConnected || web3.Account != null;

        public bool IsWalletVerified => options.SimulateAlwaysConnected || isWalletVerified;

        public bool IsStreamingDegraded => isStreamingDegraded;

        public bool SimulatePaymentSuccess => options.SimulatePaymentSuccess;

        public async Task InitializeAsync()
        {
            isWalletVerified = false;
            EnsureWeb3EventHandlers();

            if (options.SimulateAlwaysConnected)
            {
                hasActiveSession = true;
                lastConnectedPublicKey = options.SimulatedPublicKey;
                UpdateStreamingHealth(false);
                Connected?.Invoke();
                return;
            }

            if (!playerPrefs.HasKey(LastWalletTypeKey))
            {
                UpdateStreamingHealth(true);
                return;
            }

            string lastWalletType = playerPrefs.GetString(LastWalletTypeKey);
            await RestoreLastWalletAsync(lastWalletType).ConfigureAwait(false);
        }

        public async Task<bool> ConnectAsync()
        {
            EnsureWeb3EventHandlers();

            if (options.SimulateAlwaysConnected)
            {
                hasActiveSession = true;
                lastConnectedPublicKey = options.SimulatedPublicKey;
                UpdateStreamingHealth(false);
                Connected?.Invoke();
                return true;
            }

            try
            {
#if UNITY_EDITOR
                if (options.UseEditorPrivateKey && EditorWalletDevTools.HasPrivateKey)
                {
                    string privateKey = EditorWalletDevTools.PrivateKey;
                    if (!EditorWalletDevTools.TryNormalizePrivateKey(privateKey, out string normalizedKey, out string normalizeError))
                    {
                        throw new Exception(string.IsNullOrEmpty(normalizeError)
                            ? "The stored editor private key could not be parsed."
                            : normalizeError);
                    }

                    if (!string.Equals(privateKey, normalizedKey, StringComparison.Ordinal))
                    {
                        EditorWalletDevTools.PrivateKey = normalizedKey;
                    }

                    var inGameWallet = CreateEditorInGameWallet();
                    await inGameWallet.CreateAccount(normalizedKey);
                    web3.WalletBase = inGameWallet;
                    playerPrefs.SetString(LastWalletTypeKey, "editorwallet");
                    playerPrefs.Save();
                    MarkWalletVerified(inGameWallet.Account?.PublicKey?.Key);
                }
                else
#endif
                {
                    if (!IsWalletAdapterSupported(out string adapterError))
                    {
                        throw new PlatformNotSupportedException(adapterError);
                    }

                    await web3.LoginWalletAdapterAsync();
                    if (web3.Account == null)
                    {
                        throw new Exception("Wallet connection failed or user rejected the request.");
                    }

                    playerPrefs.SetString(LastWalletTypeKey, "phantomwallet");
                    playerPrefs.Save();
                    RefreshVerificationStatus();
                }

                return true;
            }
            catch (Exception ex)
            {
                logger.LogError($"Wallet Connection Failed: {ex.Message}");
                ConnectionFailed?.Invoke(ex.Message);
                return false;
            }
        }

        public async Task DisconnectAsync()
        {
            if (options.SimulateAlwaysConnected)
            {
                hasActiveSession = false;
                lastConnectedPublicKey = null;
                isWalletVerified = false;
                lastKnownBalance = null;
                UpdateStreamingHealth(true);
                Disconnected?.Invoke();
                playerPrefs.DeleteKey(LastWalletTypeKey);
                playerPrefs.DeleteKey(VerifiedWalletKey);
                playerPrefs.Save();
                await Task.CompletedTask;
                return;
            }

            web3.Logout();

            playerPrefs.DeleteKey(LastWalletTypeKey);
            playerPrefs.DeleteKey(VerifiedWalletKey);
            playerPrefs.Save();

            await Task.CompletedTask;
        }

        public Task ShutdownAsync()
        {
            UnregisterWeb3EventHandlers();
            return Task.CompletedTask;
        }

        public void RefreshVerificationStatus()
        {
            if (!IsWalletConnected)
            {
                isWalletVerified = false;
                return;
            }

            string verifiedKey = playerPrefs.GetString(VerifiedWalletKey, string.Empty);
            isWalletVerified = CurrentPublicKey != null && CurrentPublicKey.Key == verifiedKey;
        }

        public void MarkWalletVerified(string publicKey)
        {
            if (string.IsNullOrEmpty(publicKey))
            {
                return;
            }

            isWalletVerified = true;
            playerPrefs.SetString(VerifiedWalletKey, publicKey);
            playerPrefs.Save();
            WalletVerified?.Invoke(publicKey);
        }

        public void ClearVerificationStatus()
        {
            isWalletVerified = false;
            playerPrefs.DeleteKey(VerifiedWalletKey);
            playerPrefs.Save();
        }

        private async Task RestoreLastWalletAsync(string walletType)
        {
            switch (walletType)
            {
                case "phantomwallet":
                    if (IsWalletAdapterSupported(out string adapterError))
                    {
                        await web3.LoginWalletAdapterAsync();
                    }
                    else
                    {
                        logger.LogError(adapterError);
                        playerPrefs.DeleteKey(LastWalletTypeKey);
                        playerPrefs.Save();
                    }
                    break;
                case "web3authwallet":
                    await web3.LoginWeb3AuthAsync(Provider.GOOGLE);
                    break;
#if UNITY_EDITOR
                case "editorwallet":
                    if (options.UseEditorPrivateKey && EditorWalletDevTools.HasPrivateKey)
                    {
                        string privateKey = EditorWalletDevTools.PrivateKey;
                        if (!EditorWalletDevTools.TryNormalizePrivateKey(privateKey, out string normalizedKey, out string normalizeError))
                        {
                            logger.LogError(string.IsNullOrEmpty(normalizeError)
                                ? "Failed to restore editor wallet: stored private key could not be parsed."
                                : normalizeError);
                            break;
                        }

                        if (!string.Equals(privateKey, normalizedKey, StringComparison.Ordinal))
                        {
                            EditorWalletDevTools.PrivateKey = normalizedKey;
                        }

                        var inGameWallet = CreateEditorInGameWallet();
                        await inGameWallet.CreateAccount(normalizedKey);
                        web3.WalletBase = inGameWallet;
                        MarkWalletVerified(inGameWallet.Account?.PublicKey?.Key);
                    }
                    break;
#endif
            }
        }

        private bool IsWalletAdapterSupported(out string errorMessage)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            errorMessage = null;
            return true;
#else
            errorMessage = "The Solana Mobile Wallet Adapter is only available when running on an Android device. Configure an editor wallet or enable simulation to test in the editor.";
            return false;
#endif
        }

#if UNITY_EDITOR
        private InGameWallet CreateEditorInGameWallet()
        {
            var customRpc = string.IsNullOrWhiteSpace(options.EditorInGameWalletCustomRpc)
                ? null
                : options.EditorInGameWalletCustomRpc.Trim();
            var streamingRpc = string.IsNullOrWhiteSpace(options.EditorInGameWalletStreamingRpc)
                ? null
                : options.EditorInGameWalletStreamingRpc.Trim();

            return new InGameWallet(
                options.EditorInGameWalletCluster,
                customRpc,
                streamingRpc,
                options.EditorInGameWalletAutoConnectOnStartup);
        }
#endif

        private void EnsureWeb3EventHandlers()
        {
            if (web3EventsRegistered || options.SimulateAlwaysConnected)
            {
                return;
            }

            web3.WalletStateChanged += HandleWeb3WalletStateChanged;
            web3.LoggedIn += HandleWeb3LoggedIn;
            web3.LoggedOut += HandleWeb3LoggedOut;
            web3.BalanceChanged += HandleWeb3BalanceChanged;
            web3.WebSocketConnected += HandleWeb3WebSocketConnected;
            web3EventsRegistered = true;

            if (web3.Account != null)
            {
                HandleWeb3LoggedIn(web3.Account);
            }
            else
            {
                UpdateStreamingHealth(true);
            }
        }

        private void UnregisterWeb3EventHandlers()
        {
            if (!web3EventsRegistered || options.SimulateAlwaysConnected)
            {
                return;
            }

            web3.WalletStateChanged -= HandleWeb3WalletStateChanged;
            web3.LoggedIn -= HandleWeb3LoggedIn;
            web3.LoggedOut -= HandleWeb3LoggedOut;
            web3.BalanceChanged -= HandleWeb3BalanceChanged;
            web3.WebSocketConnected -= HandleWeb3WebSocketConnected;
            web3EventsRegistered = false;
        }

        private void HandleWeb3WalletStateChanged()
        {
            if (options.SimulateAlwaysConnected)
            {
                return;
            }

            RefreshVerificationStatus();
        }

        private void HandleWeb3LoggedIn(Account account)
        {
            if (options.SimulateAlwaysConnected || account?.PublicKey == null)
            {
                return;
            }

            bool wasActive = hasActiveSession;
            string newKey = account.PublicKey.Key;
            bool isNewAccount = string.IsNullOrEmpty(lastConnectedPublicKey) ||
                                !string.Equals(lastConnectedPublicKey, newKey, StringComparison.Ordinal);

            hasActiveSession = true;
            lastConnectedPublicKey = newKey;
            UpdateStreamingHealth(false);
            RefreshVerificationStatus();

            if (!wasActive || isNewAccount)
            {
                Connected?.Invoke();
                if (IsWalletVerified && CurrentPublicKey != null)
                {
                    WalletVerified?.Invoke(CurrentPublicKey.Key);
                }
            }
        }

        private void HandleWeb3LoggedOut()
        {
            if (options.SimulateAlwaysConnected)
            {
                return;
            }

            bool wasActive = hasActiveSession;
            hasActiveSession = false;
            lastConnectedPublicKey = null;
            lastKnownBalance = null;
            isWalletVerified = false;
            UpdateStreamingHealth(true);

            if (wasActive)
            {
                Disconnected?.Invoke();
            }
        }

        private void HandleWeb3BalanceChanged(double solAmount)
        {
            if (options.SimulateAlwaysConnected)
            {
                return;
            }

            ulong lamports = ConvertSolToLamports(solAmount);
            UpdateStreamingHealth(false);
            UpdateSolBalance(lamports);
        }

        private void HandleWeb3WebSocketConnected()
        {
            if (options.SimulateAlwaysConnected)
            {
                return;
            }

            UpdateStreamingHealth(false);
        }

        private static ulong ConvertSolToLamports(double solAmount)
        {
            if (double.IsNaN(solAmount) || double.IsInfinity(solAmount))
            {
                return 0UL;
            }

            double lamports = solAmount * LamportsPerSol;

            if (lamports <= 0d)
            {
                return 0UL;
            }

            if (lamports >= ulong.MaxValue)
            {
                return ulong.MaxValue;
            }

            return (ulong)Math.Round(lamports, MidpointRounding.AwayFromZero);
        }

        private void UpdateSolBalance(ulong lamports)
        {
            if (lastKnownBalance.HasValue && lastKnownBalance.Value == lamports)
            {
                return;
            }

            lastKnownBalance = lamports;
            SolBalanceChanged?.Invoke(lamports);
        }

        private void UpdateStreamingHealth(bool degraded)
        {
            if (isStreamingDegraded == degraded)
            {
                return;
            }

            isStreamingDegraded = degraded;
            StreamingHealthChanged?.Invoke(degraded);
        }
    }
}

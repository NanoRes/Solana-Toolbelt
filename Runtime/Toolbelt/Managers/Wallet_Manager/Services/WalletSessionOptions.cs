using System;
#if UNITY_EDITOR
using Solana.Unity.SDK;
#endif

namespace Solana.Unity.Toolbelt.Wallet
{
    /// <summary>
    /// Configuration used by <see cref="WalletSessionService"/> to control
    /// simulation behaviour and editor specific options.
    /// </summary>
    [Serializable]
    public class WalletSessionOptions
    {
        /// <summary>
        /// If true the session always reports as connected and uses the
        /// <see cref="SimulatedPublicKey"/> to satisfy public key requests.
        /// </summary>
        public bool SimulateAlwaysConnected;

        /// <summary>
        /// If true transaction requests return a simulated success signature
        /// without contacting the network.
        /// </summary>
        public bool SimulatePaymentSuccess;

        /// <summary>
        /// Public key used when <see cref="SimulateAlwaysConnected"/> is enabled.
        /// </summary>
        public string SimulatedPublicKey = "GhibVT1e5GTp2eEjtJFkgpfovYdFHpb9NTxjcj2o7a2D";

        /// <summary>
        /// Minimum interval, in seconds, used when polling SOL balance as a
        /// fallback while websocket streaming is unavailable.
        /// </summary>
        [Obsolete("Web3 handles streaming fallback internally; manual polling is no longer used.")]
        public float StreamingFallbackMinPollIntervalSeconds = 5f;

        /// <summary>
        /// Maximum interval, in seconds, that the polling loop may grow to
        /// when applying exponential backoff during outages.
        /// </summary>
        [Obsolete("Web3 handles streaming fallback internally; manual polling is no longer used.")]
        public float StreamingFallbackMaxPollIntervalSeconds = 60f;

        /// <summary>
        /// Multiplier applied to the polling interval each time a balance
        /// update attempt fails during a streaming outage. Values less than or
        /// equal to one are treated as two.
        /// </summary>
        [Obsolete("Web3 handles streaming fallback internally; manual polling is no longer used.")]
        public float StreamingFallbackBackoffMultiplier = 2f;

#if UNITY_EDITOR
        /// <summary>
        /// When enabled the session will bootstrap using the editor private key
        /// stored via <see cref="EditorWalletDevTools"/>.
        /// </summary>
        public bool UseEditorPrivateKey;

        /// <summary>
        /// RPC cluster used when instantiating the in-game wallet for editor
        /// private key sessions. Defaults to MainNet.
        /// </summary>
        public RpcCluster EditorInGameWalletCluster = RpcCluster.MainNet;

        /// <summary>
        /// Custom HTTP RPC endpoint used for editor in-game wallet sessions.
        /// Blank values are ignored and fall back to the cluster default.
        /// </summary>
        public string EditorInGameWalletCustomRpc = string.Empty;

        /// <summary>
        /// Custom streaming RPC endpoint used for editor in-game wallet
        /// sessions. Blank values are ignored and fall back to the cluster
        /// default.
        /// </summary>
        public string EditorInGameWalletStreamingRpc = string.Empty;

        /// <summary>
        /// Whether the editor in-game wallet should auto connect on startup.
        /// </summary>
        public bool EditorInGameWalletAutoConnectOnStartup = false;
#endif
    }
}

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Solana.Unity.Toolbelt.Wallet
{
    /// <summary>
    /// Handles message signing and signature verification to confirm wallet
    /// ownership.
    /// </summary>
    public class WalletVerificationService : IWalletVerificationService
    {
        private readonly IWalletSessionService sessionService;
        private readonly IWeb3Facade web3;
        private readonly IWalletLogger logger;
        private readonly object verificationLock = new();
        private Task<bool> pendingVerificationTask;
        private readonly SynchronizationContext unitySynchronizationContext;
        private readonly int unityThreadId;

        public WalletVerificationService(
            IWalletSessionService sessionService,
            IWeb3Facade web3,
            IWalletLogger logger)
        {
            this.sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
            this.web3 = web3 ?? throw new ArgumentNullException(nameof(web3));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            unitySynchronizationContext = SynchronizationContext.Current;
            unityThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        private static bool SupportsBackgroundThreads
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return false;
#else
                return true;
#endif
            }
        }

        public async Task<bool> VerifyOwnershipAsync(string message)
        {
            sessionService.RefreshVerificationStatus();
            if (sessionService.IsWalletVerified)
            {
                logger.Log("[WalletVerification] Wallet already verified; skipping signature request.");
                return true;
            }

            Task<bool> verificationTask;
            lock (verificationLock)
            {
                if (pendingVerificationTask == null || pendingVerificationTask.IsCompleted)
                {
                    pendingVerificationTask = VerifyOwnershipInternalAsync(message);
                }

                verificationTask = pendingVerificationTask;
            }

            try
            {
                return await verificationTask.ConfigureAwait(false);
            }
            finally
            {
                if (verificationTask.IsCompleted)
                {
                    lock (verificationLock)
                    {
                        if (ReferenceEquals(pendingVerificationTask, verificationTask))
                        {
                            pendingVerificationTask = null;
                        }
                    }
                }
            }
        }

        private async Task<bool> VerifyOwnershipInternalAsync(string message)
        {
#if UNITY_EDITOR
            if (!sessionService.IsWalletConnected || sessionService.CurrentPublicKey == null)
            {
                logger.LogWarning("[WalletVerification] Cannot verify ownership without an active wallet session.");
                return false;
            }

            sessionService.MarkWalletVerified(sessionService.CurrentPublicKey.Key);
            logger.Log("[WalletVerification] Unity Editor detected; skipping signature verification.");
            return true;
#else
            if (!sessionService.IsWalletConnected || sessionService.CurrentPublicKey == null)
            {
                logger.LogWarning("[WalletVerification] Cannot verify ownership without an active wallet session.");
                return false;
            }

            var activePublicKey = sessionService.CurrentPublicKey;
            string verifiedPublicKey = activePublicKey.Key;
            byte[] verifiedPublicKeyBytes = (byte[])activePublicKey.KeyBytes?.Clone();

            if (verifiedPublicKeyBytes == null || verifiedPublicKeyBytes.Length == 0)
            {
                logger.LogWarning("[WalletVerification] Wallet public key was unavailable during verification.");
                await RunOnUnityThreadAsync(sessionService.ClearVerificationStatus).ConfigureAwait(false);
                return false;
            }

            try
            {
                string messageWithNonce = AppendNonce(message);
                byte[] messageBytes = Encoding.UTF8.GetBytes(messageWithNonce);
                byte[] signatureBytes = await web3.SignMessageAsync(messageBytes).ConfigureAwait(false);

                if (signatureBytes == null || signatureBytes.Length == 0)
                {
                    logger.LogWarning("[WalletVerification] Wallet returned an empty signature.");
                    await RunOnUnityThreadAsync(sessionService.ClearVerificationStatus).ConfigureAwait(false);
                    return false;
                }

                bool verified;
                if (SupportsBackgroundThreads)
                {
                    verified = await Task.Run(() =>
                        Ed25519.Verify(signatureBytes, 0, verifiedPublicKeyBytes, 0, messageBytes, 0, messageBytes.Length)).ConfigureAwait(false);
                }
                else
                {
                    verified = Ed25519.Verify(signatureBytes, 0, verifiedPublicKeyBytes, 0, messageBytes, 0, messageBytes.Length);
                }

                if (verified)
                {
                    await RunOnUnityThreadAsync(() => sessionService.MarkWalletVerified(verifiedPublicKey)).ConfigureAwait(false);
                    logger.Log("[WalletVerification] Wallet signature verified.");
                    return true;
                }

                logger.LogWarning("[WalletVerification] Signature verification failed.");
                await RunOnUnityThreadAsync(sessionService.ClearVerificationStatus).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError($"[WalletVerification] Message signing failed: {ex.Message}");
                await RunOnUnityThreadAsync(sessionService.ClearVerificationStatus).ConfigureAwait(false);
            }

            return false;
#endif
        }


        private static string AppendNonce(string message)
        {
            string baseMessage = message ?? string.Empty;
            byte[] nonceBytes = new byte[16];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(nonceBytes);
            }

            string nonce = BitConverter.ToString(nonceBytes).Replace("-", string.Empty);

            return string.Concat(baseMessage,
                Environment.NewLine,
                "Nonce: ",
                nonce,
                Environment.NewLine,
                "Issued At (UTC): ",
                DateTime.UtcNow.ToString("O"));
        }

        private Task RunOnUnityThreadAsync(Action action)
        {
            if (action == null)
            {
                return Task.CompletedTask;
            }

            if (Thread.CurrentThread.ManagedThreadId == unityThreadId)
            {
                action();
                return Task.CompletedTask;
            }

            var completionSource = new TaskCompletionSource<bool>();

            void InvokeAction()
            {
                try
                {
                    action();
                    completionSource.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    completionSource.TrySetException(ex);
                }
            }

            if (unitySynchronizationContext != null)
            {
                unitySynchronizationContext.Post(_ => InvokeAction(), null);
            }
            else if (MainThreadDispatcher.Exists())
            {
                try
                {
                    MainThreadDispatcher.Instance().Enqueue(InvokeAction);
                }
                catch (Exception ex)
                {
                    completionSource.TrySetException(ex);
                }
            }
            else
            {
                InvokeAction();
            }

            return completionSource.Task;
        }
    }
}

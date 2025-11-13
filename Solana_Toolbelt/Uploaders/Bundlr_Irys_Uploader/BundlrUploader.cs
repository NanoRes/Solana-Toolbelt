using System;
using System.Collections.Generic;
using System.Numerics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Solana.Unity.Programs;
using Solana.Unity.Rpc;
using Solana.Unity.Rpc.Builders;
using Solana.Unity.Rpc.Core.Http;
using Solana.Unity.Rpc.Models;
using Solana.Unity.Rpc.Types;
using Solana.Unity.Wallet;
using Solana.Unity.Toolbelt.Internal;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Uploads JSON metadata and optional media assets to Arweave using the
    /// Irys/Bundlr REST API. The component signs DataItems locally using a
    /// configured Solana private key and returns the resulting Arweave URIs.
    /// </summary>
    [DisallowMultipleComponent]
    public class BundlrUploader : MonoBehaviour, INftStorageUploader, ILevelJsonUploader, IUploadCostEstimator
    {
        [Header("Bundlr Node")]
        [Tooltip("Bundlr/Irys node endpoint used for uploads")]
        [SerializeField]
        private string bundlrNodeUrl = "https://node1.irys.xyz";

        [Tooltip("Currency identifier supported by the Bundlr node")]
        [SerializeField]
        private string currency = "solana";

        [Tooltip("Arweave gateway used to construct public URIs")]
        [SerializeField]
        private string arweaveGatewayUrl = "https://gateway.irys.xyz";

        [Header("Authentication")]
        [Tooltip("Base58 encoded Solana private key used to sign Bundlr uploads. Leave empty to load from environment variable.")]
        [SerializeField]
        private string privateKeyBase58 = string.Empty;

        [Tooltip("Environment variable that stores the Bundlr private key. Used when the inspector field is left blank.")]
        [SerializeField]
        private string privateKeyEnvironmentVariable = string.Empty;

        [Header("Behaviour")]
        [Tooltip("Timeout applied to HTTP requests performed against the Bundlr node (seconds)")]
        [SerializeField]
        private float requestTimeoutSeconds = 30f;

        [Tooltip("Perform a balance check before uploading and throw if the Bundlr account is unfunded")]
        [SerializeField]
        private bool checkBalanceBeforeUpload = true;

        [Tooltip("Optional tag recorded on uploads to identify the client application")]
        [SerializeField]
        private string appNameTag = "Solana Toolbelt";

        [Tooltip("Emit debug logs when uploads succeed")]
        [SerializeField]
        private bool logDebugMessages = false;

        private IBundlrUploadTransport transport;
        private string runtimePrivateKeyOverride;
        private Account cachedUploaderAccount;
        private HttpClient nodeInfoClient;
        private string cachedDepositAddress;
        private static readonly BigInteger UlongMaxBigInt = new BigInteger(ulong.MaxValue);
        private const ulong FundingTransactionFeeLamports = 5000UL;

        /// <summary>
        /// Override the private key at runtime. Calling this will reset the
        /// underlying Bundlr client so the new credentials are used on the next upload.
        /// </summary>
        public void SetPrivateKey(string base58PrivateKey)
        {
            runtimePrivateKeyOverride = base58PrivateKey;
            ResetTransport();
        }

        /// <summary>
        /// Upload metadata JSON and optionally an accompanying image. The returned
        /// result exposes both Arweave URIs so callers can reference them from NFT metadata.
        /// </summary>
        public Task<BundlrMetadataUploadResult> UploadJsonAndOptionalImageAsync(
            string jsonFileName,
            string jsonContent,
            byte[] imageData = null,
            string imageFileName = null,
            string imageContentType = null,
            IEnumerable<KeyValuePair<string, string>> jsonTags = null,
            IEnumerable<KeyValuePair<string, string>> imageTags = null,
            CancellationToken cancellationToken = default)
        {
            return UploadJsonAndOptionalImageInternalAsync(
                jsonFileName,
                jsonContent,
                imageData,
                imageFileName,
                imageContentType,
                jsonTags,
                imageTags,
                cancellationToken);
        }

        /// <inheritdoc />
        public async Task<string> UploadJsonAsync(string fileName, string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON content is empty", nameof(json));

            var jsonBytes = Encoding.UTF8.GetBytes(json);
            var result = await UploadFileAsync(
                jsonBytes,
                EnsureJsonFileName(fileName),
                "application/json",
                null,
                CancellationToken.None);
            return result.Uri;
        }

        /// <inheritdoc />
        public async Task<string> UploadMediaAsync(string fileName, byte[] data, string contentType)
        {
            var result = await UploadFileAsync(
                data,
                string.IsNullOrWhiteSpace(fileName) ? null : fileName,
                contentType,
                null,
                CancellationToken.None);
            return result.Uri;
        }

        /// <summary>
        /// Returns the public key (base58) of the Bundlr signer account used for uploads.
        /// </summary>
        public string GetUploaderWalletAddress()
        {
            var account = ResolveUploaderAccount(throwOnMissingKey: false);
            return account?.PublicKey?.Key;
        }

        /// <summary>
        /// Query the Bundlr node for the current balance in atomic units.
        /// </summary>
        public Task<BigInteger> GetBundlrBalanceAsync(CancellationToken cancellationToken)
        {
            var activeTransport = EnsureTransport();
            return activeTransport.GetBalanceAsync(cancellationToken);
        }

        /// <summary>
        /// Query the Bundlr node for the price (in atomic units) of uploading the given payload size.
        /// </summary>
        public Task<BigInteger> GetUploadPriceAsync(int dataLength, CancellationToken cancellationToken)
        {
            var activeTransport = EnsureTransport();
            return activeTransport.GetPriceAsync(dataLength, cancellationToken);
        }

        /// <summary>
        /// Estimate the SOL required for the deposit transaction fee.
        /// </summary>
        public ulong GetEstimatedFundingFeeLamports() => FundingTransactionFeeLamports;

        /// <summary>
        /// Fetch the SOL balance of the Bundlr uploader wallet.
        /// </summary>
        public async Task<ulong> GetUploaderWalletLamportsAsync(IRpcClient rpcClient, Commitment commitment, CancellationToken cancellationToken)
        {
            if (rpcClient == null)
                throw new ArgumentNullException(nameof(rpcClient));

            var account = ResolveUploaderAccount(throwOnMissingKey: true);
            var balanceResult = await rpcClient.GetBalanceAsync(account.PublicKey.Key, commitment).ConfigureAwait(false);
            if (balanceResult == null || !balanceResult.WasSuccessful || balanceResult.Result?.Value == null)
                return 0UL;

            return balanceResult.Result.Value;
        }

        /// <summary>
        /// Attempt to deposit lamports into the Bundlr node from the uploader wallet.
        /// Returns <c>true</c> when the transaction was submitted or <c>false</c> when
        /// the uploader wallet does not currently have enough SOL to cover the deposit
        /// and transaction fee.
        /// </summary>
        public async Task<bool> TryDepositAsync(
            IRpcClient rpcClient,
            ulong lamports,
            CancellationToken cancellationToken,
            bool throwOnFailure = false)
        {
            if (rpcClient == null)
                throw new ArgumentNullException(nameof(rpcClient));

            if (lamports == 0)
                return true;

            var account = ResolveUploaderAccount(throwOnMissingKey: true);

            ulong requiredLamports = lamports + FundingTransactionFeeLamports;
            ulong currentLamports = await GetUploaderWalletLamportsAsync(rpcClient, Commitment.Confirmed, cancellationToken).ConfigureAwait(false);
            if (currentLamports < requiredLamports)
            {
                if (throwOnFailure)
                {
                    throw new InvalidOperationException(
                        $"Bundlr uploader wallet has insufficient SOL. Required {requiredLamports} lamports but only {currentLamports} are available.");
                }

                return false;
            }

            string depositAddress = await GetBundlrDepositAddressAsync(cancellationToken).ConfigureAwait(false);

            var blockHashRes = await rpcClient.GetLatestBlockHashAsync(Commitment.Confirmed).ConfigureAwait(false);
            if (blockHashRes == null || !blockHashRes.WasSuccessful || blockHashRes.Result?.Value == null)
                throw new InvalidOperationException($"Failed to fetch recent blockhash: {blockHashRes?.Reason ?? "Unknown error"}.");

            var txBuilder = new TransactionBuilder()
                .SetRecentBlockHash(blockHashRes.Result.Value.Blockhash)
                .SetFeePayer(account)
                .AddInstruction(SystemProgram.Transfer(account.PublicKey, new PublicKey(depositAddress), lamports));

            var builtTx = txBuilder.Build(new[] { account });
            var transaction = Transaction.Deserialize(builtTx);
            transaction.Sign(account);

            var sendRes = await rpcClient.SendAndConfirmTransactionAsync(
                transaction.Serialize(),
                skipPreflight: false,
                commitment: Commitment.Confirmed).ConfigureAwait(false);

            if (sendRes == null || !sendRes.WasSuccessful || string.IsNullOrEmpty(sendRes.Result))
            {
                if (throwOnFailure)
                {
                    throw new InvalidOperationException($"Bundlr funding transaction failed: {sendRes?.Reason ?? "Unknown error"}.");
                }

                return false;
            }

            if (logDebugMessages)
            {
                Debug.Log($"[BundlrUploader] Funded Bundlr node with {lamports} lamports. Tx: {sendRes.Result}");
            }

            return true;
        }

        private async Task<BundlrMetadataUploadResult> UploadJsonAndOptionalImageInternalAsync(
            string jsonFileName,
            string jsonContent,
            byte[] imageData,
            string imageFileName,
            string imageContentType,
            IEnumerable<KeyValuePair<string, string>> jsonTags,
            IEnumerable<KeyValuePair<string, string>> imageTags,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(jsonContent))
                throw new ArgumentException("JSON content is empty", nameof(jsonContent));

            BundlrFileUploadResult imageResult = null;
            if (imageData != null && imageData.Length > 0)
            {
                imageResult = await UploadFileAsync(
                    imageData,
                    string.IsNullOrWhiteSpace(imageFileName) ? null : imageFileName,
                    imageContentType,
                    imageTags,
                    cancellationToken);
            }

            var jsonBytes = Encoding.UTF8.GetBytes(jsonContent);
            var jsonResult = await UploadFileAsync(
                jsonBytes,
                EnsureJsonFileName(jsonFileName),
                "application/json",
                jsonTags,
                cancellationToken);

            return new BundlrMetadataUploadResult(jsonResult, imageResult);
        }

        private async Task<BundlrFileUploadResult> UploadFileAsync(
            byte[] data,
            string fileName,
            string contentType,
            IEnumerable<KeyValuePair<string, string>> additionalTags,
            CancellationToken cancellationToken)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("No data provided for upload", nameof(data));

            var activeTransport = EnsureTransport();
            var response = await activeTransport.UploadAsync(
                data,
                fileName,
                contentType,
                additionalTags,
                cancellationToken);
            if (logDebugMessages)
            {
                Debug.Log($"[BundlrUploader] Uploaded {(fileName ?? "<unnamed>")} ({data.Length} bytes) -> {response.Id}");
            }

            return response;
        }

        /// <inheritdoc />
        public async Task<ulong?> EstimateUploadCostLamportsAsync(int dataSizeBytes, CancellationToken cancellationToken)
        {
            if (dataSizeBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(dataSizeBytes), "Data size cannot be negative.");

            var activeTransport = EnsureTransport();
            BigInteger price = await activeTransport.GetPriceAsync(dataSizeBytes, cancellationToken).ConfigureAwait(false);

            if (price < BigInteger.Zero)
                price = BigInteger.Zero;

            if (price > new BigInteger(ulong.MaxValue))
                throw new InvalidOperationException($"Bundlr price {price} exceeds supported range.");

            return (ulong)price;
        }

        private IBundlrUploadTransport EnsureTransport()
        {
            if (transport != null)
                return transport;

            var configuration = BuildConfiguration();
            var provider = CreateCredentialProvider();
            transport = new BundlrUploadTransport(configuration, provider);
            return transport;
        }
        //
        private Account ResolveUploaderAccount(bool throwOnMissingKey)
        {
            if (cachedUploaderAccount != null)
                return cachedUploaderAccount;

            string privateKey = ResolvePrivateKey();
            if (string.IsNullOrWhiteSpace(privateKey))
            {
                if (throwOnMissingKey)
                    throw new InvalidOperationException("Bundlr private key not configured.");

                return null;
            }

            string trimmedKey = privateKey.Trim();

            try
            {
                cachedUploaderAccount = CreateAccountFromPrivateKey(trimmedKey);
                return cachedUploaderAccount;
            }
            catch (Exception ex)
            {
                cachedUploaderAccount = null;
                if (throwOnMissingKey)
                    throw new InvalidOperationException($"Failed to load Bundlr private key: {ex.Message}", ex);

                Debug.LogError($"[BundlrUploader] Unable to load Bundlr private key: {ex.Message}");
                return null;
            }
        }

        private string ResolvePrivateKey()
        {
            if (!string.IsNullOrWhiteSpace(runtimePrivateKeyOverride))
                return runtimePrivateKeyOverride;

            if (!string.IsNullOrWhiteSpace(privateKeyBase58))
                return privateKeyBase58;

            if (!string.IsNullOrWhiteSpace(privateKeyEnvironmentVariable))
            {
                var env = Environment.GetEnvironmentVariable(privateKeyEnvironmentVariable);
                if (!string.IsNullOrWhiteSpace(env))
                    return env;
            }

            return null;
        }

        private static Account CreateAccountFromPrivateKey(string privateKeyBase58)
        {
            if (string.IsNullOrWhiteSpace(privateKeyBase58))
                throw new ArgumentException("Private key cannot be empty.", nameof(privateKeyBase58));

            string publicKeyBase58 = TryDerivePublicKey(privateKeyBase58);

            if (!string.IsNullOrEmpty(publicKeyBase58))
                return new Account(privateKeyBase58, publicKeyBase58);

            return new Account(privateKeyBase58, string.Empty);
        }

        private static string TryDerivePublicKey(string privateKeyBase58)
        {
            try
            {
                var decoded = Base58Utility.Decode(privateKeyBase58);
                if (decoded.Length == 64)
                {
                    var publicKey = new byte[32];
                    Buffer.BlockCopy(decoded, 32, publicKey, 0, 32);
                    return Base58Utility.Encode(publicKey);
                }

                if (decoded.Length == 32)
                {
                    var privateKey = new byte[32];
                    Buffer.BlockCopy(decoded, 0, privateKey, 0, 32);
                    var publicKey = new byte[32];
                    Ed25519.GeneratePublicKey(privateKey, 0, publicKey, 0);
                    return Base58Utility.Encode(publicKey);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BundlrUploader] Failed to derive public key from private key: {ex.Message}");
            }

            return null;
        }

        private async Task<string> GetBundlrDepositAddressAsync(CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(cachedDepositAddress))
                return cachedDepositAddress;

            var client = EnsureNodeInfoClient();
            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(string.Empty, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to query Bundlr node for deposit address: {ex.Message}", ex);
            }

            using (response)
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"Bundlr node responded with {(int)response.StatusCode} when fetching deposit address: {body}");
                }

                try
                {
                    var token = JToken.Parse(body);
                    var address = token?["addresses"]?["solana"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(address))
                        throw new InvalidOperationException("Bundlr node did not return a Solana deposit address.");

                    cachedDepositAddress = address.Trim();
                    return cachedDepositAddress;
                }
                catch (Exception ex) when (ex is JsonException || ex is ArgumentException)
                {
                    throw new InvalidOperationException("Failed to parse Bundlr node info response.", ex);
                }
            }
        }

        private HttpClient EnsureNodeInfoClient()
        {
            if (nodeInfoClient != null)
                return nodeInfoClient;

            string baseUrl = string.IsNullOrWhiteSpace(bundlrNodeUrl)
                ? "https://node1.irys.xyz/"
                : bundlrNodeUrl.Trim();

            if (!baseUrl.EndsWith("/", StringComparison.Ordinal))
                baseUrl += "/";

            try
            {
                nodeInfoClient = new HttpClient
                {
                    BaseAddress = new Uri(baseUrl),
                    Timeout = TimeSpan.FromSeconds(Mathf.Max(1f, requestTimeoutSeconds))
                };
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Invalid Bundlr node URL '{bundlrNodeUrl}': {ex.Message}", ex);
            }

            return nodeInfoClient;
        }

        private BundlrUploadConfiguration BuildConfiguration()
        {
            return new BundlrUploadConfiguration(
                bundlrNodeUrl,
                currency,
                arweaveGatewayUrl,
                TimeSpan.FromSeconds(Mathf.Max(1f, requestTimeoutSeconds)),
                checkBalanceBeforeUpload,
                appNameTag,
                "1.1.1");
        }

        private IBundlrCredentialProvider CreateCredentialProvider()
        {
            IBundlrCredentialProvider provider = new InspectorCredentialProvider(privateKeyBase58, privateKeyEnvironmentVariable);

            if (!string.IsNullOrWhiteSpace(runtimePrivateKeyOverride))
            {
                provider = new OverrideCredentialProvider(runtimePrivateKeyOverride, provider);
            }

            return provider;
        }

        private void ResetTransport()
        {
            transport?.Dispose();
            transport = null;
            cachedUploaderAccount = null;
            cachedDepositAddress = null;
        }

        private string EnsureJsonFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return $"{Guid.NewGuid()}.json";

            fileName = fileName.Trim();
            if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                fileName += ".json";
            return fileName;
        }

        private void OnDestroy()
        {
            ResetTransport();
            nodeInfoClient?.Dispose();
            nodeInfoClient = null;
        }

        private sealed class InspectorCredentialProvider : IBundlrCredentialProvider
        {
            private readonly string privateKeyBase58;
            private readonly string environmentVariable;

            public InspectorCredentialProvider(string privateKeyBase58, string environmentVariable)
            {
                this.privateKeyBase58 = privateKeyBase58;
                this.environmentVariable = environmentVariable;
            }

            public string ResolvePrivateKey()
            {
                if (!string.IsNullOrWhiteSpace(privateKeyBase58))
                    return privateKeyBase58.Trim();

                if (!string.IsNullOrWhiteSpace(environmentVariable))
                {
                    var envValue = Environment.GetEnvironmentVariable(environmentVariable);
                    if (!string.IsNullOrWhiteSpace(envValue))
                        return envValue.Trim();
                }

                return null;
            }
        }

        private sealed class OverrideCredentialProvider : IBundlrCredentialProvider
        {
            private readonly string overrideKey;
            private readonly IBundlrCredentialProvider inner;

            public OverrideCredentialProvider(string overrideKey, IBundlrCredentialProvider inner)
            {
                this.overrideKey = overrideKey;
                this.inner = inner;
            }

            public string ResolvePrivateKey()
            {
                if (!string.IsNullOrWhiteSpace(overrideKey))
                    return overrideKey.Trim();

                return inner?.ResolvePrivateKey();
            }
        }
    }
}

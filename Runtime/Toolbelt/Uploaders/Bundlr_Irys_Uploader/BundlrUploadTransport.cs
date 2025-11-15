using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Solana.Unity.Toolbelt.Internal;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Provides access to Bundlr/Irys upload functionality without depending on MonoBehaviour.
    /// </summary>
    public interface IBundlrUploadTransport : IDisposable
    {
        Task<BundlrFileUploadResult> UploadAsync(
            byte[] data,
            string fileName,
            string contentType,
            IEnumerable<KeyValuePair<string, string>> additionalTags,
            CancellationToken cancellationToken);

        Task<BigInteger> GetPriceAsync(int dataLength, CancellationToken cancellationToken);

        Task<BigInteger> GetBalanceAsync(CancellationToken cancellationToken);

        IBundlrNetworkClient EnsureClient();

        IReadOnlyList<BundlrTag> BuildTags(
            string fileName,
            string contentType,
            IEnumerable<KeyValuePair<string, string>> additionalTags);
    }

    /// <summary>
    /// Resolves the credential material required to sign Bundlr uploads.
    /// </summary>
    public interface IBundlrCredentialProvider
    {
        string ResolvePrivateKey();
    }

    /// <summary>
    /// Factory used to create the network client that communicates with the Bundlr node.
    /// </summary>
    public interface IBundlrNetworkClientFactory
    {
        IBundlrNetworkClient Create(BundlrUploadConfiguration configuration, string privateKey);
    }

    /// <summary>
    /// Abstraction over the HTTP/Irys client used by the transport service. Exposed for testing.
    /// </summary>
    public interface IBundlrNetworkClient : IDisposable
    {
        Task<BundlrFileUploadResult> UploadAsync(
            byte[] data,
            IReadOnlyList<BundlrTag> tags,
            string fileName,
            string contentType,
            CancellationToken cancellationToken);

        Task<BigInteger> GetPriceAsync(int dataLength, CancellationToken cancellationToken);

        Task<BigInteger> GetBalanceAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Immutable configuration for the Bundlr upload transport.
    /// </summary>
    public sealed class BundlrUploadConfiguration
    {
        public string BundlrNodeUrl { get; }
        public string Currency { get; }
        public string ArweaveGatewayUrl { get; }
        public TimeSpan RequestTimeout { get; }
        public bool CheckBalanceBeforeUpload { get; }
        public string AppNameTag { get; }
        public string AppVersionTag { get; }

        public BundlrUploadConfiguration(
            string bundlrNodeUrl,
            string currency,
            string arweaveGatewayUrl,
            TimeSpan requestTimeout,
            bool checkBalanceBeforeUpload,
            string appNameTag,
            string appVersionTag)
        {
            BundlrNodeUrl = bundlrNodeUrl?.Trim();
            Currency = string.IsNullOrWhiteSpace(currency) ? "solana" : currency.Trim();
            ArweaveGatewayUrl = string.IsNullOrWhiteSpace(arweaveGatewayUrl)
                ? "https://gateway.irys.xyz"
                : arweaveGatewayUrl.Trim();
            RequestTimeout = requestTimeout <= TimeSpan.Zero
                ? TimeSpan.FromSeconds(30)
                : requestTimeout;
            CheckBalanceBeforeUpload = checkBalanceBeforeUpload;
            AppNameTag = appNameTag?.Trim();
            AppVersionTag = appVersionTag?.Trim();
        }
    }

    /// <summary>
    /// Default Bundlr upload transport implementation that handles credential resolution,
    /// HTTP client creation and tag management.
    /// </summary>
    public sealed class BundlrUploadTransport : IBundlrUploadTransport
    {
        private readonly BundlrUploadConfiguration _configuration;
        private readonly IBundlrCredentialProvider _credentialProvider;
        private readonly IBundlrNetworkClientFactory _clientFactory;
        private readonly object _clientLock = new();

        private IBundlrNetworkClient _client;

        public BundlrUploadTransport(
            BundlrUploadConfiguration configuration,
            IBundlrCredentialProvider credentialProvider,
            IBundlrNetworkClientFactory clientFactory = null)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _credentialProvider = credentialProvider ?? throw new ArgumentNullException(nameof(credentialProvider));
            _clientFactory = clientFactory ?? new DefaultBundlrNetworkClientFactory(configuration);
        }

        public async Task<BundlrFileUploadResult> UploadAsync(
            byte[] data,
            string fileName,
            string contentType,
            IEnumerable<KeyValuePair<string, string>> additionalTags,
            CancellationToken cancellationToken)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("No data provided for upload", nameof(data));

            var client = EnsureClient();
            var tags = BuildTags(fileName, contentType, additionalTags);

            if (_configuration.CheckBalanceBeforeUpload)
            {
                BigInteger price = await client.GetPriceAsync(data.Length, cancellationToken).ConfigureAwait(false);
                BigInteger balance = await client.GetBalanceAsync(cancellationToken).ConfigureAwait(false);

                if (balance < price)
                {
                    throw new InvalidOperationException(
                        $"Bundlr account has insufficient balance. Required {price} atomic units but only {balance} are available. Fund the account before uploading.");
                }
            }

            return await client.UploadAsync(data, tags, fileName, contentType, cancellationToken).ConfigureAwait(false);
        }

        public IBundlrNetworkClient EnsureClient()
        {
            if (_client != null)
                return _client;

            lock (_clientLock)
            {
                if (_client != null)
                    return _client;

                string privateKey = _credentialProvider.ResolvePrivateKey();
                if (string.IsNullOrWhiteSpace(privateKey))
                    throw new InvalidOperationException("Bundlr private key not configured.");

                _client = _clientFactory.Create(_configuration, privateKey.Trim());
                if (_client == null)
                    throw new InvalidOperationException("Bundlr network client factory returned null.");

                return _client;
            }
        }

        public IReadOnlyList<BundlrTag> BuildTags(
            string fileName,
            string contentType,
            IEnumerable<KeyValuePair<string, string>> additionalTags)
        {
            var tags = new List<BundlrTag>();

            if (!string.IsNullOrWhiteSpace(_configuration.AppNameTag))
                tags.Add(new BundlrTag("App-Name", _configuration.AppNameTag));

            if (!string.IsNullOrWhiteSpace(_configuration.AppVersionTag))
                tags.Add(new BundlrTag("App-Version", _configuration.AppVersionTag));

            if (!string.IsNullOrWhiteSpace(contentType))
                tags.Add(new BundlrTag("Content-Type", contentType));

            if (!string.IsNullOrWhiteSpace(fileName))
                tags.Add(new BundlrTag("File-Name", fileName));

            if (additionalTags != null)
            {
                foreach (var kvp in additionalTags)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Key))
                        tags.Add(new BundlrTag(kvp.Key, kvp.Value ?? string.Empty));
                }
            }

            return tags;
        }

        public Task<BigInteger> GetPriceAsync(int dataLength, CancellationToken cancellationToken)
        {
            if (dataLength < 0)
                throw new ArgumentOutOfRangeException(nameof(dataLength), "Data length cannot be negative.");

            var client = EnsureClient();
            return client.GetPriceAsync(dataLength, cancellationToken);
        }

        public Task<BigInteger> GetBalanceAsync(CancellationToken cancellationToken)
        {
            var client = EnsureClient();
            return client.GetBalanceAsync(cancellationToken);
        }

        public void Dispose()
        {
            lock (_clientLock)
            {
                _client?.Dispose();
                _client = null;
            }
        }

        private static string BuildGatewayUri(BundlrUploadConfiguration configuration, string transactionId)
        {
            if (string.IsNullOrWhiteSpace(transactionId))
                return configuration.ArweaveGatewayUrl;

            var gateway = configuration.ArweaveGatewayUrl;
            return gateway.EndsWith("/", StringComparison.Ordinal)
                ? gateway + transactionId
                : gateway + "/" + transactionId;
        }

        private sealed class DefaultBundlrNetworkClientFactory : IBundlrNetworkClientFactory
        {
            private readonly BundlrUploadConfiguration _configuration;

            public DefaultBundlrNetworkClientFactory(BundlrUploadConfiguration configuration)
            {
                _configuration = configuration;
            }

            public IBundlrNetworkClient Create(BundlrUploadConfiguration configuration, string privateKey)
            {
                if (configuration == null)
                    throw new ArgumentNullException(nameof(configuration));

                if (string.IsNullOrWhiteSpace(configuration.BundlrNodeUrl))
                    throw new InvalidOperationException("Bundlr node URL is not configured.");

                string baseUri = configuration.BundlrNodeUrl.EndsWith("/", StringComparison.Ordinal)
                    ? configuration.BundlrNodeUrl
                    : configuration.BundlrNodeUrl + "/";

                var httpClient = new HttpClient
                {
                    BaseAddress = new Uri(baseUri),
                    Timeout = configuration.RequestTimeout
                };

                var signer = new Internal.SolanaBundlrSigner(privateKey);
                var innerClient = new Internal.IrysBundlrClient(
                    httpClient,
                    signer,
                    configuration.Currency,
                    id => BuildGatewayUri(_configuration, id));

                return new IrysBundlrNetworkClientAdapter(innerClient, httpClient);
            }
        }

        private sealed class IrysBundlrNetworkClientAdapter : IBundlrNetworkClient
        {
            private readonly Internal.IrysBundlrClient _innerClient;
            private readonly HttpClient _httpClient;
            private bool _disposed;

            public IrysBundlrNetworkClientAdapter(Internal.IrysBundlrClient innerClient, HttpClient httpClient)
            {
                _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
                _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            }

            public Task<BundlrFileUploadResult> UploadAsync(
                byte[] data,
                IReadOnlyList<BundlrTag> tags,
                string fileName,
                string contentType,
                CancellationToken cancellationToken)
            {
                ThrowIfDisposed();
                return _innerClient.UploadAsync(data, tags, fileName, contentType, cancellationToken);
            }

            public Task<BigInteger> GetPriceAsync(int dataLength, CancellationToken cancellationToken)
            {
                ThrowIfDisposed();
                return _innerClient.GetPriceAsync(dataLength, cancellationToken);
            }

            public Task<BigInteger> GetBalanceAsync(CancellationToken cancellationToken)
            {
                ThrowIfDisposed();
                return _innerClient.GetBalanceAsync(cancellationToken);
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;
                _httpClient.Dispose();
            }

            private void ThrowIfDisposed()
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(IrysBundlrNetworkClientAdapter));
            }
        }
    }
}

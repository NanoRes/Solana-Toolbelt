using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Solana.Unity.Rpc;
using Solana.Unity.Rpc.Models;
using Solana.Unity.Rpc.Types;
using Solana.Unity.Wallet;
using Solana.Unity.Metaplex.NFT.Library;
using UnityEngine;
using UnityEngine.Networking;

namespace Solana.Unity.Toolbelt
{
    internal static class MetadataQueryLogger
    {
        private const string Prefix = "[MetadataQuery]";

        public static bool VerboseLoggingEnabled { get; set; }

        public static void LogWarning(string message)
        {
            if (!VerboseLoggingEnabled || string.IsNullOrEmpty(message))
                return;

            Debug.LogWarning($"{Prefix} {message}");
        }

        public static void LogWarning(Func<string> messageFactory)
        {
            if (!VerboseLoggingEnabled || messageFactory == null)
                return;

            var message = messageFactory();
            if (string.IsNullOrEmpty(message))
                return;

            Debug.LogWarning($"{Prefix} {message}");
        }
    }

    [Serializable]
    public class MetadataResult
    {
        public bool success;
        public string errorMessage;
        public NftMetadata parsedData;
        public string onChainUri;
        public MetadataValidationResult validation;
    }

    [Serializable]
    public class NftMetadata
    {
        public string name;
        public string symbol;
        public string description;
        public string image;
        public List<Attribute> attributes;
        public string external_url;
        public Dictionary<string, object> additionalFields;
    }

    [Serializable]
    public class Attribute
    {
        public string trait_type;
        public string value;
    }

    public interface IMetadataAccountLoader
    {
        Task<MetadataAccount> LoadAsync(PublicKey mint, Commitment commitment = Commitment.Processed);
        Task<IReadOnlyList<MetadataAccount>> LoadManyAsync(IReadOnlyList<PublicKey> mints, Commitment commitment = Commitment.Processed);
    }

    public sealed class MetadataAccountLoader : IMetadataAccountLoader
    {
        private readonly IRpcClient _rpcClient;

        public MetadataAccountLoader(IRpcClient rpcClient)
        {
            _rpcClient = rpcClient ?? throw new ArgumentNullException(nameof(rpcClient));
        }

        public async Task<MetadataAccount> LoadAsync(PublicKey mint, Commitment commitment = Commitment.Processed)
        {
            if (mint == null)
                throw new ArgumentNullException(nameof(mint));

            try
            {
                return await MetadataAccount.GetAccount(_rpcClient, mint, commitment);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MetadataAccountLoader] Failed to fetch metadata for {mint.Key}: {ex.Message}");
                return null;
            }
        }

        public async Task<IReadOnlyList<MetadataAccount>> LoadManyAsync(IReadOnlyList<PublicKey> mints, Commitment commitment = Commitment.Processed)
        {
            if (mints == null)
                throw new ArgumentNullException(nameof(mints));

            if (mints.Count == 0)
                return Array.Empty<MetadataAccount>();

            var metadataAddresses = new List<PublicKey>(mints.Count);

            foreach (var mint in mints)
            {
                if (mint == null)
                    throw new ArgumentNullException(nameof(mints), "Mint entries cannot be null.");

                var metadataPda = Solana.Unity.Metaplex.Utilities.PDALookupExtensions.FindMetadataPDA(mint);
                metadataAddresses.Add(metadataPda);
            }

            var results = new MetadataAccount[mints.Count];
            var metadataAddressKeys = new List<string>(metadataAddresses.Count);

            foreach (var address in metadataAddresses)
            {
                metadataAddressKeys.Add(address.Key);
            }

            List<AccountInfo> bulkValues = null;

            try
            {
                var response = await _rpcClient.GetMultipleAccountsAsync(metadataAddressKeys, commitment).ConfigureAwait(false);
                if (response == null)
                {
                    Debug.LogWarning("[MetadataAccountLoader] Bulk metadata RPC response was null; falling back to individual requests.");
                }
                else if (!response.WasSuccessful)
                {
                    Debug.LogWarning($"[MetadataAccountLoader] Bulk metadata RPC failed (reason: {response.Reason ?? "<none>"}); falling back to individual requests.");
                }
                else if (response.Result == null)
                {
                    Debug.LogWarning("[MetadataAccountLoader] Bulk metadata RPC returned a null result; falling back to individual requests.");
                }
                else
                {
                    bulkValues = response.Result.Value;

                    if (bulkValues == null)
                    {
                        Debug.LogWarning("[MetadataAccountLoader] Bulk metadata RPC returned a null value list; falling back to individual requests where necessary.");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MetadataAccountLoader] Bulk metadata RPC threw an exception: {ex.Message}; falling back to individual requests.");
            }

            bool anySuccess = false;

            for (int i = 0; i < mints.Count; i++)
            {
                var mint = mints[i];
                MetadataAccount account = null;

                if (bulkValues != null)
                {
                    if (i >= bulkValues.Count)
                    {
                        Debug.LogWarning($"[MetadataAccountLoader] Bulk metadata list was shorter than expected for mint {mint?.Key} at index {i}; retrying individually.");
                    }
                    else
                    {
                        var accountInfo = bulkValues[i];

                        if (accountInfo != null)
                        {
                            try
                            {
                                account = await MetadataAccount.BuildMetadataAccount(accountInfo).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                Debug.LogWarning($"[MetadataAccountLoader] Failed to parse bulk metadata for mint {mint?.Key} at index {i}: {ex.Message}; retrying individually.");
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"[MetadataAccountLoader] Bulk metadata entry was null for mint {mint?.Key} at index {i}; retrying individually.");
                        }
                    }
                }

                if (account == null)
                {
                    try
                    {
                        account = await LoadAsync(mint, commitment).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[MetadataAccountLoader] Individual metadata lookup failed for mint {mint?.Key}: {ex.Message}");
                    }
                }

                if (account?.metadata != null)
                {
                    anySuccess = true;
                }
                else
                {
                    Debug.LogWarning($"[MetadataAccountLoader] Metadata lookup returned null for mint {mint?.Key} at index {i} after fallback attempts.");
                }

                results[i] = account;
            }

            if (!anySuccess)
            {
                throw new InvalidOperationException("Failed to load metadata for any of the requested mints.");
            }

            return results;
        }
    }

    public interface IMetadataJsonFetcher
    {
        Task<string> FetchAsync(string uri);
    }

    public sealed class UnityWebRequestMetadataJsonFetcher : IMetadataJsonFetcher
    {
        private static readonly string[] DefaultIpfsGateways =
        {
            "https://cloudflare-ipfs.com",
            "https://ipfs.io",
            "https://cf-ipfs.com"
        };

        private readonly int _timeoutSeconds;

        public UnityWebRequestMetadataJsonFetcher(int timeoutSeconds = 30)
        {
            if (timeoutSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Timeout must be non-negative.");

            _timeoutSeconds = timeoutSeconds;
        }

        public async Task<string> FetchAsync(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
                throw new ArgumentException("URI cannot be null or empty.", nameof(uri));

            Exception lastError = null;

            foreach (var candidate in BuildCandidateUris(uri))
            {
                try
                {
                    return await FetchFromGateway(candidate);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Debug.LogWarning($"[UnityWebRequestMetadataJsonFetcher] Failed to fetch metadata from {candidate}: {ex.Message}");
                }
            }

            throw lastError ?? new InvalidOperationException($"Unable to fetch metadata from {uri}.");
        }

        private async Task<string> FetchFromGateway(string uri)
        {
            using var request = UnityWebRequest.Get(uri);
            request.timeout = _timeoutSeconds;

            var operation = request.SendWebRequest();
            await AwaitRequest(operation);

#if UNITY_2020_1_OR_NEWER
            if (request.result != UnityWebRequest.Result.Success)
                throw new InvalidOperationException($"UnityWebRequest error fetching {uri}: {request.error}");
#else
            if (request.isNetworkError || request.isHttpError)
                throw new InvalidOperationException($"UnityWebRequest error fetching {uri}: {request.error}");
#endif

            return request.downloadHandler?.text ?? string.Empty;
        }

        private static IEnumerable<string> BuildCandidateUris(string originalUri)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in EnumerateCandidates(originalUri))
            {
                if (seen.Add(candidate))
                    yield return candidate;
            }
        }

        private static IEnumerable<string> EnumerateCandidates(string originalUri)
        {
            yield return originalUri;

            if (!Uri.TryCreate(originalUri, UriKind.Absolute, out var parsed))
                yield break;

            string queryFragment = string.Concat(parsed.Query, parsed.Fragment);

            if (string.Equals(parsed.Scheme, "ipfs", StringComparison.OrdinalIgnoreCase))
            {
                var ipfsPath = ($"{parsed.Authority}{parsed.AbsolutePath}").TrimStart('/');

                foreach (var gateway in DefaultIpfsGateways)
                {
                    yield return BuildGatewayUri(gateway, ipfsPath, queryFragment);
                }

                yield break;
            }

            if (parsed.AbsolutePath.StartsWith("/ipfs/", StringComparison.OrdinalIgnoreCase))
            {
                var ipfsPath = parsed.AbsolutePath.Substring("/ipfs/".Length).TrimStart('/');

                foreach (var gateway in DefaultIpfsGateways)
                {
                    yield return BuildGatewayUri(gateway, ipfsPath, queryFragment);
                }
            }
        }

        private static string BuildGatewayUri(string gatewayBase, string ipfsPath, string queryFragment)
        {
            var baseUri = gatewayBase.TrimEnd('/');
            var normalizedPath = ipfsPath?.TrimStart('/') ?? string.Empty;
            return $"{baseUri}/ipfs/{normalizedPath}{queryFragment}";
        }

        private static Task AwaitRequest(UnityWebRequestAsyncOperation operation)
        {
            if (operation.isDone)
                return Task.CompletedTask;

            var tcs = new TaskCompletionSource<bool>();
            operation.completed += _ => tcs.TrySetResult(true);
            return tcs.Task;
        }
    }

    public sealed class MetadataValidationResult
    {
        private MetadataValidationResult(bool isValid, string errorMessage, string computedHash, string expectedHash)
        {
            IsValid = isValid;
            ErrorMessage = errorMessage;
            ComputedHash = computedHash;
            ExpectedHash = expectedHash;
        }

        public bool IsValid { get; }
        public string ErrorMessage { get; }
        public string ComputedHash { get; }
        public string ExpectedHash { get; }

        public static MetadataValidationResult Success(string computedHash, string expectedHash)
            => new(true, null, computedHash, expectedHash);

        public static MetadataValidationResult Failure(string errorMessage, string computedHash, string expectedHash)
            => new(false, errorMessage, computedHash, expectedHash);
    }

    public interface IMetadataValidator
    {
        MetadataValidationResult Validate(string rawJson, NftMetadata metadata);
    }

    public sealed class MetadataValidator : IMetadataValidator
    {
        public MetadataValidationResult Validate(string rawJson, NftMetadata metadata)
        {
            string json = rawJson ?? string.Empty;
            string computedHash = ComputeSha256(json);

            if (metadata == null)
                return MetadataValidationResult.Failure("Metadata payload is missing.", computedHash, null);

            string expectedHash = ExtractSha256Attribute(metadata);

            if (string.IsNullOrEmpty(expectedHash))
                return MetadataValidationResult.Success(computedHash, expectedHash);

            return string.Equals(expectedHash, computedHash, StringComparison.OrdinalIgnoreCase)
                ? MetadataValidationResult.Success(computedHash, expectedHash)
                : MetadataValidationResult.Failure("invalid level data", computedHash, expectedHash);
        }

        private static string ExtractSha256Attribute(NftMetadata metadata)
        {
            if (metadata?.attributes == null)
                return null;

            foreach (var attribute in metadata.attributes)
            {
                if (attribute != null && attribute.trait_type == "sha256")
                    return attribute.value;
            }

            return null;
        }

        private static string ComputeSha256(string text)
        {
            using var sha = SHA256.Create();
            byte[] data = Encoding.UTF8.GetBytes(text);
            byte[] hash = sha.ComputeHash(data);
            var sb = new StringBuilder(hash.Length * 2);

            foreach (byte b in hash)
                sb.Append(b.ToString("x2"));

            return sb.ToString();
        }
    }

    public class MetadataQueryService
    {
        private readonly IMetadataAccountLoader _accountLoader;
        private readonly IMetadataJsonFetcher _jsonFetcher;
        private readonly IMetadataValidator _validator;

        public IMetadataJsonFetcher JsonFetcher => _jsonFetcher;

        public bool VerboseLoggingEnabled
        {
            get => MetadataQueryLogger.VerboseLoggingEnabled;
            set => MetadataQueryLogger.VerboseLoggingEnabled = value;
        }

        public Action<string> OnError { get; set; }
        public Action<MetadataValidationResult> OnValidationCompleted { get; set; }

        public static Task<MetadataQueryService> CreateAsync(IRpcClient rpcClient)
        {
            if (rpcClient == null)
                throw new ArgumentNullException(nameof(rpcClient));

            var service = new MetadataQueryService(
                new MetadataAccountLoader(rpcClient),
                new UnityWebRequestMetadataJsonFetcher(),
                new MetadataValidator());

            return Task.FromResult(service);
        }

        public MetadataQueryService(
            IMetadataAccountLoader accountLoader,
            IMetadataJsonFetcher jsonFetcher,
            IMetadataValidator validator)
        {
            _accountLoader = accountLoader ?? throw new ArgumentNullException(nameof(accountLoader));
            _jsonFetcher = jsonFetcher ?? throw new ArgumentNullException(nameof(jsonFetcher));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));

            OnError = MetadataQueryLogger.LogWarning;
        }

        public async Task<MetadataAccount> GetMetadataAccountAsync(string mintAddress, Commitment commitment = Commitment.Processed)
        {
            if (string.IsNullOrWhiteSpace(mintAddress))
            {
                OnError?.Invoke("Mint address is null or empty.");
                return null;
            }

            PublicKey mintPubKey;
            try
            {
                mintPubKey = new PublicKey(mintAddress);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Invalid mint public key: {ex.Message}");
                return null;
            }

            try
            {
                return await _accountLoader.LoadAsync(mintPubKey, commitment);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Failed to retrieve metadata for mint {mintAddress}: {ex.Message}");
                return null;
            }
        }

        public async Task<IReadOnlyDictionary<string, MetadataAccountResult>> GetMetadataAccountsAsync(
            IEnumerable<string> mintAddresses,
            Commitment commitment = Commitment.Processed)
        {
            var results = new Dictionary<string, MetadataAccountResult>(StringComparer.Ordinal);

            if (mintAddresses == null)
                return results;

            var uniqueMints = new List<string>();
            var mintKeys = new List<PublicKey>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var mint in mintAddresses)
            {
                if (string.IsNullOrWhiteSpace(mint))
                {
                    OnError?.Invoke("Mint address is null or empty.");
                    continue;
                }

                var normalized = mint.Trim();
                if (!seen.Add(normalized))
                    continue;

                try
                {
                    mintKeys.Add(new PublicKey(normalized));
                    uniqueMints.Add(normalized);
                }
                catch (Exception ex)
                {
                    OnError?.Invoke($"Invalid mint public key: {ex.Message}");
                    results[normalized] = new MetadataAccountResult(false, null, ex.Message);
                }
            }

            if (mintKeys.Count == 0)
                return results;

            IReadOnlyList<MetadataAccount> metadataAccounts;

            try
            {
                metadataAccounts = await _accountLoader.LoadManyAsync(mintKeys, commitment).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Failed to retrieve metadata accounts: {ex.Message}");
                for (int i = 0; i < uniqueMints.Count; i++)
                {
                    var mint = uniqueMints[i];

                    MetadataQueryLogger.LogWarning(() =>
                        $"[MetadataQueryService] LoadManyAsync threw while requesting mint {mint} at index {i}: {ex.Message}");

                    if (!results.ContainsKey(mint))
                        results[mint] = new MetadataAccountResult(false, null, ex.Message);
                }

                return results;
            }

            for (int i = 0; i < uniqueMints.Count; i++)
            {
                var mint = uniqueMints[i];

                if (results.ContainsKey(mint))
                    continue;

                MetadataAccount account = null;
                if (metadataAccounts != null && i < metadataAccounts.Count)
                {
                    account = metadataAccounts[i];
                }

                if (account?.metadata == null)
                {
                    MetadataQueryLogger.LogWarning(() =>
                        $"[MetadataQueryService] Metadata account missing for mint {mint} at index {i} (account {(account == null ? "null" : "metadata null")}).");
                    results[mint] = new MetadataAccountResult(false, null, $"Metadata account not found for mint {mint}.");
                }
                else
                {
                    results[mint] = new MetadataAccountResult(true, account, null);
                }
            }

            return results;
        }

        public async Task<MetadataResult> FetchNftMetadataAsync(string mintAddress, Commitment commitment = Commitment.Processed)
        {
            if (string.IsNullOrWhiteSpace(mintAddress))
                return Fail("Mint address is null or empty.");

            PublicKey mintPubKey;
            try
            {
                mintPubKey = new PublicKey(mintAddress);
            }
            catch (Exception ex)
            {
                return Fail($"Invalid mint public key: {ex.Message}");
            }

            var account = await _accountLoader.LoadAsync(mintPubKey, commitment);
            if (account?.metadata == null)
                return Fail($"Failed to retrieve or parse metadata for mint {mintAddress}");

            string uri = account.metadata.uri?.TrimEnd('\0');
            if (!TryValidateUri(uri, out string uriError))
                return Fail(uriError);

            string json;
            try
            {
                json = await _jsonFetcher.FetchAsync(uri);
            }
            catch (Exception ex)
            {
                return Fail($"HTTP error fetching off-chain JSON: {ex.Message}");
            }

            NftMetadata parsed;
            try
            {
                parsed = JsonConvert.DeserializeObject<NftMetadata>(json);
            }
            catch (Exception ex)
            {
                return Fail($"Failed to parse JSON from URI: {ex.Message}");
            }

            if (parsed == null)
                return Fail("Failed to parse JSON from URI: Deserialization returned null.");

            var validation = _validator.Validate(json, parsed);
            OnValidationCompleted?.Invoke(validation);

            if (!validation.IsValid)
            {
                var validationMessage = validation.ErrorMessage ?? "Metadata validation failed.";
                OnError?.Invoke(validationMessage);

                return new MetadataResult
                {
                    success = false,
                    errorMessage = validationMessage,
                    onChainUri = uri,
                    parsedData = parsed,
                    validation = validation
                };
            }

            return new MetadataResult
            {
                success = true,
                parsedData = parsed,
                onChainUri = uri,
                validation = validation
            };
        }

        public async Task<(bool success, string uriOrError)> GetOnChainUriAsync(string mintAddress, Commitment commitment = Commitment.Processed)
        {
            var result = await FetchNftMetadataAsync(mintAddress, commitment);
            return result.success ? (true, result.onChainUri) : (false, result.errorMessage);
        }

        private MetadataResult Fail(string message)
        {
            OnError?.Invoke(message);
            return new MetadataResult
            {
                success = false,
                errorMessage = message
            };
        }

        private static bool TryValidateUri(string uri, out string errorMessage)
        {
            errorMessage = null;

            if (string.IsNullOrWhiteSpace(uri))
            {
                errorMessage = "On-chain metadata URI is empty or invalid.";
                return false;
            }

            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Scheme) || string.IsNullOrEmpty(parsed.Host))
            {
                errorMessage = "On-chain metadata URI is malformed.";
                return false;
            }

            return true;
        }
    }
}

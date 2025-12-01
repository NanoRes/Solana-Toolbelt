using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Solana.Unity.Programs;
using Solana.Unity.Rpc;
using Solana.Unity.Rpc.Core.Http;
using Solana.Unity.Rpc.Models;
using Solana.Unity.Rpc.Types;
using Solana.Unity.Wallet;
namespace Solana.Unity.Toolbelt
{
    public class RpcNftInventoryAdapter : INftInventoryService
    {
        private static readonly TimeSpan DefaultCacheTimeToLive = TimeSpan.FromSeconds(15);

        private readonly Func<IRpcClient> _rpcClientResolver;
        private readonly object _cacheLock = new();
        private readonly Func<DateTimeOffset> _clock;
        private readonly Action<string> _log;
        private readonly IRpcEndpointManager rpcEndpointManager;
        private TimeSpan _tokenAccountCacheTimeToLive;
        private TimeSpan _mintDetailsCacheTimeToLive;
        private readonly Dictionary<TokenAccountCacheKey, TokenAccountCacheEntry> _tokenAccountCache = new();
        private readonly Dictionary<MintDetailsCacheKey, MintDetailsCacheEntry> _mintDetailsCache = new();

        public RpcNftInventoryAdapter(
            Func<IRpcClient> rpcClientResolver,
            TimeSpan? tokenAccountCacheTimeToLive = null,
            TimeSpan? mintDetailsCacheTimeToLive = null,
            Action<string> log = null,
            Func<DateTimeOffset> clock = null,
            IRpcEndpointManager rpcEndpointManager = null)
        {
            _rpcClientResolver = rpcClientResolver;
            _log = log;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _tokenAccountCacheTimeToLive = NormalizeTtl(tokenAccountCacheTimeToLive ?? DefaultCacheTimeToLive);
            _mintDetailsCacheTimeToLive = NormalizeTtl(mintDetailsCacheTimeToLive ?? DefaultCacheTimeToLive);
            this.rpcEndpointManager = rpcEndpointManager;
        }

        protected RpcNftInventoryAdapter()
            : this(null)
        {
        }

        public async Task<IReadOnlyList<TokenAccountSnapshot>> GetTokenAccountsByOwnerAsync(
            string ownerPublicKey,
            string collectionMint = null,
            RpcCommitment commitment = RpcCommitment.Confirmed)
        {
            if (string.IsNullOrWhiteSpace(ownerPublicKey))
            {
                return Array.Empty<TokenAccountSnapshot>();
            }

            var key = new TokenAccountCacheKey(ownerPublicKey, collectionMint, commitment);

            if (TryGetCachedResult(key, out var cached))
            {
                return cached;
            }

            LogCacheMiss(key);

            var fetchResult = await FetchTokenAccountsByOwnerAsync(ownerPublicKey, collectionMint, commitment)
                .ConfigureAwait(false);

            if (!fetchResult.IsSuccessful)
            {
                return null;
            }

            var snapshots = fetchResult.Snapshots ?? Array.Empty<TokenAccountSnapshot>();
            var materialized = snapshots as TokenAccountSnapshot[] ?? snapshots.ToArray();

            CacheResult(key, materialized);

            return materialized;
        }

        public async Task<TokenMintDetails> GetMintDetailsAsync(
            string tokenMint,
            RpcCommitment commitment = RpcCommitment.Confirmed)
        {
            if (string.IsNullOrWhiteSpace(tokenMint))
            {
                return new TokenMintDetails(false, null, null, "Token mint is required");
            }

            var key = new MintDetailsCacheKey(tokenMint, commitment);

            if (TryGetCachedMintDetails(key, out var cachedDetails))
            {
                return cachedDetails;
            }

            var details = await FetchMintDetailsAsync(tokenMint, commitment).ConfigureAwait(false);

            CacheMintDetails(key, details);

            return details;
        }

        public void InvalidateTokenAccountsCache(
            string ownerPublicKey = null,
            string collectionMint = null,
            RpcCommitment? commitment = null)
        {
            List<TokenAccountCacheKey> keysToRemove = null;
            HashSet<MintDetailsCacheKey> mintKeysToRemove = null;

            lock (_cacheLock)
            {
                if (_tokenAccountCache.Count == 0)
                {
                    if (string.IsNullOrWhiteSpace(ownerPublicKey))
                    {
                        if (_mintDetailsCache.Count > 0)
                        {
                            _mintDetailsCache.Clear();
                        }
                    }

                    return;
                }

                if (string.IsNullOrWhiteSpace(ownerPublicKey))
                {
                    _tokenAccountCache.Clear();
                    _mintDetailsCache.Clear();
                    Log("Cleared all cached token account snapshots on request.");
                    return;
                }

                string ownerKey = ownerPublicKey.Trim();
                string mintKey = string.IsNullOrWhiteSpace(collectionMint) ? null : collectionMint.Trim();

                keysToRemove = new List<TokenAccountCacheKey>();
                foreach (var pair in _tokenAccountCache.Keys)
                {
                    if (pair.Matches(ownerKey, mintKey, commitment))
                    {
                        keysToRemove.Add(pair);
                    }
                }

                if (keysToRemove.Count == 0)
                {
                    return;
                }

                foreach (var keyToRemove in keysToRemove)
                {
                    if (_tokenAccountCache.TryGetValue(keyToRemove, out var entry) &&
                        entry?.Snapshot != null)
                    {
                        foreach (var snapshot in entry.Snapshot)
                        {
                            if (string.IsNullOrWhiteSpace(snapshot?.Mint))
                            {
                                continue;
                            }

                            mintKeysToRemove ??= new HashSet<MintDetailsCacheKey>();
                            mintKeysToRemove.Add(new MintDetailsCacheKey(snapshot.Mint, keyToRemove.Commitment));
                        }
                    }

                    _tokenAccountCache.Remove(keyToRemove);
                }

                if (mintKeysToRemove != null && _mintDetailsCache.Count > 0)
                {
                    foreach (var mintKeyToRemove in mintKeysToRemove)
                    {
                        _mintDetailsCache.Remove(mintKeyToRemove);
                    }
                }
            }

            if (keysToRemove == null)
            {
                return;
            }

            Log($"Invalidated {keysToRemove.Count} cached token account snapshot(s) for owner '{ownerPublicKey}'.");
        }

        public void SetTokenAccountCacheTimeToLive(TimeSpan ttl)
        {
            ttl = NormalizeTtl(ttl);

            lock (_cacheLock)
            {
                _tokenAccountCacheTimeToLive = ttl;

                if (ttl == TimeSpan.Zero)
                {
                    _tokenAccountCache.Clear();
                }
            }

            Log(ttl == TimeSpan.Zero
                ? "Token account cache disabled; all cached entries cleared."
                : $"Token account cache TTL updated to {ttl.TotalSeconds:F1} seconds.");
        }

        public void SetMintDetailsCacheTimeToLive(TimeSpan ttl)
        {
            ttl = NormalizeTtl(ttl);

            lock (_cacheLock)
            {
                _mintDetailsCacheTimeToLive = ttl;

                if (ttl == TimeSpan.Zero)
                {
                    _mintDetailsCache.Clear();
                }
            }

            Log(ttl == TimeSpan.Zero
                ? "Mint details cache disabled; all cached entries cleared."
                : $"Mint details cache TTL updated to {ttl.TotalSeconds:F1} seconds.");
        }

        protected virtual async Task<TokenAccountFetchResult> FetchTokenAccountsByOwnerAsync(
            string ownerPublicKey,
            string collectionMint,
            RpcCommitment commitment)
        {
            var commitmentArg = ToCommitment(commitment);
            var ownerKey = new PublicKey(ownerPublicKey);
            PublicKey mintKey = string.IsNullOrWhiteSpace(collectionMint)
                ? null
                : new PublicKey(collectionMint);
            bool hasCollectionMint = mintKey != null;
            var programKey = TokenProgram.ProgramIdKey;

            string lastError = null;

            if (rpcEndpointManager != null)
            {
                var managedResult = await rpcEndpointManager.ExecuteAsync(client =>
                        hasCollectionMint
                            ? client.GetTokenAccountsByOwnerAsync(ownerKey, mintKey, programKey, commitmentArg)
                            : client.GetTokenAccountsByOwnerAsync(ownerKey, programKey, commitment: commitmentArg))
                    .ConfigureAwait(false);

                if (TryConvert(managedResult, out var managedSnapshots, out var managedError))
                {
                    return TokenAccountFetchResult.Success(managedSnapshots);
                }

                lastError = managedError;
            }

            var rpcClient = _rpcClientResolver?.Invoke();
            if (rpcClient != null)
            {
                RequestResult<Solana.Unity.Rpc.Messages.ResponseValue<List<TokenAccount>>> fallbackResult = null;

                try
                {
                    fallbackResult = hasCollectionMint
                        ? await rpcClient
                            .GetTokenAccountsByOwnerAsync(ownerKey, mintKey, programKey, commitmentArg)
                            .ConfigureAwait(false)
                        : await rpcClient
                            .GetTokenAccountsByOwnerAsync(ownerKey, programKey, commitment: commitmentArg)
                            .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }

                if (fallbackResult != null)
                {
                    if (TryConvert(fallbackResult, out var fallbackSnapshots, out var fallbackError))
                    {
                        return TokenAccountFetchResult.Success(fallbackSnapshots);
                    }

                    lastError = fallbackError;
                }
            }
            else if (lastError == null)
            {
                lastError = "RPC client unavailable.";
            }

            string failureReason = string.IsNullOrWhiteSpace(lastError)
                ? "RPC request reported failure."
                : lastError;

            LogFetchFailure(ownerPublicKey, collectionMint, commitment, failureReason);
            return TokenAccountFetchResult.Failure(failureReason);

            static TokenAccountSnapshot[] Convert(IReadOnlyList<TokenAccount> accounts)
            {
                var list = new List<TokenAccountSnapshot>(accounts.Count);
                foreach (var account in accounts)
                {
                    string address = account?.PublicKey ?? string.Empty;
                    var info = account?.Account?.Data?.Parsed?.Info;
                    string mint = info?.Mint ?? string.Empty;
                    ulong rawAmount = info?.TokenAmount?.AmountUlong ?? 0UL;

                    list.Add(new TokenAccountSnapshot(address, mint, rawAmount));
                }

                return list.ToArray();
            }

            static bool TryConvert(
                RequestResult<Solana.Unity.Rpc.Messages.ResponseValue<List<TokenAccount>>> result,
                out TokenAccountSnapshot[] snapshots,
                out string error)
            {
                snapshots = null;
                error = null;

                if (result == null)
                {
                    error = "RPC request returned no response.";
                    return false;
                }

                if (!result.WasSuccessful)
                {
                    error = string.IsNullOrWhiteSpace(result.Reason)
                        ? "RPC request reported failure."
                        : result.Reason;
                    return false;
                }

                var value = result.Result?.Value;
                if (value == null)
                {
                    error = "RPC request returned a null value.";
                    return false;
                }

                snapshots = Convert(value);
                return true;
            }
        }

        private bool TryGetCachedResult(TokenAccountCacheKey key, out IReadOnlyList<TokenAccountSnapshot> snapshots)
        {
            if (_tokenAccountCacheTimeToLive == TimeSpan.Zero)
            {
                snapshots = null;
                return false;
            }

            TokenAccountCacheEntry cachedEntry = null;
            DateTimeOffset now = default;
            bool expired = false;

            lock (_cacheLock)
            {
                if (!_tokenAccountCache.TryGetValue(key, out cachedEntry))
                {
                    snapshots = null;
                    return false;
                }

                now = _clock();

                if (now < cachedEntry.Expiration)
                {
                    snapshots = cachedEntry.Snapshot;
                }
                else
                {
                    expired = true;
                    _tokenAccountCache.Remove(key);
                    snapshots = null;
                }
            }

            if (expired)
            {
                LogCacheExpired(key, cachedEntry);
                return false;
            }

            LogCacheHit(key, cachedEntry, now);
            return true;
        }

        private void CacheResult(TokenAccountCacheKey key, IReadOnlyList<TokenAccountSnapshot> snapshots)
        {
            if (_tokenAccountCacheTimeToLive == TimeSpan.Zero)
            {
                return;
            }

            var now = _clock();
            var expiration = now + _tokenAccountCacheTimeToLive;
            var buffer = snapshots as TokenAccountSnapshot[] ?? snapshots.ToArray();
            var entry = new TokenAccountCacheEntry(buffer, now, expiration);

            lock (_cacheLock)
            {
                _tokenAccountCache[key] = entry;
            }

            LogCacheStored(key, entry);
        }

        private bool TryGetCachedMintDetails(MintDetailsCacheKey key, out TokenMintDetails details)
        {
            if (_mintDetailsCacheTimeToLive == TimeSpan.Zero)
            {
                details = null;
                return false;
            }

            MintDetailsCacheEntry cachedEntry = null;
            DateTimeOffset now = default;
            bool expired = false;

            lock (_cacheLock)
            {
                if (!_mintDetailsCache.TryGetValue(key, out cachedEntry))
                {
                    details = null;
                    return false;
                }

                now = _clock();

                if (now < cachedEntry.Expiration)
                {
                    details = cachedEntry.Details;
                }
                else
                {
                    expired = true;
                    _mintDetailsCache.Remove(key);
                    details = null;
                }
            }

            if (expired)
            {
                Log($"Mint details cache entry expired for mint '{key.Mint}' (commitment: {key.Commitment}).");
                return false;
            }

            Log($"Mint details cache hit for mint '{key.Mint}' (commitment: {key.Commitment}).");
            return true;
        }

        private void CacheMintDetails(MintDetailsCacheKey key, TokenMintDetails details)
        {
            if (_mintDetailsCacheTimeToLive == TimeSpan.Zero || details == null)
            {
                return;
            }

            var now = _clock();
            var entry = new MintDetailsCacheEntry(details, now, now + _mintDetailsCacheTimeToLive);

            lock (_cacheLock)
            {
                _mintDetailsCache[key] = entry;
            }

            Log($"Cached mint details for '{key.Mint}' (commitment: {key.Commitment}) with TTL {_mintDetailsCacheTimeToLive.TotalSeconds:F1}s.");
        }

        protected virtual async Task<TokenMintDetails> FetchMintDetailsAsync(string tokenMint, RpcCommitment commitment)
        {
            if (rpcEndpointManager != null)
            {
                var result = await rpcEndpointManager.ExecuteAsync(client =>
                        client.GetTokenMintInfoAsync(tokenMint, ToCommitment(commitment)))
                    .ConfigureAwait(false);

                if (result == null)
                {
                    return new TokenMintDetails(false, null, null, "No response");
                }

                if (!result.WasSuccessful || result.Result?.Value == null)
                {
                    return new TokenMintDetails(false, null, null, result.Reason);
                }

                var info = result.Result.Value.Data?.Parsed?.Info;
                int? decimals = info?.Decimals;
                string mintAuthority = info?.MintAuthority;

                return new TokenMintDetails(true, decimals, mintAuthority, null);
            }

            var rpcClient = _rpcClientResolver?.Invoke();
            if (rpcClient == null)
            {
                return new TokenMintDetails(false, null, null, "RPC service unavailable");
            }

            var fallbackResult = await rpcClient
                .GetTokenMintInfoAsync(tokenMint, ToCommitment(commitment))
                .ConfigureAwait(false);

            if (fallbackResult == null)
            {
                return new TokenMintDetails(false, null, null, "No response");
            }

            if (!fallbackResult.WasSuccessful || fallbackResult.Result?.Value == null)
            {
                return new TokenMintDetails(false, null, null, fallbackResult.Reason);
            }

            var fallbackInfo = fallbackResult.Result.Value.Data?.Parsed?.Info;
            int? fallbackDecimals = fallbackInfo?.Decimals;
            string fallbackMintAuthority = fallbackInfo?.MintAuthority;

            return new TokenMintDetails(true, fallbackDecimals, fallbackMintAuthority, null);
        }

        private static Commitment ToCommitment(RpcCommitment commitment)
        {
            return commitment switch
            {
                RpcCommitment.Processed => Commitment.Processed,
                RpcCommitment.Confirmed => Commitment.Confirmed,
                _ => Commitment.Finalized
            };
        }

        private void Log(string message)
        {
            _log?.Invoke(message);
        }

        private void LogCacheMiss(TokenAccountCacheKey key)
        {
            Log($"Cache miss for owner '{key.Owner}' (mint: {key.Mint ?? "<any>"}, commitment: {key.Commitment}). Fetching from RPC.");
        }

        private void LogCacheHit(TokenAccountCacheKey key, TokenAccountCacheEntry entry, DateTimeOffset now)
        {
            if (entry == null)
            {
                return;
            }

            var age = now - entry.CachedAt;
            var remaining = entry.Expiration - now;
            Log($"Cache hit for owner '{key.Owner}' (mint: {key.Mint ?? "<any>"}, commitment: {key.Commitment}). Age: {age.TotalSeconds:F1}s, remaining TTL: {Math.Max(remaining.TotalSeconds, 0):F1}s.");
        }

        private void LogCacheExpired(TokenAccountCacheKey key, TokenAccountCacheEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            var ttl = entry.Expiration - entry.CachedAt;
            Log($"Cache entry expired for owner '{key.Owner}' (mint: {key.Mint ?? "<any>"}, commitment: {key.Commitment}). TTL was {Math.Max(ttl.TotalSeconds, 0):F1}s.");
        }

        private void LogCacheStored(TokenAccountCacheKey key, TokenAccountCacheEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            var ttl = entry.Expiration - entry.CachedAt;
            int count = entry.Snapshot?.Length ?? 0;
            Log($"Cached {count} token account snapshot(s) for owner '{key.Owner}' (mint: {key.Mint ?? "<any>"}, commitment: {key.Commitment}) with TTL {Math.Max(ttl.TotalSeconds, 0):F1}s.");
        }

        private void LogFetchFailure(string ownerPublicKey, string collectionMint, RpcCommitment commitment, string reason)
        {
            var ownerDisplay = string.IsNullOrWhiteSpace(ownerPublicKey) ? "<unknown>" : ownerPublicKey.Trim();
            var mintDisplay = string.IsNullOrWhiteSpace(collectionMint) ? "<any>" : collectionMint.Trim();
            var reasonDisplay = string.IsNullOrWhiteSpace(reason) ? "No additional details." : reason.Trim();

            Log($"Token account fetch failed for owner '{ownerDisplay}' (mint: {mintDisplay}, commitment: {commitment}). Reason: {reasonDisplay}");
        }

        private static TimeSpan NormalizeTtl(TimeSpan ttl)
        {
            return ttl < TimeSpan.Zero ? TimeSpan.Zero : ttl;
        }

        protected readonly struct TokenAccountFetchResult
        {
            private TokenAccountFetchResult(bool isSuccessful, IReadOnlyList<TokenAccountSnapshot> snapshots, string error)
            {
                IsSuccessful = isSuccessful;
                Snapshots = snapshots;
                Error = string.IsNullOrWhiteSpace(error) ? null : error.Trim();
            }

            public bool IsSuccessful { get; }

            public IReadOnlyList<TokenAccountSnapshot> Snapshots { get; }

            public string Error { get; }

            public static TokenAccountFetchResult Success(IReadOnlyList<TokenAccountSnapshot> snapshots)
            {
                return new TokenAccountFetchResult(true, snapshots ?? Array.Empty<TokenAccountSnapshot>(), null);
            }

            public static TokenAccountFetchResult Failure(string error)
            {
                return new TokenAccountFetchResult(false, null, error);
            }
        }

        private readonly struct TokenAccountCacheKey : IEquatable<TokenAccountCacheKey>
        {
            public TokenAccountCacheKey(string ownerPublicKey, string collectionMint, RpcCommitment commitment)
            {
                Owner = string.IsNullOrWhiteSpace(ownerPublicKey) ? string.Empty : ownerPublicKey.Trim();
                Mint = string.IsNullOrWhiteSpace(collectionMint) ? null : collectionMint.Trim();
                Commitment = commitment;
            }

            public string Owner { get; }
            public string Mint { get; }
            public RpcCommitment Commitment { get; }

            public bool Equals(TokenAccountCacheKey other)
            {
                return StringComparer.Ordinal.Equals(Owner, other.Owner) &&
                       StringComparer.Ordinal.Equals(Mint ?? string.Empty, other.Mint ?? string.Empty) &&
                       Commitment == other.Commitment;
            }

            public override bool Equals(object obj)
            {
                return obj is TokenAccountCacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = StringComparer.Ordinal.GetHashCode(Owner ?? string.Empty);
                    hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(Mint ?? string.Empty);
                    hash = (hash * 397) ^ (int)Commitment;
                    return hash;
                }
            }

            public bool Matches(string owner, string mint, RpcCommitment? commitment)
            {
                if (!StringComparer.Ordinal.Equals(Owner, owner))
                {
                    return false;
                }

                if (mint != null && !StringComparer.Ordinal.Equals(Mint ?? string.Empty, mint))
                {
                    return false;
                }

                if (commitment.HasValue && Commitment != commitment.Value)
                {
                    return false;
                }

                return true;
            }
        }

        private sealed class TokenAccountCacheEntry
        {
            public TokenAccountCacheEntry(TokenAccountSnapshot[] snapshot, DateTimeOffset cachedAt, DateTimeOffset expiration)
            {
                Snapshot = snapshot;
                CachedAt = cachedAt;
                Expiration = expiration;
            }

            public TokenAccountSnapshot[] Snapshot { get; }
            public DateTimeOffset CachedAt { get; }
            public DateTimeOffset Expiration { get; }
        }

        private readonly struct MintDetailsCacheKey : IEquatable<MintDetailsCacheKey>
        {
            public MintDetailsCacheKey(string mint, RpcCommitment commitment)
            {
                Mint = string.IsNullOrWhiteSpace(mint) ? string.Empty : mint.Trim();
                Commitment = commitment;
            }

            public string Mint { get; }
            public RpcCommitment Commitment { get; }

            public bool Equals(MintDetailsCacheKey other)
            {
                return StringComparer.Ordinal.Equals(Mint, other.Mint) && Commitment == other.Commitment;
            }

            public override bool Equals(object obj)
            {
                return obj is MintDetailsCacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = StringComparer.Ordinal.GetHashCode(Mint ?? string.Empty);
                    hash = (hash * 397) ^ (int)Commitment;
                    return hash;
                }
            }
        }

        private sealed class MintDetailsCacheEntry
        {
            public MintDetailsCacheEntry(TokenMintDetails details, DateTimeOffset cachedAt, DateTimeOffset expiration)
            {
                Details = details;
                CachedAt = cachedAt;
                Expiration = expiration;
            }

            public TokenMintDetails Details { get; }
            public DateTimeOffset CachedAt { get; }
            public DateTimeOffset Expiration { get; }
        }
    }
}

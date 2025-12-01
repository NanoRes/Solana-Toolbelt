using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Solana.Unity.Rpc;
using Solana.Unity.Rpc.Core.Http;
using UnityEngine;

namespace Solana.Unity.Toolbelt
{
    public interface IRpcEndpointManager
    {
        Task<RequestResult<TResponse>> ExecuteAsync<TResponse>(
            Func<IRpcClient, Task<RequestResult<TResponse>>> operation,
            CancellationToken cancellationToken = default);

        IRpcClient TryGetCurrentClient();
    }

    public sealed class RpcEndpointManager : IRpcEndpointManager
    {
        private const string DefaultMainnetEndpoint = "https://api.mainnet-beta.solana.com";

        private readonly SolanaConfiguration configuration;
        private readonly object syncRoot = new();
        private readonly Dictionary<string, IRpcClient> rpcClients = new(StringComparer.OrdinalIgnoreCase);

        private string lastSuccessfulEndpoint;

        public RpcEndpointManager(SolanaConfiguration configuration)
        {
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public async Task<RequestResult<TResponse>> ExecuteAsync<TResponse>(
            Func<IRpcClient, Task<RequestResult<TResponse>>> operation,
            CancellationToken cancellationToken = default)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            var endpoints = BuildEndpointList();
            RequestResult<TResponse> lastResult = null;

            foreach (string endpoint in endpoints)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var client = GetOrCreateClient(endpoint);

                if (client == null)
                {
                    Debug.LogWarning($"[RpcEndpointManager] Failed to create RPC client for '{endpoint}'. Skipping endpoint.");
                    lastResult = new RequestResult<TResponse>
                    {
                        Reason = $"Failed to create RPC client for '{endpoint}'."
                    };
                    continue;
                }

                try
                {
                    lastResult = await operation(client).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[RpcEndpointManager] RPC call threw an exception on '{endpoint}': {ex.Message}");
                    lastResult = new RequestResult<TResponse>
                    {
                        Reason = ex.Message
                    };
                }

                if (lastResult == null)
                {
                    continue;
                }

                if (lastResult.WasSuccessful)
                {
                    RecordSuccess(endpoint);
                    return lastResult;
                }

                if (!ShouldRetry(lastResult.Reason))
                {
                    Debug.LogWarning($"[RpcEndpointManager] RPC request failed on '{endpoint}': {lastResult.Reason}");
                    return lastResult;
                }

                Debug.LogWarning($"[RpcEndpointManager] RPC request failed on '{endpoint}' with retryable error: {lastResult.Reason}");
            }

            return lastResult;
        }

        public IRpcClient TryGetCurrentClient()
        {
            lock (syncRoot)
            {
                if (!string.IsNullOrWhiteSpace(lastSuccessfulEndpoint) &&
                    rpcClients.TryGetValue(lastSuccessfulEndpoint, out var client) && client != null)
                {
                    return client;
                }

                var endpoint = BuildEndpointList().FirstOrDefault();
                return endpoint != null ? GetOrCreateClient(endpoint) : null;
            }
        }

        private IEnumerable<string> BuildEndpointList()
        {
            var ordered = configuration?.GetOrderedRpcUrls() ?? new List<string>();
            var unique = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(lastSuccessfulEndpoint) && seen.Add(lastSuccessfulEndpoint))
            {
                unique.Add(lastSuccessfulEndpoint);
            }

            foreach (var endpoint in ordered)
            {
                string trimmed = endpoint?.Trim();
                if (string.IsNullOrEmpty(trimmed) || !seen.Add(trimmed))
                {
                    continue;
                }

                unique.Add(trimmed);
            }

            if (seen.Add(DefaultMainnetEndpoint))
            {
                unique.Add(DefaultMainnetEndpoint);
            }

            if (unique.Count == 0)
            {
                unique.Add(DefaultMainnetEndpoint);
            }

            return unique;
        }

        private IRpcClient GetOrCreateClient(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                endpoint = DefaultMainnetEndpoint;
            }

            lock (syncRoot)
            {
                if (!rpcClients.TryGetValue(endpoint, out var client) || client == null)
                {
                    client = ClientFactory.GetClient(endpoint);
                    if (client == null)
                    {
                        Debug.LogWarning($"[RpcEndpointManager] ClientFactory returned null for endpoint '{endpoint}'.");
                        return null;
                    }

                    rpcClients[endpoint] = client;
                }

                return client;
            }
        }

        private void RecordSuccess(string endpoint)
        {
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                lock (syncRoot)
                {
                    lastSuccessfulEndpoint = endpoint;
                }
            }
        }

        private static bool ShouldRetry(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return false;
            }

            string text = reason.ToLowerInvariant();
            if (text.Contains("unauthorized") || text.Contains("forbidden") || text.Contains("invalid api key") ||
                text.Contains("access denied"))
            {
                return true;
            }

            if (text.Contains("429") || text.Contains("too many requests") || text.Contains("temporarily unavailable") ||
                text.Contains("timed out") || text.Contains("timeout") || text.Contains("503") || text.Contains("504") ||
                text.Contains("bad gateway"))
            {
                return true;
            }

            return false;
        }
    }
}

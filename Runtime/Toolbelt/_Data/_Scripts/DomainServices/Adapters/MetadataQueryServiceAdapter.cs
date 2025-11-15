using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Solana.Unity.Rpc.Types;
namespace Solana.Unity.Toolbelt
{
    public sealed class MetadataQueryServiceAdapter : INftMetadataService
    {
        private readonly MetadataQueryService _service;

        public MetadataQueryServiceAdapter(MetadataQueryService service)
        {
            _service = service;
        }

        public bool VerboseLoggingEnabled
        {
            get => MetadataQueryLogger.VerboseLoggingEnabled;
            set => MetadataQueryLogger.VerboseLoggingEnabled = value;
        }

        public async Task<NftUriResult> GetOnChainUriAsync(string mintAddress, RpcCommitment commitment = RpcCommitment.Processed)
        {
            if (_service == null || string.IsNullOrWhiteSpace(mintAddress))
            {
                return new NftUriResult(false, null, "Metadata service unavailable");
            }

            var result = await _service.GetOnChainUriAsync(mintAddress, ToCommitment(commitment));
            return result.success
                ? new NftUriResult(true, result.uriOrError, null)
                : new NftUriResult(false, null, result.uriOrError);
        }

        public async Task<string> FetchMetadataJsonAsync(string uri)
        {
            if (_service == null)
            {
                throw new InvalidOperationException("Metadata service unavailable.");
            }

            var fetcher = _service.JsonFetcher ?? new UnityWebRequestMetadataJsonFetcher();
            return await fetcher.FetchAsync(uri);
        }

        public async Task<MetadataAccountResult> GetMetadataAccountAsync(string mintAddress, RpcCommitment commitment = RpcCommitment.Processed)
        {
            if (_service == null)
            {
                return new MetadataAccountResult(false, null, "Metadata service unavailable");
            }

            if (string.IsNullOrWhiteSpace(mintAddress))
            {
                return new MetadataAccountResult(false, null, "Mint address is required.");
            }

            try
            {
                var account = await _service.GetMetadataAccountAsync(mintAddress, ToCommitment(commitment));
                if (account == null)
                {
                    return new MetadataAccountResult(false, null, $"Metadata account not found for mint {mintAddress}.");
                }

                return new MetadataAccountResult(true, account, null);
            }
            catch (Exception ex)
            {
                return new MetadataAccountResult(false, null, ex.Message);
            }
        }

        public async Task<IReadOnlyDictionary<string, MetadataAccountResult>> GetMetadataAccountsAsync(
            IEnumerable<string> mintAddresses,
            RpcCommitment commitment = RpcCommitment.Processed)
        {
            var results = new Dictionary<string, MetadataAccountResult>(StringComparer.Ordinal);

            if (_service == null)
            {
                return results;
            }

            if (mintAddresses == null)
            {
                return results;
            }

            var normalizedMints = mintAddresses
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (normalizedMints.Count == 0)
            {
                return results;
            }

            try
            {
                var serviceResults = await _service.GetMetadataAccountsAsync(normalizedMints, ToCommitment(commitment))
                    .ConfigureAwait(false);

                foreach (var mint in normalizedMints)
                {
                    if (serviceResults != null && serviceResults.TryGetValue(mint, out var account))
                    {
                        if (account == null)
                        {
                            MetadataQueryLogger.LogWarning(() =>
                                $"[MetadataQueryServiceAdapter] Service returned a null result for mint {mint}.");
                            results[mint] = new MetadataAccountResult(false, null, $"Metadata account not found for mint {mint}.");
                        }
                        else if (account.Success)
                        {
                            results[mint] = new MetadataAccountResult(true, account.MetadataAccount, null);
                        }
                        else
                        {
                            MetadataQueryLogger.LogWarning(() =>
                                $"[MetadataQueryServiceAdapter] Service reported failure for mint {mint}: {account.Error ?? "Unknown error"}.");
                            results[mint] = new MetadataAccountResult(false, null, account.Error);
                        }
                    }
                    else
                    {
                        MetadataQueryLogger.LogWarning(() =>
                            serviceResults == null
                                ? $"[MetadataQueryServiceAdapter] Service returned null results while requesting mint {mint}."
                                : $"[MetadataQueryServiceAdapter] Service results did not include mint {mint}.");

                        results[mint] = new MetadataAccountResult(false, null, $"Metadata account not found for mint {mint}.");
                    }
                }
            }
            catch (Exception ex)
            {
                foreach (var mint in normalizedMints)
                {
                    results[mint] = new MetadataAccountResult(false, null, ex.Message);
                }
            }

            return results;
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
    }
}

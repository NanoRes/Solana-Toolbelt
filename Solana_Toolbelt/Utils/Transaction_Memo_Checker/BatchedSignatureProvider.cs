using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Solana.Unity.Rpc.Core.Http;
using Solana.Unity.Rpc.Models;
using Solana.Unity.Rpc.Types;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Responsible for retrieving transactions for a given address while
    /// respecting a concurrency limit on transaction lookups.
    /// </summary>
    public class BatchedSignatureProvider
    {
        private readonly Func<string, ulong, Commitment, Task<RequestResult<List<SignatureStatusInfo>>>> _getSignatures;
        private readonly Func<string, Commitment, Task<RequestResult<TransactionMetaSlotInfo>>> _getTransaction;
        private readonly int _maxConcurrentTransactions;

        public BatchedSignatureProvider(
            Func<string, ulong, Commitment, Task<RequestResult<List<SignatureStatusInfo>>>> getSignatures,
            Func<string, Commitment, Task<RequestResult<TransactionMetaSlotInfo>>> getTransaction,
            int maxConcurrentTransactions)
        {
            _getSignatures = getSignatures ?? throw new ArgumentNullException(nameof(getSignatures));
            _getTransaction = getTransaction ?? throw new ArgumentNullException(nameof(getTransaction));
            if (maxConcurrentTransactions <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxConcurrentTransactions));

            _maxConcurrentTransactions = maxConcurrentTransactions;
        }

        public async Task<IReadOnlyList<TransactionMetaSlotInfo>> FetchTransactionsAsync(
            string address,
            int maxSignatures,
            Commitment commitment)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("Address cannot be null or whitespace", nameof(address));
            if (maxSignatures <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxSignatures));

            var signatureResult = await _getSignatures(address, (ulong)maxSignatures, commitment).ConfigureAwait(false);
            if (!signatureResult.WasSuccessful() || signatureResult.Result == null)
                return Array.Empty<TransactionMetaSlotInfo>();

            var semaphore = new SemaphoreSlim(_maxConcurrentTransactions);
            try
            {
                var tasks = signatureResult.Result
                    .Where(s => !string.IsNullOrEmpty(s.Signature))
                    .Select(signature => FetchSingleAsync(signature.Signature, commitment, semaphore))
                    .ToArray();

                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                return results
                    .Where(r => r != null)
                    .ToArray();
            }
            finally
            {
                semaphore.Dispose();
            }
        }

        private async Task<TransactionMetaSlotInfo> FetchSingleAsync(
            string signature,
            Commitment commitment,
            SemaphoreSlim semaphore)
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var txResult = await _getTransaction(signature, commitment).ConfigureAwait(false);
                if (txResult.WasSuccessful() && txResult.Result != null)
                    return txResult.Result;
            }
            finally
            {
                semaphore.Release();
            }

            return null;
        }
    }

    internal static class RequestResultExtensions
    {
        public static bool WasSuccessful<T>(this RequestResult<T> result)
        {
            return result != null && result.WasHttpRequestSuccessful && result.WasRequestSuccessfullyHandled;
        }
    }
}

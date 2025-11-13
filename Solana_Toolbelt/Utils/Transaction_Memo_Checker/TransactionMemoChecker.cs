using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Solana.Unity.Programs;
using Solana.Unity.Rpc;
using Solana.Unity.Rpc.Core.Http;
using Solana.Unity.Rpc.Models;
using Solana.Unity.Rpc.Types;
using Solana.Unity.Wallet;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Service that checks a wallet's history for transactions containing configured memo keywords.
    /// Uses minimal RPC calls for compatibility with WebGL and mobile.
    /// </summary>
    public class TransactionMemoChecker
    {
        private const int MaxConcurrentTxFetches = 5;

        private readonly BatchedSignatureProvider _provider;
        private readonly TransactionFilterDescriptor _defaultDescriptor;
        private readonly int _defaultMaxSignatures;

        public TransactionMemoChecker(
            IRpcClient rpcClient,
            IEnumerable<string> memoSearchStrings,
            bool caseSensitive = false,
            int defaultMaxSignatures = 50,
            IEnumerable<string> destinationAccountFilters = null,
            IEnumerable<string> programIdFilters = null)
            : this(
                rpcClient != null
                    ? new BatchedSignatureProvider(
                        (address, limit, commitment) => rpcClient.GetSignaturesForAddressAsync(address, limit, commitment: commitment),
                        (signature, commitment) => rpcClient.GetTransactionAsync(signature, commitment: commitment),
                        MaxConcurrentTxFetches)
                    : throw new ArgumentNullException(nameof(rpcClient)),
                BuildDefaultDescriptor(memoSearchStrings, caseSensitive, destinationAccountFilters, programIdFilters),
                defaultMaxSignatures)
        {
        }

        public TransactionMemoChecker(
            Func<string, ulong, Commitment, Task<RequestResult<List<SignatureStatusInfo>>>> getSignatures,
            Func<string, Commitment, Task<RequestResult<TransactionMetaSlotInfo>>> getTransaction,
            IEnumerable<string> memoSearchStrings,
            bool caseSensitive = false,
            int defaultMaxSignatures = 50,
            IEnumerable<string> destinationAccountFilters = null,
            IEnumerable<string> programIdFilters = null,
            int maxConcurrentFetches = MaxConcurrentTxFetches)
            : this(
                new BatchedSignatureProvider(getSignatures, getTransaction, maxConcurrentFetches),
                BuildDefaultDescriptor(memoSearchStrings, caseSensitive, destinationAccountFilters, programIdFilters),
                defaultMaxSignatures)
        {
        }

        private TransactionMemoChecker(
            BatchedSignatureProvider provider,
            TransactionFilterDescriptor defaultDescriptor,
            int defaultMaxSignatures)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _defaultDescriptor = defaultDescriptor ?? throw new ArgumentNullException(nameof(defaultDescriptor));
            _defaultMaxSignatures = ValidateMaxSignatures(defaultMaxSignatures);
        }

        /// <summary>
        /// Returns true if the wallet has a finalized transaction that matches the configured memo criteria.
        /// </summary>
        [Obsolete("Use HasMatchingMemoAsync instead.")]
        public Task<bool> HasTokenTossMemoAsync(
            string walletAddress,
            int? maxSignatures = null,
            IEnumerable<string> memoSearchStrings = null,
            bool? caseSensitive = null,
            IEnumerable<string> destinationAccountFilters = null,
            IEnumerable<string> programIdFilters = null)
        {
            return HasMatchingMemoAsync(walletAddress, maxSignatures, memoSearchStrings, caseSensitive, destinationAccountFilters, programIdFilters);
        }

        /// <summary>
        /// Returns true if the wallet has a finalized transaction that matches the configured memo criteria.
        /// </summary>
        public async Task<bool> HasMatchingMemoAsync(
            string walletAddress,
            int? maxSignatures = null,
            IEnumerable<string> memoSearchStrings = null,
            bool? caseSensitive = null,
            IEnumerable<string> destinationAccountFilters = null,
            IEnumerable<string> programIdFilters = null)
        {
            ValidateWallet(walletAddress);

            var descriptor = BuildDescriptor(
                memoSearchStrings,
                caseSensitive,
                destinationAccountFilters,
                programIdFilters);

            int resolvedMax = ValidateMaxSignatures(maxSignatures ?? _defaultMaxSignatures);
            var transactions = await _provider
                .FetchTransactionsAsync(walletAddress, resolvedMax, Commitment.Finalized)
                .ConfigureAwait(false);

            foreach (var tx in transactions)
            {
                if (!TransactionFilterEvaluator.Matches(tx.Transaction, descriptor))
                    continue;

                var matches = MemoParser.ExtractMatches(tx.Transaction, descriptor.MemoSearchStrings, descriptor.Comparison);
                if (matches.Count > 0)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns a list of token skin identifiers purchased via TransferSPL transactions
        /// sent to studio wallets. Matches results using the configured memo criteria.
        /// </summary>
        public async Task<List<string>> GetPurchasedTokenSkinsAsync(
            string walletAddress,
            SolanaConfiguration config,
            int? maxSignatures = null,
            IEnumerable<string> memoSearchStrings = null,
            bool? caseSensitive = null,
            IEnumerable<string> destinationAccountFilters = null,
            IEnumerable<string> programIdFilters = null)
        {
            ValidateWallet(walletAddress);
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            string ghibliAta = null;
            string rewardAta = null;
            if (!string.IsNullOrEmpty(config.studioGhibliWalletPublicKey) && !string.IsNullOrEmpty(config.ghibliTokenMint))
            {
                ghibliAta = AssociatedTokenAccountProgram
                    .DeriveAssociatedTokenAccount(new PublicKey(config.studioGhibliWalletPublicKey), new PublicKey(config.ghibliTokenMint))
                    .Key;
            }
            if (!string.IsNullOrEmpty(config.studioRewardsTokenWalletPublicKey) && !string.IsNullOrEmpty(config.rewardTokenMint))
            {
                rewardAta = AssociatedTokenAccountProgram
                    .DeriveAssociatedTokenAccount(new PublicKey(config.studioRewardsTokenWalletPublicKey), new PublicKey(config.rewardTokenMint))
                    .Key;
            }

            var descriptor = BuildDescriptor(
                memoSearchStrings,
                caseSensitive,
                CombineFilters(destinationAccountFilters, new[] { ghibliAta, rewardAta }),
                programIdFilters);

            int resolvedMax = ValidateMaxSignatures(maxSignatures ?? _defaultMaxSignatures);
            var transactions = await _provider
                .FetchTransactionsAsync(walletAddress, resolvedMax, Commitment.Finalized)
                .ConfigureAwait(false);

            var skins = new List<string>();
            foreach (var tx in transactions)
            {
                if (!TransactionFilterEvaluator.Matches(tx.Transaction, descriptor))
                    continue;

                var matches = MemoParser.ExtractMatches(tx.Transaction, descriptor.MemoSearchStrings, descriptor.Comparison);
                foreach (var match in matches)
                {
                    var cleaned = string.IsNullOrEmpty(match.Suffix) ? match.MemoText : match.Suffix;
                    skins.Add(cleaned);
                }
            }

            return skins;
        }

        private static TransactionFilterDescriptor BuildDefaultDescriptor(
            IEnumerable<string> memoSearchStrings,
            bool caseSensitive,
            IEnumerable<string> destinationAccountFilters,
            IEnumerable<string> programIdFilters)
        {
            return new TransactionFilterBuilder()
                .WithMemoSearchStrings(ValidateMemoSearchStrings(memoSearchStrings))
                .WithCaseSensitivity(caseSensitive)
                .WithDestinationFilters(destinationAccountFilters)
                .WithProgramFilters(programIdFilters)
                .Build();
        }

        private TransactionFilterDescriptor BuildDescriptor(
            IEnumerable<string> memoSearchStrings,
            bool? caseSensitive,
            IEnumerable<string> destinationAccountFilters,
            IEnumerable<string> programIdFilters)
        {
            return new TransactionFilterBuilder()
                .FromDescriptor(_defaultDescriptor)
                .WithMemoSearchStrings(memoSearchStrings != null ? ValidateMemoSearchStrings(memoSearchStrings) : null)
                .WithCaseSensitivity(caseSensitive)
                .WithDestinationFilters(destinationAccountFilters)
                .WithProgramFilters(programIdFilters)
                .Build();
        }

        private static IEnumerable<string> ValidateMemoSearchStrings(IEnumerable<string> memoSearchStrings)
        {
            if (memoSearchStrings == null)
                throw new ArgumentNullException(nameof(memoSearchStrings));

            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var memo in memoSearchStrings)
            {
                if (string.IsNullOrWhiteSpace(memo))
                    continue;

                var trimmed = memo.Trim();
                if (seen.Add(trimmed))
                    list.Add(trimmed);
            }

            if (list.Count == 0)
                throw new ArgumentException("At least one memo search string must be provided.", nameof(memoSearchStrings));

            return list;
        }

        private static IEnumerable<string> CombineFilters(params IEnumerable<string>[] sources)
        {
            foreach (var source in sources)
            {
                if (source == null)
                    continue;

                foreach (var entry in source)
                {
                    if (!string.IsNullOrWhiteSpace(entry))
                        yield return entry;
                }
            }
        }

        private static void ValidateWallet(string walletAddress)
        {
            if (string.IsNullOrWhiteSpace(walletAddress))
                throw new ArgumentException("Wallet address is empty", nameof(walletAddress));

            _ = new PublicKey(walletAddress);
        }

        private static int ValidateMaxSignatures(int value)
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Signature limit must be positive");
            return value;
        }
    }
}

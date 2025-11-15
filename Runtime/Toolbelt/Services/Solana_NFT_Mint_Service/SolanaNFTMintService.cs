using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Solana.Unity.Metaplex.NFT.Library;
using Solana.Unity.Metaplex.Utilities;
using Solana.Unity.Programs;
using Solana.Unity.Rpc;
using Solana.Unity.Rpc.Core.Http;
using Solana.Unity.Rpc.Types;
using Solana.Unity.Rpc.Models;
using Solana.Unity.Wallet;
using Solana.Unity.SDK;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Encapsulates all information required to mint one or more NFTs.
    /// </summary>
    public class MintRequest
    {
        public string CollectionMint { get; set; }
        public string MetadataUri { get; set; }
        public string Name { get; set; }
        public string Symbol { get; set; }
        public ushort SellerFeeBasisPoints { get; set; }
        public List<Creator> Creators { get; set; } = new List<Creator>();
        public int Quantity { get; set; } = 1;
        public bool IsMutable { get; set; } = true;
    }

    public class MintResult
    {
        public bool Success { get; set; }
        public List<string> Signatures { get; set; } = new List<string>();
        public string ErrorMessage { get; set; }
    }

    public interface IMintBlockchainClient
    {
        Task<ulong> GetMinimumBalanceForRentExemptionAsync(int dataLength);
        Commitment Commitment { get; }
    }

    public class RpcMintBlockchainClient : IMintBlockchainClient
    {
        private readonly IRpcClient _rpcClient;
        private readonly Commitment _commitment;

        public RpcMintBlockchainClient(IRpcClient rpcClient, Commitment commitment = Commitment.Confirmed)
        {
            _rpcClient = rpcClient ?? throw new ArgumentNullException(nameof(rpcClient));
            _commitment = commitment;
        }

        public Commitment Commitment => _commitment;

        public async Task<ulong> GetMinimumBalanceForRentExemptionAsync(int dataLength)
        {
            var minRentRes = await _rpcClient.GetMinimumBalanceForRentExemptionAsync(dataLength, _commitment);
            if (!minRentRes.WasSuccessful)
                throw new InvalidOperationException("Failed to fetch rent-exemption for mint account.");

            return minRentRes.Result;
        }
    }

    /// <summary>
    /// Service that mints NFT(s) using Solana Unity SDK, leveraging provided wallet and RPC service.
    /// </summary>
    public class SolanaNFTMintService
    {
        private readonly IMintBlockchainClient _blockchainClient;
        private readonly WalletBase _wallet;
        private readonly int _blockhashMaxSeconds;
        private readonly ConcurrentDictionary<int, ulong> _rentCache = new();
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _rentLocks = new();
        private readonly object _rentCommitmentLock = new();
        private Commitment? _rentCacheCommitment;

        public SolanaNFTMintService(IRpcClient rpcClient, IWalletBase wallet, int blockhashMaxSeconds = 0)
            : this(
                new RpcMintBlockchainClient(rpcClient),
                RequireWalletBase(wallet),
                blockhashMaxSeconds)
        {
        }

        public SolanaNFTMintService(
            IMintBlockchainClient blockchainClient,
            WalletBase wallet,
            int blockhashMaxSeconds = 0)
        {
            _blockchainClient = blockchainClient ?? throw new ArgumentNullException(nameof(blockchainClient));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _blockhashMaxSeconds = Math.Max(0, blockhashMaxSeconds);

            if (_wallet.Account == null)
                throw new ArgumentException("Wallet account is required for minting.", nameof(wallet));
        }

        private static WalletBase RequireWalletBase(IWalletBase wallet)
        {
            if (wallet is WalletBase walletBase)
            {
                return walletBase;
            }

            throw new ArgumentException("Wallet implementation must derive from WalletBase.", nameof(wallet));
        }

        /// <summary>
        /// Mint one or more NFTs. Each NFT is minted in a separate transaction
        /// and the resulting signatures are returned in order.
        /// </summary>
        public virtual async Task<MintResult> MintAndVerifyAsync(MintRequest request, string memo = null)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var result = new MintResult();
            bool forceFreshBlockhash = false;

            try
            {
                var quantity = Math.Max(request.Quantity, 1);
                var lamportsForMint = await GetRentExemptionLamportsAsync(TokenProgram.MintAccountDataSize).ConfigureAwait(false);

                for (int i = 0; i < quantity; i++)
                {
                    var recentBlockhash = await GetRecentBlockhashAsync(forceFreshBlockhash).ConfigureAwait(false);
                    forceFreshBlockhash = false;

                    var builder = CreateTransactionBuilder(recentBlockhash, lamportsForMint, request, memo);
                    await CustomizeBuilderAsync(builder, request, i).ConfigureAwait(false);

                    var additionalSigners = GetAdditionalSignerAccounts(builder, request, i);
                    var transaction = builder.BuildTransaction(additionalSigners);
                    transaction = await CustomizeTransactionBeforeSigningAsync(transaction, request, i).ConfigureAwait(false);

                    RequestResult<string> submission;
                    try
                    {
                        submission = await SubmitTransactionAsync(transaction).ConfigureAwait(false);
                    }
                    catch
                    {
                        forceFreshBlockhash = true;
                        throw;
                    }

                    if (submission == null || !submission.WasSuccessful || string.IsNullOrEmpty(submission.Result))
                    {
                        forceFreshBlockhash = true;
                        throw new InvalidOperationException(submission?.Reason ?? "Transaction submission failed.");
                    }

                    result.Signatures.Add(submission.Result);
                }

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        protected virtual MintTransactionBuilder CreateTransactionBuilder(
            string recentBlockhash,
            ulong lamportsForMint,
            MintRequest request,
            string memo)
        {
            var builder = new MintTransactionBuilder(recentBlockhash, _wallet.Account, request)
                .AddMintAccountCreation(lamportsForMint)
                .AddMetadataAccount()
                .AddMasterEdition()
                .AddCollectionVerification()
                .AddMemo(memo);

            return builder;
        }

        protected virtual Task CustomizeBuilderAsync(MintTransactionBuilder builder, MintRequest request, int mintIndex)
        {
            return Task.CompletedTask;
        }

        protected virtual IEnumerable<Account> GetAdditionalSignerAccounts(MintTransactionBuilder builder, MintRequest request, int mintIndex)
        {
            return Array.Empty<Account>();
        }

        protected virtual Task<Transaction> CustomizeTransactionBeforeSigningAsync(Transaction transaction, MintRequest request, int mintIndex)
        {
            return Task.FromResult(transaction);
        }

        protected Account WalletAccount => _wallet.Account;

        private Task<RequestResult<string>> SubmitTransactionAsync(Transaction transaction)
        {
            return _wallet.SignAndSendTransaction(
                transaction,
                skipPreflight: false,
                commitment: _blockchainClient.Commitment);
        }

        private Task<string> GetRecentBlockhashAsync(bool forceRefresh)
        {
            bool useCache = !forceRefresh && _blockhashMaxSeconds > 0;
            return _wallet.GetBlockHash(_blockchainClient.Commitment, useCache, _blockhashMaxSeconds);
        }

        private async Task<ulong> GetRentExemptionLamportsAsync(int dataLength)
        {
            var commitment = _blockchainClient.Commitment;
            EnsureRentCacheCommitment(commitment);

            if (_rentCache.TryGetValue(dataLength, out var cached))
                return cached;

            var semaphore = _rentLocks.GetOrAdd(dataLength, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                if (_rentCache.TryGetValue(dataLength, out cached))
                    return cached;

                var lamports = await _blockchainClient.GetMinimumBalanceForRentExemptionAsync(dataLength).ConfigureAwait(false);
                _rentCache[dataLength] = lamports;
                return lamports;
            }
            finally
            {
                semaphore.Release();
            }
        }

        private void EnsureRentCacheCommitment(Commitment commitment)
        {
            var current = _rentCacheCommitment;
            if (current.HasValue && current.Value == commitment)
                return;

            lock (_rentCommitmentLock)
            {
                current = _rentCacheCommitment;
                if (current.HasValue && current.Value == commitment)
                    return;

                _rentCache.Clear();
                _rentLocks.Clear();
                _rentCacheCommitment = commitment;
            }
        }

    }
}

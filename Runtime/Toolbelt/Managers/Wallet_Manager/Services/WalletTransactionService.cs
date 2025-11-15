using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Solana.Unity.Programs;
using Solana.Unity.Toolbelt;
using Solana.Unity.Rpc;
using Solana.Unity.Rpc.Core.Http;
using Solana.Unity.Rpc.Messages;
using Solana.Unity.Rpc.Models;
using Solana.Unity.Rpc.Types;
using Solana.Unity.Wallet;
using RpcTokenBalance = Solana.Unity.Rpc.Models.TokenBalance;
using Solana.Unity.SDK;

namespace Solana.Unity.Toolbelt.Wallet
{
    /// <summary>
    /// Provides balance queries and transaction submission capabilities for the
    /// currently connected wallet. Designed to be used outside of MonoBehaviours
    /// for improved testability.
    /// </summary>
    public class WalletTransactionService : IWalletTransactionService
    {
        private readonly IWalletSessionService sessionService;
        private readonly IWeb3Facade web3;
        private readonly int blockhashCacheMaxSeconds;
        private readonly IRpcEndpointManager rpcEndpointManager;
        private const int TokenAccountStateOffset = 108;
        private readonly Dictionary<Commitment, (DateTime Timestamp, string Blockhash)> blockhashCache = new();
        private readonly object blockhashCacheLock = new();

        public WalletTransactionService(
            IWalletSessionService sessionService,
            IWeb3Facade web3,
            int blockhashCacheMaxSeconds = 0,
            IRpcEndpointManager rpcEndpointManager = null)
        {
            this.sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
            this.web3 = web3 ?? throw new ArgumentNullException(nameof(web3));
            this.blockhashCacheMaxSeconds = Math.Max(0, blockhashCacheMaxSeconds);
            this.rpcEndpointManager = rpcEndpointManager;
        }

        public async Task<ulong> GetSolBalanceAsync(Commitment commitment = Commitment.Finalized)
        {
            if (!sessionService.IsWalletConnected || sessionService.CurrentPublicKey == null)
            {
                return 0;
            }

            var result = await ExecuteRpcAsync(client =>
                    client.GetBalanceAsync(sessionService.CurrentPublicKey.Key, commitment))
                .ConfigureAwait(false);

            return result != null && result.WasSuccessful && result.Result != null
                ? result.Result.Value
                : 0UL;
        }

        public async Task<RequestResult<ResponseValue<RpcTokenBalance>>> GetTokenBalanceAsync(string tokenMint, Commitment commitment = Commitment.Finalized)
        {
            if (!sessionService.IsWalletConnected || sessionService.CurrentPublicKey == null)
            {
                throw new InvalidOperationException("Wallet not connected");
            }

            var ata = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(sessionService.CurrentPublicKey, new PublicKey(tokenMint));
            return await ExecuteRpcAsync(client =>
                    client.GetTokenAccountBalanceAsync(ata.Key, commitment))
                .ConfigureAwait(false);
        }

        public async Task<RequestResult<string>> TransferSolAsync(string destination, ulong lamports, string memo = null, Commitment commitment = Commitment.Finalized)
        {
            EnsureWalletConnected();

            if (sessionService.SimulatePaymentSuccess)
            {
                return new RequestResult<string> { Result = "SIMULATED_TX_SIG" };
            }

            var wallet = RequireWalletBase();
            var payer = sessionService.CurrentPublicKey;

            string recentBlockhash = await GetRecentBlockhashAsync(commitment).ConfigureAwait(false);
            if (string.IsNullOrEmpty(recentBlockhash))
            {
                return new RequestResult<string>
                {
                    Reason = "Unable to fetch a recent blockhash."
                };
            }

            var tx = new Transaction
            {
                FeePayer = payer,
                RecentBlockHash = recentBlockhash,
                Instructions = new List<TransactionInstruction>
                {
                    SystemProgram.Transfer(payer, new PublicKey(destination), lamports)
                }
            };

            var memoIx = MemoUtils.CreateMemoInstruction(memo);
            if (memoIx != null)
            {
                tx.Instructions.Add(memoIx);
            }

            Transaction signedTransaction;
            try
            {
                signedTransaction = await RunOnUnityThreadAsync(() => wallet.SignTransaction(tx)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new RequestResult<string>
                {
                    Reason = $"Signing failed: {ex.Message}"
                };
            }

            string encodedTransaction = Convert.ToBase64String(signedTransaction.Serialize());

            return await ExecuteRpcAsync(client =>
                    client.SendTransactionAsync(
                        encodedTransaction,
                        skipPreflight: false,
                        preFlightCommitment: commitment))
                .ConfigureAwait(false);
        }

        public async Task<RequestResult<string>> TransferSplTokenAsync(string tokenMint, string destination, ulong amount, string memo = null, Commitment commitment = Commitment.Finalized)
        {
            EnsureWalletConnected();

            if (sessionService.SimulatePaymentSuccess)
            {
                return new RequestResult<string> { Result = "SIMULATED_SPL_TX_SIG" };
            }

            var mintKey = new PublicKey(tokenMint);
            var sourceAta = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(sessionService.CurrentPublicKey, mintKey);
            var destAta = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(new PublicKey(destination), mintKey);

            var wallet = RequireWalletBase();
            var payer = sessionService.CurrentPublicKey;
            var payerAccount = sessionService.CurrentAccount ?? throw new InvalidOperationException("Wallet account is unavailable.");

            var instructions = new List<TransactionInstruction>();

            var destAtaInfoResult = await ExecuteRpcAsync(client =>
                    client.GetAccountInfoAsync(destAta.Key, commitment))
                .ConfigureAwait(false);

            var destAtaOwnedByTokenProgram = destAtaInfoResult?.WasSuccessful == true &&
                                              destAtaInfoResult.Result?.Value != null &&
                                              string.Equals(
                                                  destAtaInfoResult.Result.Value.Owner,
                                                  TokenProgram.ProgramIdKey.Key,
                                                  StringComparison.Ordinal);

            var destAtaInitialized = false;

            if (destAtaOwnedByTokenProgram)
            {
                var accountData = destAtaInfoResult.Result.Value.Data;
                var dataBase64 = accountData != null && accountData.Count > 0 ? accountData[0] : null;

                if (!string.IsNullOrEmpty(dataBase64))
                {
                    byte[] rawData;

                    try
                    {
                        rawData = Convert.FromBase64String(dataBase64);
                    }
                    catch (FormatException)
                    {
                        rawData = Array.Empty<byte>();
                    }

                    if (rawData.Length > TokenAccountStateOffset)
                    {
                        destAtaInitialized = rawData[TokenAccountStateOffset] != 0;
                    }
                }
            }

            if (!destAtaOwnedByTokenProgram || !destAtaInitialized)
            {
                instructions.Add(
                    AssociatedTokenAccountProgram.CreateAssociatedTokenAccount(
                        payerAccount,
                        new PublicKey(destination),
                        mintKey));
            }

            instructions.Add(
                TokenProgram.Transfer(
                    new PublicKey(sourceAta.Key),
                    new PublicKey(destAta.Key),
                    amount,
                    payer));

            var memoIx = MemoUtils.CreateMemoInstruction(memo);
            if (memoIx != null)
            {
                instructions.Add(memoIx);
            }

            string recentBlockhash = await GetRecentBlockhashAsync(commitment).ConfigureAwait(false);
            if (string.IsNullOrEmpty(recentBlockhash))
            {
                return new RequestResult<string>
                {
                    Reason = "Unable to fetch a recent blockhash."
                };
            }

            var tx = new Transaction
            {
                FeePayer = payer,
                RecentBlockHash = recentBlockhash,
                Instructions = instructions
            };

            Transaction signedTransaction;
            try
            {
                signedTransaction = await RunOnUnityThreadAsync(() => wallet.SignTransaction(tx)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new RequestResult<string>
                {
                    Reason = $"Signing failed: {ex.Message}"
                };
            }

            string encodedTransaction = Convert.ToBase64String(signedTransaction.Serialize());

            return await ExecuteRpcAsync(client =>
                    client.SendTransactionAsync(
                        encodedTransaction,
                        skipPreflight: false,
                        preFlightCommitment: commitment))
                .ConfigureAwait(false);
        }

        public async Task<string> GetRecentBlockhashAsync(Commitment commitment = Commitment.Finalized)
        {
            EnsureWalletConnected();

            string cached = TryGetCachedBlockhash(commitment);
            if (!string.IsNullOrEmpty(cached))
            {
                return cached;
            }

            var result = await ExecuteRpcAsync(client => client.GetLatestBlockHashAsync(commitment)).ConfigureAwait(false);
            string blockhash = result?.WasSuccessful == true ? result.Result?.Value?.Blockhash : null;

            CacheBlockhash(commitment, blockhash);
            return blockhash;
        }

        private void EnsureWalletConnected()
        {
            if (!sessionService.IsWalletConnected || sessionService.CurrentPublicKey == null)
            {
                throw new InvalidOperationException("Wallet not connected");
            }
        }

        private WalletBase RequireWalletBase()
        {
            var wallet = web3.WalletBase;
            if (wallet == null)
            {
                throw new InvalidOperationException("Solana wallet is unavailable.");
            }

            return wallet;
        }

        private Task<RequestResult<TResponse>> ExecuteRpcAsync<TResponse>(
            Func<IRpcClient, Task<RequestResult<TResponse>>> operation)
        {
            if (rpcEndpointManager != null)
            {
                return rpcEndpointManager.ExecuteAsync(operation);
            }

            var client = web3.RpcClient;
            if (client == null)
            {
                throw new InvalidOperationException("Solana RPC client is unavailable.");
            }

            return operation(client);
        }

        private string TryGetCachedBlockhash(Commitment commitment)
        {
            if (blockhashCacheMaxSeconds <= 0)
            {
                return null;
            }

            lock (blockhashCacheLock)
            {
                if (blockhashCache.TryGetValue(commitment, out var entry))
                {
                    if ((DateTime.UtcNow - entry.Timestamp).TotalSeconds < blockhashCacheMaxSeconds)
                    {
                        return entry.Blockhash;
                    }

                    blockhashCache.Remove(commitment);
                }
            }

            return null;
        }

        private void CacheBlockhash(Commitment commitment, string blockhash)
        {
            if (blockhashCacheMaxSeconds <= 0 || string.IsNullOrEmpty(blockhash))
            {
                return;
            }

            lock (blockhashCacheLock)
            {
                blockhashCache[commitment] = (DateTime.UtcNow, blockhash);
            }
        }

        private static Task<T> RunOnUnityThreadAsync<T>(Func<Task<T>> action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (!MainThreadDispatcher.Exists())
            {
                return action();
            }

            var completionSource = new TaskCompletionSource<T>();

            try
            {
                MainThreadDispatcher.Instance().Enqueue(() =>
                {
                    try
                    {
                        var task = action();
                        if (task == null)
                        {
                            completionSource.TrySetException(new InvalidOperationException("The provided action returned a null task."));
                            return;
                        }

                        task.ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                            {
                                var baseException = t.Exception?.GetBaseException() ?? t.Exception;
                                completionSource.TrySetException(baseException ?? new Exception("Sign operation failed."));
                            }
                            else if (t.IsCanceled)
                            {
                                completionSource.TrySetCanceled();
                            }
                            else
                            {
                                completionSource.TrySetResult(t.Result);
                            }
                        }, TaskScheduler.Default);
                    }
                    catch (Exception ex)
                    {
                        completionSource.TrySetException(ex);
                    }
                });
            }
            catch (Exception ex)
            {
                completionSource.TrySetException(ex);
            }

            return completionSource.Task;
        }
    }
}

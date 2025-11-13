using System.Globalization;
using System.Threading.Tasks;
using Solana.Unity.Rpc.Types;
namespace Solana.Unity.Toolbelt
{
    public sealed class WalletManagerToolbeltAdapter : IToolbeltWalletService
    {
        private readonly WalletManager _walletManager;

        public WalletManagerToolbeltAdapter(WalletManager walletManager)
        {
            _walletManager = walletManager;
        }

        public bool IsWalletConnected => _walletManager?.Session?.IsWalletConnected ?? false;

        public bool IsWalletVerified => _walletManager?.Session?.IsWalletVerified ?? false;

        public string CurrentPublicKey => _walletManager?.Session?.CurrentPublicKey?.Key;

        public Task<bool> ConnectAsync()
        {
            return _walletManager?.Session?.ConnectAsync() ?? Task.FromResult(false);
        }

        public Task DisconnectAsync()
        {
            return _walletManager?.Session?.DisconnectAsync() ?? Task.CompletedTask;
        }

        public void RefreshVerificationStatus()
        {
            _walletManager?.Session?.RefreshVerificationStatus();
        }

        public Task<bool> VerifyOwnershipAsync(string memo)
        {
            return _walletManager?.Verification?.VerifyOwnershipAsync(memo) ?? Task.FromResult(false);
        }

        public Task<ulong> GetSolBalanceAsync()
        {
            if (_walletManager?.Transactions == null)
            {
                return Task.FromResult(0UL);
            }

            return _walletManager.Transactions.GetSolBalanceAsync(Commitment.Finalized);
        }

        public async Task<TokenBalance> GetTokenBalanceAsync(string tokenMint)
        {
            if (_walletManager?.Transactions == null)
            {
                return new TokenBalance(false, null, null, "Transaction service unavailable");
            }

            var result = await _walletManager.Transactions
                .GetTokenBalanceAsync(tokenMint, Commitment.Finalized);

            if (result == null)
            {
                return new TokenBalance(false, null, null, "No response");
            }

            if (!result.WasSuccessful || result.Result?.Value == null)
            {
                return new TokenBalance(false, null, null, result.Reason);
            }

            var value = result.Result.Value;
            var uiAmountString = value.UiAmountString;
            decimal? uiAmount = null;
            if (!string.IsNullOrEmpty(uiAmountString) &&
                decimal.TryParse(uiAmountString, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedUiAmount))
            {
                uiAmount = parsedUiAmount;
            }

            return new TokenBalance(true, uiAmount, uiAmountString ?? string.Empty, null);
        }

        public async Task<TransactionResult> TransferSolAsync(string destination, ulong lamports, RpcCommitment commitment = RpcCommitment.Finalized, string memo = null)
        {
            if (_walletManager?.Transactions == null)
            {
                return new TransactionResult(false, null, "Transaction service unavailable");
            }

            var request = await _walletManager.Transactions
                .TransferSolAsync(destination, lamports, memo, ToCommitment(commitment));

            return request == null
                ? new TransactionResult(false, null, "No response")
                : new TransactionResult(request.WasSuccessful, request.Result, request.Reason);
        }

        public async Task<TransactionResult> TransferSplTokenAsync(string tokenMint, string destination, ulong rawAmount, RpcCommitment commitment = RpcCommitment.Finalized, string memo = null)
        {
            if (_walletManager?.Transactions == null)
            {
                return new TransactionResult(false, null, "Transaction service unavailable");
            }

            var request = await _walletManager.Transactions
                .TransferSplTokenAsync(tokenMint, destination, rawAmount, memo, ToCommitment(commitment));

            return request == null
                ? new TransactionResult(false, null, "No response")
                : new TransactionResult(request.WasSuccessful, request.Result, request.Reason);
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

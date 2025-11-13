using System.Threading.Tasks;

namespace Solana.Unity.Toolbelt
{
    public interface IToolbeltWalletService
    {
        bool IsWalletConnected { get; }
        bool IsWalletVerified { get; }
        string CurrentPublicKey { get; }

        Task<bool> ConnectAsync();
        Task DisconnectAsync();

        void RefreshVerificationStatus();
        Task<bool> VerifyOwnershipAsync(string memo);

        Task<ulong> GetSolBalanceAsync();
        Task<TokenBalance> GetTokenBalanceAsync(string tokenMint);

        Task<TransactionResult> TransferSolAsync(
            string destination,
            ulong lamports,
            RpcCommitment commitment = RpcCommitment.Finalized,
            string memo = null);

        Task<TransactionResult> TransferSplTokenAsync(
            string tokenMint,
            string destination,
            ulong rawAmount,
            RpcCommitment commitment = RpcCommitment.Finalized,
            string memo = null);
    }
}

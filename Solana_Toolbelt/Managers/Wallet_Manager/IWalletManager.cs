using System.Threading.Tasks;
using Solana.Unity.Rpc.Core.Http;
using Solana.Unity.Rpc.Messages;
using Solana.Unity.Rpc.Models;
using Solana.Unity.Rpc.Types;
using Solana.Unity.Wallet;
using Solana.Unity.Toolbelt.Wallet;
using Solana.Unity.SDK;
using RpcTokenBalance = Solana.Unity.Rpc.Models.TokenBalance;

public interface IWalletManager
{
    IWalletSessionService Session { get; }
    IWalletTransactionService Transactions { get; }
    IWalletVerificationService Verification { get; }

    PublicKey CurrentPublicKey { get; }
    Account CurrentAccount { get; }
    bool IsWalletConnected { get; }
    bool IsWalletVerified { get; }
    bool IsWalletStreamingDegraded { get; }

    Task<bool> ConnectAsync();
    Task DisconnectAsync();

    Task<ulong> GetSolBalanceAsync(Commitment commitment = Commitment.Finalized);
    Task<RequestResult<ResponseValue<RpcTokenBalance>>> GetTokenBalanceAsync(string tokenMint, Commitment commitment = Commitment.Finalized);
    Task<RequestResult<string>> TransferSolAsync(string destination, ulong lamports, string memo = null, Commitment commitment = Commitment.Finalized);
    Task<RequestResult<string>> TransferSplTokenAsync(string tokenMint, string destination, ulong amount, string memo = null, Commitment commitment = Commitment.Finalized);
    Task<string> GetRecentBlockhashAsync(Commitment commitment = Commitment.Finalized);
    Task<bool> SignMessageAndVerifyOwnership(string message);
    void RefreshVerificationStatus();
    Task<SessionWallet> GetSessionWalletAsync(PublicKey targetProgram, string password);
    Task<string> MintLevelEditorUnlockNftAsync(string memo = null);
}

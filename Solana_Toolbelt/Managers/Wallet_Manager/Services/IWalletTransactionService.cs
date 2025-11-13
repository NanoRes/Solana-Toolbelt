using System.Threading.Tasks;
using Solana.Unity.Rpc.Core.Http;
using Solana.Unity.Rpc.Messages;
using Solana.Unity.Rpc.Models;
using Solana.Unity.Rpc.Types;
using RpcTokenBalance = Solana.Unity.Rpc.Models.TokenBalance;

namespace Solana.Unity.Toolbelt.Wallet
{
    public interface IWalletTransactionService
    {
        Task<ulong> GetSolBalanceAsync(Commitment commitment = Commitment.Finalized);
        Task<RequestResult<ResponseValue<RpcTokenBalance>>> GetTokenBalanceAsync(string tokenMint, Commitment commitment = Commitment.Finalized);
        Task<RequestResult<string>> TransferSolAsync(string destination, ulong lamports, string memo = null, Commitment commitment = Commitment.Finalized);
        Task<RequestResult<string>> TransferSplTokenAsync(string tokenMint, string destination, ulong amount, string memo = null, Commitment commitment = Commitment.Finalized);
        Task<string> GetRecentBlockhashAsync(Commitment commitment = Commitment.Finalized);
    }
}

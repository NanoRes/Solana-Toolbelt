using System.Threading.Tasks;

namespace Solana.Unity.Toolbelt.Wallet
{
    public interface IWalletVerificationService
    {
        Task<bool> VerifyOwnershipAsync(string message);
    }
}

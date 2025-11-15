using System;
using System.Threading.Tasks;
using Solana.Unity.SDK;
using Solana.Unity.Wallet;

namespace Solana.Unity.Toolbelt.Wallet
{
    public interface IWalletSessionService
    {
        event Action Connected;
        event Action Disconnected;
        event Action<string> ConnectionFailed;
        event Action<string> WalletVerified;
        event Action<ulong> SolBalanceChanged;
        event Action<bool> StreamingHealthChanged;

        PublicKey CurrentPublicKey { get; }
        Account CurrentAccount { get; }
        WalletBase WalletBase { get; }
        bool IsWalletConnected { get; }
        bool IsWalletVerified { get; }
        bool IsStreamingDegraded { get; }
        bool SimulatePaymentSuccess { get; }

        Task InitializeAsync();
        Task<bool> ConnectAsync();
        Task DisconnectAsync();
        Task ShutdownAsync();

        void RefreshVerificationStatus();
        void MarkWalletVerified(string publicKey);
        void ClearVerificationStatus();
    }
}

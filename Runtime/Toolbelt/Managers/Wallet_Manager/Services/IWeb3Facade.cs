using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Solana.Unity.Rpc;
using Solana.Unity.Rpc.Models;
using Solana.Unity.SDK;
using Solana.Unity.SDK.Nft;
using Solana.Unity.Wallet;

namespace Solana.Unity.Toolbelt.Wallet
{
    /// <summary>
    /// Facade that exposes the subset of Web3 functionality required by the
    /// wallet services while remaining mockable for tests.
    /// </summary>
    public interface IWeb3Facade
    {
        Account Account { get; }
        WalletBase WalletBase { get; set; }
        IRpcClient RpcClient { get; }

        event Action WalletStateChanged;
        event Action<Account> LoggedIn;
        event Action LoggedOut;
        event Action<double> BalanceChanged;
        event Action WebSocketConnected;
        event Action<IReadOnlyList<Nft>> NftsUpdated;

        Task LoginWalletAdapterAsync();
        Task LoginWeb3AuthAsync(Provider provider);
        void Logout();
        Task<Transaction> SignTransactionAsync(Transaction transaction);
        Task<byte[]> SignMessageAsync(byte[] message);
        Task<SessionWallet> GetSessionWalletAsync(PublicKey targetProgram, string password, WalletBase externalWallet = null);
    }
}

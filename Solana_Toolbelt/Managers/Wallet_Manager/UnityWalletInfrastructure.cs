using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Solana.Unity.Rpc;
using Solana.Unity.Rpc.Models;
using Solana.Unity.SDK;
using Solana.Unity.SDK.Nft;
using Solana.Unity.Wallet;
using UnityEngine;

namespace Solana.Unity.Toolbelt.Wallet
{
    public sealed class UnityPlayerPrefsStore : IPlayerPrefsStore
    {
        public bool HasKey(string key) => PlayerPrefs.HasKey(key);
        public string GetString(string key, string defaultValue = "") => PlayerPrefs.GetString(key, defaultValue);
        public void SetString(string key, string value) => PlayerPrefs.SetString(key, value);
        public void DeleteKey(string key) => PlayerPrefs.DeleteKey(key);
        public void Save() => PlayerPrefs.Save();
    }

    public sealed class UnityWalletLogger : IWalletLogger
    {
        public void Log(string message) => Debug.Log(message);
        public void LogWarning(string message) => Debug.LogWarning(message);
        public void LogError(string message) => Debug.LogError(message);
    }

    public sealed class Web3Facade : IWeb3Facade
    {
        private readonly Dictionary<Action, List<Web3.WalletChange>> walletStateChangedHandlers = new();
        private readonly Dictionary<Action<double>, List<Web3.BalanceChange>> balanceChangedHandlers = new();
        private readonly Dictionary<Action<IReadOnlyList<Nft>>, Web3.NFTsUpdate> nftUpdateHandlers =
            new();

        public Account Account => Web3.Account;

        public WalletBase WalletBase
        {
            get => Web3.Instance?.WalletBase;
            set
            {
                if (Web3.Instance != null)
                {
                    if (value is WalletBase walletBase)
                    {
                        Web3.Instance.WalletBase = walletBase;
                    }
                    else if (value != null)
                    {
                        throw new InvalidCastException(
                            $"Unable to assign wallet of type {value.GetType().FullName} to Web3.Instance.WalletBase.");
                    }
                    else
                    {
                        Web3.Instance.WalletBase = null;
                    }
                }
            }
        }

        public IRpcClient RpcClient => Web3.Rpc;

        public event Action WalletStateChanged
        {
            add
            {
                if (value == null)
                {
                    return;
                }

                var handler = new Web3.WalletChange(value);
                if (!walletStateChangedHandlers.TryGetValue(value, out var handlers))
                {
                    handlers = new List<Web3.WalletChange>();
                    walletStateChangedHandlers[value] = handlers;
                }

                handlers.Add(handler);
                Web3.OnWalletChangeState += handler;
            }
            remove
            {
                if (value == null)
                {
                    return;
                }

                if (!walletStateChangedHandlers.TryGetValue(value, out var handlers) || handlers.Count == 0)
                {
                    return;
                }

                var index = handlers.Count - 1;
                var handler = handlers[index];
                handlers.RemoveAt(index);
                if (handlers.Count == 0)
                {
                    walletStateChangedHandlers.Remove(value);
                }

                Web3.OnWalletChangeState -= handler;
            }
        }

        public event Action<Account> LoggedIn
        {
            add => Web3.OnLogin += value;
            remove => Web3.OnLogin -= value;
        }

        public event Action LoggedOut
        {
            add => Web3.OnLogout += value;
            remove => Web3.OnLogout -= value;
        }

        public event Action<double> BalanceChanged
        {
            add
            {
                if (value == null)
                {
                    return;
                }

                Web3.BalanceChange handler = balance => value(balance);
                if (!balanceChangedHandlers.TryGetValue(value, out var handlers))
                {
                    handlers = new List<Web3.BalanceChange>();
                    balanceChangedHandlers[value] = handlers;
                }

                handlers.Add(handler);
                Web3.OnBalanceChange += handler;
            }
            remove
            {
                if (value == null)
                {
                    return;
                }

                if (!balanceChangedHandlers.TryGetValue(value, out var handlers) || handlers.Count == 0)
                {
                    return;
                }

                var index = handlers.Count - 1;
                var handler = handlers[index];
                handlers.RemoveAt(index);
                if (handlers.Count == 0)
                {
                    balanceChangedHandlers.Remove(value);
                }

                Web3.OnBalanceChange -= handler;
            }
        }

        public event Action WebSocketConnected
        {
            add => Web3.OnWebSocketConnect += value;
            remove => Web3.OnWebSocketConnect -= value;
        }

        public event Action<IReadOnlyList<Nft>> NftsUpdated
        {
            add
            {
                if (value == null)
                {
                    return;
                }

                Web3.NFTsUpdate handler = (nfts, _) => value(nfts);
                nftUpdateHandlers[value] = handler;
                Web3.OnNFTsUpdate += handler;
            }
            remove
            {
                if (value == null)
                {
                    return;
                }

                if (nftUpdateHandlers.TryGetValue(value, out var handler))
                {
                    Web3.OnNFTsUpdate -= handler;
                    nftUpdateHandlers.Remove(value);
                }
            }
        }

        public Task LoginWalletAdapterAsync() => Web3.Instance.LoginWalletAdapter();
        public Task LoginWeb3AuthAsync(Provider provider) => Web3.Instance.LoginWeb3Auth(provider);
        public void Logout()
        {
            if (Web3.Instance == null)
            {
                Debug.LogWarning("Attempted to logout but Web3.Instance was null.");
                return;
            }

            Web3.Instance.Logout();
        }
        public Task<Transaction> SignTransactionAsync(Transaction transaction) => Web3.Wallet.SignTransaction(transaction);
        public Task<byte[]> SignMessageAsync(byte[] message) => Web3.Wallet.SignMessage(message);

        public Task<SessionWallet> GetSessionWalletAsync(
            PublicKey targetProgram,
            string password,
            WalletBase externalWallet = null)
        {
            if (targetProgram == null)
            {
                throw new ArgumentNullException(nameof(targetProgram));
            }

            return SessionWallet.GetSessionWallet(targetProgram, password, externalWallet ?? Web3.Wallet);
        }
    }
}

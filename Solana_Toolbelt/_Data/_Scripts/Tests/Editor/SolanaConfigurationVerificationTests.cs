using System;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using NUnit.Framework;
using Solana.Unity.Rpc.Models;
using Solana.Unity.SDK;
using Solana.Unity.Toolbelt.Wallet;
using Solana.Unity.Wallet;
using UnityEngine;

namespace Solana.Unity.Toolbelt.Tests.Editor
{
    public class SolanaConfigurationVerificationTests
    {
        [SetUp]
        public void SetUp()
        {
            ResetSessionWalletStatics();
        }

        [TearDown]
        public void TearDown()
        {
            ResetSessionWalletStatics();
        }

        [Test]
        public void ShouldReturnFalseWhenVerificationDisabled()
        {
            var config = ScriptableObject.CreateInstance<SolanaConfiguration>();
            config.verifyLevelCreator = false;

            var result = config.ShouldVerifyLevelCreatorForSession(new StubSessionService(), RuntimePlatform.Android);

            Assert.IsFalse(result);
        }

        [Test]
        public void SessionWalletWithoutExternalAuthorityIsRejected()
        {
            var config = ScriptableObject.CreateInstance<SolanaConfiguration>();
            var sessionWallet = CreateSessionWalletStub();
            var sessionService = new StubSessionService
            {
                WalletBase = sessionWallet,
                CurrentPublicKey = new PublicKey("11111111111111111111111111111111"),
                IsWalletVerified = true
            };

            var result = config.ShouldVerifyLevelCreatorForSession(sessionService, RuntimePlatform.Android);

            Assert.IsFalse(result);
        }

        [Test]
        public void SessionWalletWithValidExternalAuthorityIsAccepted()
        {
            var config = ScriptableObject.CreateInstance<SolanaConfiguration>();
            var sessionWallet = CreateSessionWalletStub();
            var sessionService = new StubSessionService
            {
                WalletBase = sessionWallet,
                CurrentPublicKey = new PublicKey("11111111111111111111111111111111"),
                IsWalletVerified = true
            };

            SetSessionWalletExternalAuthority("22222222222222222222222222222222");

            var result = config.ShouldVerifyLevelCreatorForSession(sessionService, RuntimePlatform.Android);

            Assert.IsTrue(result);
        }

        [Test]
        public void SessionWalletWithMatchingExternalAuthorityIsRejected()
        {
            var config = ScriptableObject.CreateInstance<SolanaConfiguration>();
            var sessionWallet = CreateSessionWalletStub();
            var sessionService = new StubSessionService
            {
                WalletBase = sessionWallet,
                CurrentPublicKey = new PublicKey("11111111111111111111111111111111"),
                IsWalletVerified = true
            };

            SetSessionWalletExternalAuthority("11111111111111111111111111111111");

            var result = config.ShouldVerifyLevelCreatorForSession(sessionService, RuntimePlatform.Android);

            Assert.IsFalse(result);
        }

        [Test]
        public void NonSessionWalletMustBeVerified()
        {
            var config = ScriptableObject.CreateInstance<SolanaConfiguration>();
            var wallet = new TestWallet();
            var sessionService = new StubSessionService
            {
                WalletBase = wallet,
                CurrentPublicKey = new PublicKey("11111111111111111111111111111111"),
                IsWalletVerified = false
            };

            var result = config.ShouldVerifyLevelCreatorForSession(sessionService, RuntimePlatform.Android);

            Assert.IsFalse(result);
        }

        [Test]
        public void VerifiedNonSessionWalletIsAccepted()
        {
            var config = ScriptableObject.CreateInstance<SolanaConfiguration>();
            var wallet = new TestWallet();
            var sessionService = new StubSessionService
            {
                WalletBase = wallet,
                CurrentPublicKey = new PublicKey("11111111111111111111111111111111"),
                IsWalletVerified = true
            };

            var result = config.ShouldVerifyLevelCreatorForSession(sessionService, RuntimePlatform.Android);

            Assert.IsTrue(result);
        }

        private static void ResetSessionWalletStatics()
        {
            var instanceField = typeof(SessionWallet).GetField("Instance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            instanceField?.SetValue(null, null);

            var externalField = typeof(SessionWallet).GetField("_externalWallet", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            externalField?.SetValue(null, null);
        }

        private static void SetSessionWalletExternalAuthority(string base58PublicKey)
        {
            var account = CreateAccountWithPublicKey(base58PublicKey);
            var wallet = new TestWallet();
            wallet.AssignAccount(account);

            var externalField = typeof(SessionWallet).GetField("_externalWallet", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            externalField?.SetValue(null, wallet);
        }

        private static SessionWallet CreateSessionWalletStub()
        {
            return (SessionWallet)FormatterServices.GetUninitializedObject(typeof(SessionWallet));
        }

        private static Account CreateAccountWithPublicKey(string base58PublicKey)
        {
            var accountType = typeof(Account);
            var publicKey = new PublicKey(base58PublicKey);

            foreach (var ctor in accountType.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                var parameters = ctor.GetParameters();
                try
                {
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                    {
                        var candidate = (Account)ctor.Invoke(new object[] { base58PublicKey });
                        if (candidate?.PublicKey != null)
                        {
                            return candidate;
                        }
                    }
                    else if (parameters.Length == 0)
                    {
                        var candidate = (Account)ctor.Invoke(Array.Empty<object>());
                        SetAccountPublicKey(candidate, publicKey);
                        return candidate;
                    }
                }
                catch
                {
                    // Ignore and fall back to manual construction.
                }
            }

            var account = (Account)FormatterServices.GetUninitializedObject(accountType);
            SetAccountPublicKey(account, publicKey);
            return account;
        }

        private static void SetAccountPublicKey(Account account, PublicKey publicKey)
        {
            var accountType = typeof(Account);
            var property = accountType.GetProperty("PublicKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (property?.CanWrite == true)
            {
                property.SetValue(account, publicKey);
                return;
            }

            var backingField = accountType.GetField("<PublicKey>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (backingField != null)
            {
                backingField.SetValue(account, publicKey);
                return;
            }

            var field = accountType.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .FirstOrDefault(f => f.FieldType == typeof(PublicKey));
            if (field != null)
            {
                field.SetValue(account, publicKey);
                return;
            }

            throw new InvalidOperationException("Unable to assign PublicKey to Account for test configuration.");
        }

        private sealed class StubSessionService : IWalletSessionService
        {
            public event Action Connected
            {
                add { }
                remove { }
            }

            public event Action Disconnected
            {
                add { }
                remove { }
            }

            public event Action<string> ConnectionFailed
            {
                add { }
                remove { }
            }

            public event Action<string> WalletVerified
            {
                add { }
                remove { }
            }

            public event Action<ulong> SolBalanceChanged
            {
                add { }
                remove { }
            }

            public event Action<bool> StreamingHealthChanged
            {
                add { }
                remove { }
            }

            public PublicKey CurrentPublicKey { get; set; }

            public Account CurrentAccount => WalletBase?.Account;

            public WalletBase WalletBase { get; set; }

            public bool IsWalletConnected => WalletBase != null;

            public bool IsWalletVerified { get; set; }

            public bool IsStreamingDegraded => false;

            public bool SimulatePaymentSuccess => true;

            public Task InitializeAsync() => Task.CompletedTask;

            public Task<bool> ConnectAsync() => Task.FromResult(IsWalletConnected);

            public Task DisconnectAsync() => Task.CompletedTask;

            public Task ShutdownAsync() => Task.CompletedTask;

            public void RefreshVerificationStatus()
            {
            }

            public void MarkWalletVerified(string publicKey)
            {
                IsWalletVerified = true;
            }

            public void ClearVerificationStatus()
            {
                IsWalletVerified = false;
            }
        }

        private sealed class TestWallet : WalletBase
        {
            public TestWallet() : base(RpcCluster.DevNet, autoConnectOnStartup: false)
            {
            }

            public void AssignAccount(Account account)
            {
                Account = account;
            }

            protected override Task<Account> _Login(string password = null)
            {
                return Task.FromResult(Account);
            }

            protected override Task<Account> _CreateAccount(string mnemonic = null, string password = null)
            {
                return Task.FromResult(Account);
            }

            protected override Task<Transaction> _SignTransaction(Transaction transaction)
            {
                return Task.FromResult(transaction);
            }

            protected override Task<Transaction[]> _SignAllTransactions(Transaction[] transactions)
            {
                return Task.FromResult(transactions);
            }

            public override Task<byte[]> SignMessage(byte[] message)
            {
                return Task.FromResult(Array.Empty<byte>());
            }
        }
    }
}

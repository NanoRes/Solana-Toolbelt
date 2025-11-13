using System;
using System.Numerics;
using System.Reflection;
using Solana.Unity.Metaplex.NFT.Library;

namespace Solana.Unity.Toolbelt
{
    public enum RpcCommitment
    {
        Processed,
        Confirmed,
        Finalized
    }

    public sealed class TransactionResult
    {
        public TransactionResult(bool success, string signature, string error)
        {
            Success = success;
            Signature = signature;
            Error = error;
        }

        public bool Success { get; }
        public string Signature { get; }
        public string Error { get; }
    }

    public sealed class TokenBalance
    {
        public TokenBalance(bool success, decimal? uiAmount, string uiAmountString, string error)
        {
            Success = success;
            UiAmount = uiAmount;
            UiAmountString = uiAmountString;
            Error = error;
        }

        public bool Success { get; }
        public decimal? UiAmount { get; }
        public string UiAmountString { get; }
        public string Error { get; }
    }

    public sealed class TokenAccountSnapshot
    {
        public TokenAccountSnapshot(string accountAddress, string mint, ulong rawAmount)
        {
            AccountAddress = accountAddress;
            Mint = mint;
            RawAmount = rawAmount;
        }

        public string AccountAddress { get; }
        public string Mint { get; }
        public ulong RawAmount { get; }

        public bool HasBalance => RawAmount > 0;
    }

    public sealed class TokenMintDetails
    {
        public TokenMintDetails(bool success, int? decimals, string mintAuthority, string error)
        {
            Success = success;
            Decimals = decimals;
            MintAuthority = mintAuthority;
            Error = error;
        }

        public bool Success { get; }
        public int? Decimals { get; }
        public string MintAuthority { get; }
        public string Error { get; }
    }

    public sealed class NftUriResult
    {
        public NftUriResult(bool success, string uri, string error)
        {
            Success = success;
            Uri = uri;
            Error = error;
        }

        public bool Success { get; }
        public string Uri { get; }
        public string Error { get; }
    }

    public sealed class MetadataAccountResult
    {
        public MetadataAccountResult(bool success, MetadataAccount account, string error)
        {
            Success = success;
            MetadataAccount = account;
            Error = error;
        }

        public bool Success { get; }
        public MetadataAccount MetadataAccount { get; }
        public string Error { get; }

        public string CollectionMint
        {
            get
            {
                var metadata = MetadataAccount?.metadata;
                if (metadata == null)
                {
                    return null;
                }

                return MetaplexMetadataUtility.GetCollectionKey(metadata);
            }
        }
    }

    public sealed class BundlrFundingSnapshot
    {
        public BundlrFundingSnapshot(string walletAddress, ulong walletLamports, BigInteger bundlrBalanceAtomic, ulong fundingFeeLamports)
        {
            WalletAddress = walletAddress;
            WalletLamports = walletLamports;
            BundlrBalanceAtomic = bundlrBalanceAtomic;
            FundingFeeLamports = fundingFeeLamports;
        }

        public string WalletAddress { get; }
        public ulong WalletLamports { get; }
        public BigInteger BundlrBalanceAtomic { get; }
        public ulong FundingFeeLamports { get; }
    }

    public sealed class BundlrTopUpResult
    {
        public BundlrTopUpResult(bool success, string signature, string error)
        {
            Success = success;
            Signature = signature;
            Error = error;
        }

        public bool Success { get; }
        public string Signature { get; }
        public string Error { get; }
    }

    public sealed class TransactionConfirmationResult
    {
        public TransactionConfirmationResult(bool success, string error)
        {
            Success = success;
            Error = error;
        }

        public bool Success { get; }
        public string Error { get; }
    }

    public static class MetaplexMetadataUtility
    {
        public static string GetCollectionKey(OnChainData metadata)
        {
            if (metadata == null)
            {
                return null;
            }

            var fromCollection = NormalizeKey(GetMemberValue(metadata, "collection"));
            if (!string.IsNullOrWhiteSpace(fromCollection))
            {
                return fromCollection;
            }

            return NormalizeKey(GetMemberValue(metadata, "collectionLink"));
        }

        public static bool IsCollectionVerified(OnChainData metadata)
        {
            if (metadata == null)
            {
                return false;
            }

            var collection = GetMemberValue(metadata, "collection");
            if (collection == null)
            {
                return false;
            }

            var verified = GetMemberValue(collection, "verified");
            return verified is bool flag && flag;
        }

        private static string NormalizeKey(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is string str)
            {
                return string.IsNullOrWhiteSpace(str) ? null : str.Trim();
            }

            var nested = GetMemberValue(value, "key") ?? GetMemberValue(value, "Key");
            if (nested != null && !ReferenceEquals(nested, value))
            {
                return NormalizeKey(nested);
            }

            var toString = value.ToString();
            if (!string.IsNullOrWhiteSpace(toString) &&
                !string.Equals(toString, value.GetType().FullName, StringComparison.Ordinal))
            {
                return toString.Trim();
            }

            return null;
        }

        private static object GetMemberValue(object instance, string memberName)
        {
            if (instance == null || string.IsNullOrEmpty(memberName))
            {
                return null;
            }

            const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

            var type = instance.GetType();
            var property = type.GetProperty(memberName, Flags);
            if (property != null)
            {
                return property.GetValue(instance);
            }

            var field = type.GetField(memberName, Flags);
            if (field != null)
            {
                return field.GetValue(instance);
            }

            return null;
        }
    }
}

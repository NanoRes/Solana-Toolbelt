using System;
using System.Collections.Generic;
using Solana.Unity.SDK;

namespace Solana.Unity.Toolbelt
{
    public static class MintSignerUtility
    {
        public static HashSet<string> GatherAvailableSignerPublicKeys(IToolbeltServiceProvider serviceProvider)
        {
            var signerKeys = new HashSet<string>(StringComparer.Ordinal);

            if (serviceProvider?.WalletService != null)
            {
                AddIfNotEmpty(signerKeys, serviceProvider.WalletService.CurrentPublicKey);
            }

            var sessionWallet = SessionWallet.Instance;
            if (sessionWallet?.Account?.PublicKey != null)
            {
                AddIfNotEmpty(signerKeys, sessionWallet.Account.PublicKey.Key);
            }

            var externalAuthority = SessionWallet.ExternalAuthorityPublicKey;
            if (externalAuthority != null)
            {
                AddIfNotEmpty(signerKeys, externalAuthority.Key);
            }

            return signerKeys;
        }

        public static List<OwnerGovernedAssetLedgerCreator> EnforceSignerVerification(
            IEnumerable<OwnerGovernedAssetLedgerCreator> creators,
            ISet<string> signerPublicKeys)
        {
            if (creators == null)
            {
                throw new ArgumentNullException(nameof(creators));
            }

            var sanitizedCreators = new List<OwnerGovernedAssetLedgerCreator>();

            foreach (var creator in creators)
            {
                if (creator == null)
                {
                    throw new ArgumentException("Creator entries cannot be null.", nameof(creators));
                }

                string address = creator.Address?.Key;
                bool isSigner = !string.IsNullOrWhiteSpace(address) &&
                                signerPublicKeys != null &&
                                signerPublicKeys.Contains(address);

                sanitizedCreators.Add(new OwnerGovernedAssetLedgerCreator(
                    creator.Address,
                    creator.Verified && isSigner,
                    creator.Share));
            }

            return sanitizedCreators;
        }

        private static void AddIfNotEmpty(ISet<string> set, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                set.Add(value);
            }
        }
    }
}

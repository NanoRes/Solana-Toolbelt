using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Org.BouncyCastle.Math.EC.Rfc8032;
using UnityEngine;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Centralised helper that manages the Bundlr signer private key lifecycle.
    /// Ensures a deterministic key is generated per connected wallet and
    /// persisted so uploads can share the same Bundlr account.
    /// </summary>
    public static class BundlrWalletKeyStore
    {
        private const string BundlrPrivateKeyPrefPrefix = "TokenToss.Bundlr.Signer";
        private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

        /// <summary>
        /// Ensure the provided uploader has an active signer by loading the
        /// persisted private key for the current wallet or generating a new one.
        /// </summary>
        public static Task EnsureBundlrSignerAsync(BundlrUploader bundlrUploader, string walletAddress)
        {
            if (bundlrUploader == null)
                throw new ArgumentNullException(nameof(bundlrUploader));

            string existingAddress = bundlrUploader.GetUploaderWalletAddress();
            if (!string.IsNullOrWhiteSpace(existingAddress))
                return Task.CompletedTask;

            string persistedKey = TryLoadBundlrPrivateKey(walletAddress);
            if (!string.IsNullOrWhiteSpace(persistedKey))
            {
                bundlrUploader.SetPrivateKey(persistedKey);
                if (!string.IsNullOrWhiteSpace(bundlrUploader.GetUploaderWalletAddress()))
                    return Task.CompletedTask;

                DeleteBundlrPrivateKey(walletAddress);
            }

            string generatedKey = GenerateBundlrPrivateKey();
            bundlrUploader.SetPrivateKey(generatedKey);

            string uploaderWallet = bundlrUploader.GetUploaderWalletAddress();
            if (string.IsNullOrWhiteSpace(uploaderWallet))
                throw new InvalidOperationException("Failed to initialise Bundlr signer for uploads.");

            SaveBundlrPrivateKey(walletAddress, generatedKey);
            return Task.CompletedTask;
        }

        public static string TryLoadBundlrPrivateKey(string walletAddress)
        {
            string prefKey = BuildBundlrPrefKey(walletAddress);
            return PlayerPrefs.GetString(prefKey, string.Empty);
        }

        public static void SaveBundlrPrivateKey(string walletAddress, string privateKey)
        {
            if (string.IsNullOrWhiteSpace(privateKey))
                return;

            string prefKey = BuildBundlrPrefKey(walletAddress);
            PlayerPrefs.SetString(prefKey, privateKey);
            PlayerPrefs.Save();
        }

        public static void DeleteBundlrPrivateKey(string walletAddress)
        {
            string prefKey = BuildBundlrPrefKey(walletAddress);
            if (!PlayerPrefs.HasKey(prefKey))
                return;

            PlayerPrefs.DeleteKey(prefKey);
            PlayerPrefs.Save();
        }

        private static string BuildBundlrPrefKey(string walletAddress)
        {
            if (string.IsNullOrWhiteSpace(walletAddress))
                return BundlrPrivateKeyPrefPrefix;

            return $"{BundlrPrivateKeyPrefPrefix}.{walletAddress}";
        }

        private static string GenerateBundlrPrivateKey()
        {
            var privateKey = new byte[32];
            RandomNumberGenerator.Fill(privateKey);

            var publicKey = new byte[32];
            Ed25519.GeneratePublicKey(privateKey, 0, publicKey, 0);

            var combined = new byte[64];
            Buffer.BlockCopy(privateKey, 0, combined, 0, privateKey.Length);
            Buffer.BlockCopy(publicKey, 0, combined, privateKey.Length, publicKey.Length);

            return EncodeBase58(combined);
        }

        private static string EncodeBase58(byte[] data)
        {
            if (data == null || data.Length == 0)
                return string.Empty;

            int zeros = 0;
            while (zeros < data.Length && data[zeros] == 0)
                zeros++;

            var input = new byte[data.Length];
            Buffer.BlockCopy(data, 0, input, 0, data.Length);

            var encoded = new char[data.Length * 2];
            int outputStart = encoded.Length;
            int inputStart = zeros;

            while (inputStart < input.Length)
            {
                int remainder = 0;
                for (int i = inputStart; i < input.Length; i++)
                {
                    int value = input[i] & 0xFF;
                    int temp = remainder * 256 + value;
                    input[i] = (byte)(temp / 58);
                    remainder = temp % 58;
                }

                encoded[--outputStart] = Base58Alphabet[remainder];
                while (inputStart < input.Length && input[inputStart] == 0)
                    inputStart++;
            }

            while (zeros-- > 0)
                encoded[--outputStart] = '1';

            return new string(encoded, outputStart, encoded.Length - outputStart);
        }
    }
}

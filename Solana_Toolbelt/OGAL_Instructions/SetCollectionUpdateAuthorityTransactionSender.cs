using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Solana.Unity.Rpc;
using Solana.Unity.Rpc.Builders;
using Solana.Unity.Rpc.Core.Http;
using Solana.Unity.Rpc.Models;
using Solana.Unity.Rpc.Types;
using Solana.Unity.Wallet;
using UnityEngine;

/// <summary>
/// Sends a rotate_collection_authority instruction to the configured program using the deployer wallet as fee payer.
/// Attach this to a GameObject in an empty scene to run the transaction when the scene starts.
/// </summary>
public class SetCollectionUpdateAuthorityTransactionSender : MonoBehaviour
{
    private const string DefaultMainnetRpcUrl = "https://alien-orbital-sun.solana-mainnet.quiknode.pro/7817ec8194dbc40eb1f1e123afd98d67e86fbfec/";
    private const string DefaultPrivateKeyEnvironmentVariable = "DEPLOYER_PRIVATE_KEY";
    private static readonly byte[] ConfigSeed = Encoding.UTF8.GetBytes("config");
    private static readonly byte[] AuthSeed = Encoding.UTF8.GetBytes("auth");
    private static readonly byte[] MetadataSeed = Encoding.UTF8.GetBytes("metadata");
    private static readonly byte[] RotateCollectionAuthorityDiscriminator =
        CreateAnchorDiscriminator("global:rotate_collection_authority");
    private static readonly byte[] LegacyRotateCollectionAuthorityDiscriminator =
        CreateAnchorDiscriminator("global::rotate_collection_authority");
    private static readonly byte[] ExpectedRotateCollectionAuthorityDiscriminator =
    {
        127, 21, 205, 57, 21, 40, 136, 55
    };

    static SetCollectionUpdateAuthorityTransactionSender()
    {
        if (!AreEqual(
                RotateCollectionAuthorityDiscriminator,
                ExpectedRotateCollectionAuthorityDiscriminator))
        {
            Debug.LogError(
                "rotate_collection_authority discriminator mismatch. Ensure the Anchor build artifacts are up to date.");
        }
    }
    private static readonly PublicKey TokenMetadataProgramId = new PublicKey("metaqbxxUerdq28cj1RbAWkYQm3ybzjb6a8bt518x1s");

    [Header("Solana Settings")]
    [Tooltip("Optional override for the RPC endpoint. Defaults to mainnet-beta if left empty.")]
    [SerializeField] private string rpcUrl = DefaultMainnetRpcUrl;

    [Tooltip("Base58 encoded private key for the authority wallet. If left empty the value will be read from the DEPLOYER_PRIVATE_KEY environment variable.")]
    [SerializeField] private string authorityPrivateKey;

    [Tooltip("Public key that corresponds to the provided authority private key. Defaults to the studio dev wallet.")]
    [SerializeField] private string authorityPublicKey = "E5mQ27muTebiYaohBsdsCwrvPN3MVoRmECFtL4A5Sx9q";

    [Header("Program Settings")]
    [SerializeField] private string programId = "GwMpopxNkDYsnucBRPf47QSEsEzA3rS1o6ioMX78hgqx";

    [Tooltip("Namespace that defines the registry configuration. Defaults to the live mainnet namespace.")]
    [SerializeField] private string namespacePublicKey = "3Bc5ARkDGM2ZdAe8EjwHMmNrXvpSzQVcPug7MSp4Qhbw";

    [Tooltip("Mint address for the collection whose authority should be rotated to the new authority.")]
    [SerializeField] private string collectionMintPublicKey = "EhULHuQtpaKUZSdv1kQR7XwYGRfEaU8b1Y7JkbFGQHxW";

    [Tooltip("Optional safety check. When set, the derived config PDA must match this value before the transaction is sent.")]
    [SerializeField] private string expectedConfigPdaPublicKey = "5bhVoogdhY5VYuLuUuMXaiNrvP4zbmP1wNWstUUvmiF5";

    [Tooltip("Optional safety check for the mint authority PDA derived from the namespace.")]
    [SerializeField] private string expectedMintAuthorityPdaPublicKey = "G7skWhSjK6oskMKMuCbVuRQSVvrhc1VN1nQYLHR8ewL5";

    [Tooltip("Optional safety check for the collection metadata PDA derived from the collection mint.")]
    [SerializeField] private string expectedCollectionMetadataPdaPublicKey = string.Empty;

    [Tooltip("Optional override for the new collection authority public key. Defaults to the authority wallet public key.")]
    [SerializeField] private string newCollectionAuthorityPublicKey = string.Empty;

    [Tooltip("Automatically send the transaction when the scene loads.")]
    [SerializeField] private bool sendOnStart = true;

    private void OnEnable()
    {
        if (sendOnStart)
        {
            StartTheTransaction();
        }
    }

    private async void StartTheTransaction()
    {
        try
        {
            await SendTransactionAsync();
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to send rotate_collection_authority transaction: {ex.Message}\n{ex}");
        }
    }

    /// <summary>
    /// Sends the rotate_collection_authority transaction using the configured RPC endpoint and authority wallet.
    /// </summary>
    public async Task<string> SendTransactionAsync()
    {
        var client = ClientFactory.GetClient(GetRpcUrl());
        var authorityAccount = LoadAuthorityAccount();

        var programPublicKey = ParsePublicKey("program ID", programId);
        var authorityPublicKeyKey = authorityAccount.PublicKey;
        var namespaceKey = ParsePublicKey("namespace", namespacePublicKey);
        var collectionMintKey = ParsePublicKey("collection mint", collectionMintPublicKey);

        var configPda = DeriveRegistryConfigPda(namespaceKey, programPublicKey);
        var authPda = DeriveMintAuthorityPda(configPda, programPublicKey);
        var metadataPda = DeriveCollectionMetadataPda(collectionMintKey);
        ValidateDerivedPdas(configPda, authPda, metadataPda);
        var newAuthorityKey = ResolveNewCollectionAuthority(authorityAccount);

        var accounts = new List<AccountMeta>
        {
            AccountMeta.ReadOnly(authorityPublicKeyKey, true),
            AccountMeta.Writable(configPda, false),
            AccountMeta.ReadOnly(authPda, false),
            AccountMeta.Writable(metadataPda, false),
            AccountMeta.ReadOnly(collectionMintKey, false),
            AccountMeta.ReadOnly(TokenMetadataProgramId, false)
        };

        var discriminators = new List<(byte[] bytes, bool legacy)>
        {
            (RotateCollectionAuthorityDiscriminator, false),
            (LegacyRotateCollectionAuthorityDiscriminator, true)
        };

        string lastError = null;
        foreach (var (bytes, legacy) in discriminators)
        {
            if (legacy)
            {
                Debug.LogWarning(
                    "rotate_collection_authority was rejected with the canonical discriminator. " +
                    "Retrying with the legacy double-colon hash. Redeploy the program " +
                    "compiled with the patched Anchor version to adopt the canonical discriminator.");
            }

            var instructionData = BuildInstructionData(newAuthorityKey, bytes);
            var instruction = new TransactionInstruction
            {
                ProgramId = programPublicKey,
                Keys = accounts,
                Data = instructionData
            };

            var recentBlockHash = await FetchLatestBlockHashAsync(client).ConfigureAwait(false);
            var txBuilder = new TransactionBuilder()
                .SetRecentBlockHash(recentBlockHash)
                .SetFeePayer(authorityAccount)
                .AddInstruction(instruction);

            var signedTransaction = txBuilder.Build(new List<Account> { authorityAccount });
            var encodedTransaction = Convert.ToBase64String(signedTransaction);

            RequestResult<string> sendResult;
            try
            {
                sendResult = await client.SendAndConfirmTransactionAsync(
                    encodedTransaction,
                    skipPreflight: false,
                    preFlightCommitment: Commitment.Confirmed,
                    commitment: Commitment.Confirmed).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("RPC send failed", ex);
            }

            if (sendResult != null && sendResult.WasSuccessful && !string.IsNullOrEmpty(sendResult.Result))
            {
                if (legacy)
                {
                    Debug.LogWarning(
                        "rotate_collection_authority succeeded using the legacy discriminator. " +
                        "Redeploy the program built with the patched Anchor version to stop using the fallback path.");
                }

                Debug.Log($"rotate_collection_authority transaction signature: {sendResult.Result}");
                return sendResult.Result;
            }

            var reason = sendResult?.Reason ?? "Unknown error";
            var rawResponse = sendResult?.RawRpcResponse ?? string.Empty;

            if (!legacy && IsFallbackError(reason, rawResponse))
            {
                Debug.LogWarning(
                    "rotate_collection_authority transaction simulation failed with InstructionFallbackNotFound. " +
                    "Attempting to resend with the legacy discriminator.");
                continue;
            }

            if (!string.IsNullOrEmpty(rawResponse))
            {
                reason = $"{reason}. Raw RPC response: {rawResponse}";
            }

            lastError = reason;
            break;
        }

        throw new InvalidOperationException(
            $"Transaction failed: {lastError ?? "rotate_collection_authority was rejected with both discriminators."}");
    }

    private string GetRpcUrl()
    {
        if (!string.IsNullOrWhiteSpace(rpcUrl))
        {
            return rpcUrl.Trim();
        }

        return DefaultMainnetRpcUrl;
    }

    private Account LoadAuthorityAccount()
    {
        string privateKey = authorityPrivateKey;
        if (string.IsNullOrWhiteSpace(privateKey))
        {
            privateKey = Environment.GetEnvironmentVariable(DefaultPrivateKeyEnvironmentVariable);
        }

        if (string.IsNullOrWhiteSpace(privateKey))
        {
            throw new InvalidOperationException(
                $"Authority private key not provided. Set it in the inspector or via the {DefaultPrivateKeyEnvironmentVariable} environment variable.");
        }

        var account = new Account(privateKey, authorityPublicKey);
        if (!string.IsNullOrWhiteSpace(authorityPublicKey) &&
            !string.Equals(account.PublicKey.Key, authorityPublicKey, StringComparison.Ordinal))
        {
            Debug.LogWarning($"Authority public key ({authorityPublicKey}) does not match loaded wallet ({account.PublicKey.Key}). Using wallet public key.");
            authorityPublicKey = account.PublicKey.Key;
        }

        return account;
    }

    private static async Task<string> FetchLatestBlockHashAsync(IRpcClient client)
    {
        var blockHashResult = await client.GetLatestBlockHashAsync(Commitment.Confirmed).ConfigureAwait(false);
        if (blockHashResult == null || !blockHashResult.WasSuccessful || blockHashResult.Result?.Value == null)
        {
            var reason = blockHashResult?.Reason ?? "Unknown error";
            throw new InvalidOperationException($"Unable to fetch recent block hash: {reason}");
        }

        return blockHashResult.Result.Value.Blockhash;
    }

    private void ValidateDerivedPdas(PublicKey derivedConfig, PublicKey derivedAuth, PublicKey derivedMetadata)
    {
        if (!string.IsNullOrWhiteSpace(expectedConfigPdaPublicKey))
        {
            var expectedConfig = ParsePublicKey("expected config PDA", expectedConfigPdaPublicKey);
            if (!string.Equals(derivedConfig.Key, expectedConfig.Key, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Derived config PDA ({derivedConfig.Key}) does not match the expected value ({expectedConfig.Key}). " +
                    "Update the namespace or expected PDA settings before sending the transaction.");
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedMintAuthorityPdaPublicKey))
        {
            var expectedAuth = ParsePublicKey("expected mint authority PDA", expectedMintAuthorityPdaPublicKey);
            if (!string.Equals(derivedAuth.Key, expectedAuth.Key, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Derived mint authority PDA ({derivedAuth.Key}) does not match the expected value ({expectedAuth.Key}). " +
                    "Update the namespace or expected PDA settings before sending the transaction.");
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedCollectionMetadataPdaPublicKey))
        {
            var expectedMetadata = ParsePublicKey("expected collection metadata PDA", expectedCollectionMetadataPdaPublicKey);
            if (!string.Equals(derivedMetadata.Key, expectedMetadata.Key, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Derived collection metadata PDA ({derivedMetadata.Key}) does not match the expected value ({expectedMetadata.Key}). " +
                    "Update the collection mint or expected PDA settings before sending the transaction.");
            }
        }

        Debug.Log($"Derived config PDA: {derivedConfig.Key}\nDerived mint authority PDA: {derivedAuth.Key}\nDerived collection metadata PDA: {derivedMetadata.Key}");
    }

    private PublicKey ResolveNewCollectionAuthority(Account authorityAccount)
    {
        if (authorityAccount == null)
        {
            throw new ArgumentNullException(nameof(authorityAccount));
        }

        if (string.IsNullOrWhiteSpace(newCollectionAuthorityPublicKey))
        {
            return authorityAccount.PublicKey;
        }

        return ParsePublicKey("new collection authority", newCollectionAuthorityPublicKey);
    }

    private static byte[] BuildInstructionData(PublicKey newCollectionAuthority, byte[] discriminator)
    {
        if (newCollectionAuthority == null)
        {
            throw new ArgumentNullException(nameof(newCollectionAuthority));
        }

        if (discriminator == null)
        {
            throw new ArgumentNullException(nameof(discriminator));
        }

        var data = new byte[discriminator.Length + newCollectionAuthority.KeyBytes.Length];
        Buffer.BlockCopy(discriminator, 0, data, 0, discriminator.Length);
        Buffer.BlockCopy(newCollectionAuthority.KeyBytes, 0, data, discriminator.Length, newCollectionAuthority.KeyBytes.Length);
        return data;
    }

    private static bool IsFallbackError(string reason, string rawResponse)
    {
        return ContainsFallbackMarker(reason) || ContainsFallbackMarker(rawResponse);
    }

    private static bool ContainsFallbackMarker(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return value.IndexOf("InstructionFallbackNotFound", StringComparison.OrdinalIgnoreCase) >= 0 ||
               value.IndexOf("custom program error: 0x65", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool AreEqual(byte[] left, byte[] right)
    {
        if (left == null || right == null || left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static PublicKey DeriveRegistryConfigPda(PublicKey namespaceKey, PublicKey programId)
    {
        var seeds = new List<byte[]>
        {
            ConfigSeed,
            namespaceKey.KeyBytes
        };

        if (!PublicKey.TryFindProgramAddress(seeds, programId, out var pda, out _))
        {
            throw new InvalidOperationException("Unable to derive registry config PDA for namespace.");
        }

        return pda;
    }

    private static PublicKey DeriveMintAuthorityPda(PublicKey configPda, PublicKey programId)
    {
        var seeds = new List<byte[]>
        {
            AuthSeed,
            configPda.KeyBytes
        };

        if (!PublicKey.TryFindProgramAddress(seeds, programId, out var pda, out _))
        {
            throw new InvalidOperationException("Unable to derive mint authority PDA for namespace config.");
        }

        return pda;
    }

    private static PublicKey DeriveCollectionMetadataPda(PublicKey mint)
    {
        var seeds = new List<byte[]>
        {
            MetadataSeed,
            TokenMetadataProgramId.KeyBytes,
            mint.KeyBytes
        };

        if (!PublicKey.TryFindProgramAddress(seeds, TokenMetadataProgramId, out var pda, out _))
        {
            throw new InvalidOperationException("Unable to derive collection metadata PDA for mint.");
        }

        return pda;
    }

    private static byte[] CreateAnchorDiscriminator(string name)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(name));
        var discriminator = new byte[8];
        Buffer.BlockCopy(hash, 0, discriminator, 0, discriminator.Length);
        return discriminator;
    }

    private static PublicKey ParsePublicKey(string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"A {label} public key must be provided.");
        }

        try
        {
            return new PublicKey(value.Trim());
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unable to parse {label} public key.", ex);
        }
    }
}

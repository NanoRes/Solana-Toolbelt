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
using Solana.Unity.Programs;
using UnityEngine;

/// <summary>
/// Sends a migrate_config_namespace instruction to the configured program using the
/// authority wallet as both signer and payer.
/// Attach this to a GameObject in an empty scene to run the transaction when the scene starts.
/// </summary>
public class MigrateNamespaceTransactionSender : MonoBehaviour
{
    private const string DefaultMainnetRpcUrl = "https://alien-orbital-sun.solana-mainnet.quiknode.pro/7817ec8194dbc40eb1f1e123afd98d67e86fbfec/";
    private const string DefaultPrivateKeyEnvironmentVariable = "DEPLOYER_PRIVATE_KEY";

    private static readonly byte[] ConfigSeed = Encoding.UTF8.GetBytes("config");
    private static readonly byte[] AuthSeed = Encoding.UTF8.GetBytes("auth");
    private static readonly byte[] MigrateConfigNamespaceDiscriminator =
        CreateAnchorDiscriminator("global:migrate_config_namespace");

    [Header("Solana Settings")]
    [Tooltip("Optional override for the RPC endpoint. Defaults to mainnet-beta if left empty.")]
    [SerializeField] private string rpcUrl = DefaultMainnetRpcUrl;

    [Tooltip("Base58 encoded private key for the authority wallet. If left empty the value will be read from the DEPLOYER_PRIVATE_KEY environment variable.")]
    [SerializeField] private string authorityPrivateKey;

    [Tooltip("Public key that corresponds to the provided authority private key. Defaults to the studio dev wallet.")]
    [SerializeField] private string authorityPublicKey = "E5mQ27muTebiYaohBsdsCwrvPN3MVoRmECFtL4A5Sx9q";

    [Header("Program Settings")]
    [SerializeField] private string programId = "GwMpopxNkDYsnucBRPf47QSEsEzA3rS1o6ioMX78hgqx";

    [Tooltip("Namespace that defines the current registry configuration. Defaults to the live mainnet namespace.")]
    [SerializeField] private string currentNamespacePublicKey = "3Bc5ARkDGM2ZdAe8EjwHMmNrXvpSzQVcPug7MSp4Qhbw";

    [Tooltip("Namespace that should own the migrated configuration.")]
    [SerializeField] private string targetNamespacePublicKey = string.Empty;

    [Header("Optional Safety Checks")]
    [Tooltip("Expected PDA for the existing configuration account. Leave empty to skip validation.")]
    [SerializeField] private string expectedOldConfigPdaPublicKey = string.Empty;

    [Tooltip("Expected PDA for the existing mint-authority account. Leave empty to skip validation.")]
    [SerializeField] private string expectedOldAuthPdaPublicKey = string.Empty;

    [Tooltip("Expected PDA for the migrated configuration account. Leave empty to skip validation.")]
    [SerializeField] private string expectedNewConfigPdaPublicKey = string.Empty;

    [Tooltip("Expected PDA for the migrated mint-authority account. Leave empty to skip validation.")]
    [SerializeField] private string expectedNewAuthPdaPublicKey = string.Empty;

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
            Debug.LogError($"Failed to send migrate_config_namespace transaction: {ex.Message}\\n{ex}");
        }
    }

    /// <summary>
    /// Sends the migrate_config_namespace transaction using the configured RPC endpoint and authority wallet.
    /// </summary>
    public async Task<string> SendTransactionAsync()
    {
        var client = ClientFactory.GetClient(GetRpcUrl());
        var authorityAccount = LoadAuthorityAccount();

        var programPublicKey = ParsePublicKey("program ID", programId);
        var authorityPublicKeyKey = authorityAccount.PublicKey;
        var currentNamespaceKey = ParsePublicKey("current namespace", currentNamespacePublicKey);
        var targetNamespaceKey = ParsePublicKey("target namespace", targetNamespacePublicKey);

        var oldConfigPda = DeriveRegistryConfigPda(currentNamespaceKey, programPublicKey);
        var newConfigPda = DeriveRegistryConfigPda(targetNamespaceKey, programPublicKey);
        var oldAuthPda = DeriveMintAuthorityPda(oldConfigPda, programPublicKey);
        var newAuthPda = DeriveMintAuthorityPda(newConfigPda, programPublicKey);
        ValidateDerivedPdas(oldConfigPda, oldAuthPda, newConfigPda, newAuthPda);

        var instructionData = BuildInstructionData(targetNamespaceKey);
        var instruction = BuildInstruction(
            programPublicKey,
            authorityPublicKeyKey,
            oldConfigPda,
            newConfigPda,
            oldAuthPda,
            newAuthPda,
            instructionData);

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

        if (sendResult == null || !sendResult.WasSuccessful || string.IsNullOrEmpty(sendResult.Result))
        {
            var errorReason = sendResult?.Reason ?? "Unknown error";
            var rawResponse = sendResult?.RawRpcResponse;
            if (!string.IsNullOrEmpty(rawResponse))
            {
                errorReason = $"{errorReason}. Raw RPC response: {rawResponse}";
            }

            throw new InvalidOperationException($"Transaction failed: {errorReason}");
        }

        Debug.Log($"migrate_config_namespace({currentNamespaceKey} -> {targetNamespaceKey}) transaction signature: {sendResult.Result}");
        return sendResult.Result;
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
        return account;
    }

    private TransactionInstruction BuildInstruction(
        PublicKey programPublicKey,
        PublicKey authorityPublicKey,
        PublicKey oldConfigPda,
        PublicKey newConfigPda,
        PublicKey oldAuthPda,
        PublicKey newAuthPda,
        byte[] instructionData)
    {
        var accounts = new List<AccountMeta>
        {
            AccountMeta.Writable(authorityPublicKey, true),
            AccountMeta.Writable(oldConfigPda, false),
            AccountMeta.Writable(newConfigPda, false),
            AccountMeta.ReadOnly(oldAuthPda, false),
            AccountMeta.Writable(newAuthPda, false),
            AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false)
        };

        return new TransactionInstruction
        {
            ProgramId = programPublicKey,
            Keys = accounts,
            Data = instructionData
        };
    }

    private void ValidateDerivedPdas(
        PublicKey oldConfig,
        PublicKey oldAuth,
        PublicKey newConfig,
        PublicKey newAuth)
    {
        if (!string.IsNullOrWhiteSpace(expectedOldConfigPdaPublicKey))
        {
            var expectedOldConfig = ParsePublicKey("expected old config PDA", expectedOldConfigPdaPublicKey);
            if (!string.Equals(oldConfig.Key, expectedOldConfig.Key, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Derived old config PDA ({oldConfig.Key}) does not match the expected value ({expectedOldConfig.Key}). Update the namespace or expected PDA settings before sending the transaction.");
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedOldAuthPdaPublicKey))
        {
            var expectedOldAuth = ParsePublicKey("expected old auth PDA", expectedOldAuthPdaPublicKey);
            if (!string.Equals(oldAuth.Key, expectedOldAuth.Key, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Derived old auth PDA ({oldAuth.Key}) does not match the expected value ({expectedOldAuth.Key}). Update the namespace or expected PDA settings before sending the transaction.");
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedNewConfigPdaPublicKey))
        {
            var expectedNewConfig = ParsePublicKey("expected new config PDA", expectedNewConfigPdaPublicKey);
            if (!string.Equals(newConfig.Key, expectedNewConfig.Key, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Derived new config PDA ({newConfig.Key}) does not match the expected value ({expectedNewConfig.Key}). Update the namespace or expected PDA settings before sending the transaction.");
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedNewAuthPdaPublicKey))
        {
            var expectedNewAuth = ParsePublicKey("expected new auth PDA", expectedNewAuthPdaPublicKey);
            if (!string.Equals(newAuth.Key, expectedNewAuth.Key, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Derived new auth PDA ({newAuth.Key}) does not match the expected value ({expectedNewAuth.Key}). Update the namespace or expected PDA settings before sending the transaction.");
            }
        }

        Debug.Log(
            $"Derived PDAs\\n- Old Config: {oldConfig.Key}\\n- Old Auth: {oldAuth.Key}\\n- New Config: {newConfig.Key}\\n- New Auth: {newAuth.Key}");
    }

    private static byte[] BuildInstructionData(PublicKey namespaceKey)
    {
        var data = new byte[MigrateConfigNamespaceDiscriminator.Length + namespaceKey.KeyBytes.Length];
        Buffer.BlockCopy(MigrateConfigNamespaceDiscriminator, 0, data, 0, MigrateConfigNamespaceDiscriminator.Length);
        Buffer.BlockCopy(namespaceKey.KeyBytes, 0, data, MigrateConfigNamespaceDiscriminator.Length, namespaceKey.KeyBytes.Length);
        return data;
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

    private static byte[] CreateAnchorDiscriminator(string name)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(name));
        var result = new byte[8];
        Buffer.BlockCopy(hash, 0, result, 0, result.Length);
        return result;
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

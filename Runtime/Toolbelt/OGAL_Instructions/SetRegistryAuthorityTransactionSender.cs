using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Solana.Unity.Programs;
using Solana.Unity.Rpc;
using Solana.Unity.Rpc.Builders;
using Solana.Unity.Rpc.Core.Http;
using Solana.Unity.Rpc.Models;
using Solana.Unity.Rpc.Types;
using Solana.Unity.Wallet;
using UnityEngine;

/// <summary>
/// Sends a set_authority instruction to the configured Owner-Governed Asset Ledger program.
/// Attach this to a GameObject to rotate the config authority from the Unity inspector.
/// </summary>
public class SetRegistryAuthorityTransactionSender : MonoBehaviour
{
    private const string DefaultMainnetRpcUrl = "https://alien-orbital-sun.solana-mainnet.quiknode.pro/7817ec8194dbc40eb1f1e123afd98d67e86fbfec/";
    private const string DefaultPrivateKeyEnvironmentVariable = "DEPLOYER_PRIVATE_KEY";
    private static readonly byte[] ConfigSeed = Encoding.UTF8.GetBytes("config");
    private static readonly byte[] SetAuthorityDiscriminator = CreateAnchorDiscriminator("global:set_authority");

    [Header("Solana Settings")]
    [Tooltip("Optional override for the RPC endpoint. Defaults to mainnet-beta if left empty.")]
    [SerializeField] private string rpcUrl = DefaultMainnetRpcUrl;

    [Tooltip("Base58 encoded private key for the authority wallet. If left empty the value will be read from the DEPLOYER_PRIVATE_KEY environment variable.")]
    [SerializeField] private string authorityPrivateKey;

    [Tooltip("Public key that corresponds to the provided authority private key. Defaults to the studio dev wallet.")]
    [SerializeField] private string authorityPublicKey = "E5mQ27muTebiYaohBsdsCwrvPN3MVoRmECFtL4A5Sx9q";

    [Header("Program Settings")]
    [Tooltip("Program ID of the deployed Owner-Governed Asset Ledger program.")]
    [SerializeField] private string programId = "GwMpopxNkDYsnucBRPf47QSEsEzA3rS1o6ioMX78hgqx";

    [Tooltip("Namespace that defines the registry configuration. Ignored when an explicit config PDA override is supplied.")]
    [SerializeField] private string namespacePublicKey = "3Bc5ARkDGM2ZdAe8EjwHMmNrXvpSzQVcPug7MSp4Qhbw";

    [Tooltip("Optional override for the registry config PDA if you don't want to derive it from a namespace.")]
    [SerializeField] private string registryConfigOverride = string.Empty;

    [Tooltip("Optional safety check. When set, the derived config PDA must match this value before the transaction is sent.")]
    [SerializeField] private string expectedConfigPdaPublicKey = "5bhVoogdhY5VYuLuUuMXaiNrvP4zbmP1wNWstUUvmiF5";

    [Tooltip("Public key that should become the new config authority. Defaults to the authority wallet when left empty.")]
    [SerializeField] private string newAuthorityPublicKey = string.Empty;

    [Tooltip("Automatically send the transaction when the scene loads.")]
    [SerializeField] private bool sendOnStart = true;

    private void OnEnable()
    {
        if (sendOnStart)
        {
            StartTransaction();
        }
    }

    private async void StartTransaction()
    {
        try
        {
            await SendTransactionAsync();
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to send set_authority transaction: {ex.Message}\n{ex}");
        }
    }

    /// <summary>
    /// Sends the set_authority transaction using the configured RPC endpoint and authority wallet.
    /// </summary>
    public async Task<string> SendTransactionAsync()
    {
        var client = ClientFactory.GetClient(GetRpcUrl());
        var authorityAccount = LoadAuthorityAccount();

        var programPublicKey = ParsePublicKey("program ID", programId);
        var configPda = ResolveRegistryConfigPda(programPublicKey);
        ValidateExpectedConfig(configPda);
        var newAuthority = ResolveNewAuthority(authorityAccount);

        var accounts = new List<AccountMeta>
        {
            AccountMeta.ReadOnly(authorityAccount.PublicKey, true),
            AccountMeta.Writable(configPda, false)
        };

        var instruction = new TransactionInstruction
        {
            ProgramId = programPublicKey,
            Keys = accounts,
            Data = BuildInstructionData(newAuthority)
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

        if (sendResult == null || !sendResult.WasSuccessful || string.IsNullOrEmpty(sendResult.Result))
        {
            var reason = sendResult?.Reason ?? "Unknown error";
            var rawResponse = sendResult?.RawRpcResponse;
            if (!string.IsNullOrEmpty(rawResponse))
            {
                reason = $"{reason}. Raw RPC response: {rawResponse}";
            }

            throw new InvalidOperationException($"Transaction failed: {reason}");
        }

        Debug.Log($"set_authority transaction signature: {sendResult.Result}");
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
        if (!string.IsNullOrWhiteSpace(authorityPublicKey) &&
            !string.Equals(account.PublicKey.Key, authorityPublicKey, StringComparison.Ordinal))
        {
            Debug.LogWarning($"Authority public key ({authorityPublicKey}) does not match loaded wallet ({account.PublicKey.Key}). Using wallet public key.");
            authorityPublicKey = account.PublicKey.Key;
        }

        return account;
    }

    private PublicKey ResolveRegistryConfigPda(PublicKey programPublicKey)
    {
        if (!string.IsNullOrWhiteSpace(registryConfigOverride))
        {
            return ParsePublicKey("registry config override", registryConfigOverride);
        }

        var namespaceKey = ParsePublicKey("namespace", namespacePublicKey);
        return DeriveRegistryConfigPda(namespaceKey, programPublicKey);
    }

    private void ValidateExpectedConfig(PublicKey derivedConfig)
    {
        if (string.IsNullOrWhiteSpace(expectedConfigPdaPublicKey))
        {
            return;
        }

        var expectedConfig = ParsePublicKey("expected config PDA", expectedConfigPdaPublicKey);
        if (!string.Equals(derivedConfig.Key, expectedConfig.Key, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Derived config PDA ({derivedConfig.Key}) does not match the expected value ({expectedConfig.Key}). Update the settings before sending the transaction.");
        }
    }

    private PublicKey ResolveNewAuthority(Account authorityAccount)
    {
        if (authorityAccount == null)
        {
            throw new ArgumentNullException(nameof(authorityAccount));
        }

        if (string.IsNullOrWhiteSpace(newAuthorityPublicKey))
        {
            return authorityAccount.PublicKey;
        }

        return ParsePublicKey("new authority", newAuthorityPublicKey);
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

    private static PublicKey ParsePublicKey(string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing {label} setting.");
        }

        try
        {
            return new PublicKey(value);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unable to parse {label}: {value}", ex);
        }
    }

    private static PublicKey DeriveRegistryConfigPda(PublicKey namespaceKey, PublicKey programPublicKey)
    {
        var seeds = new List<byte[]>
        {
            ConfigSeed,
            namespaceKey.KeyBytes
        };

        if (!PublicKey.TryFindProgramAddress(seeds, programPublicKey, out var pda, out _))
        {
            throw new InvalidOperationException("Unable to derive registry config PDA.");
        }

        return pda;
    }

    private static byte[] BuildInstructionData(PublicKey newAuthority)
    {
        if (newAuthority == null)
        {
            throw new ArgumentNullException(nameof(newAuthority));
        }

        var data = new byte[SetAuthorityDiscriminator.Length + newAuthority.KeyBytes.Length];
        Buffer.BlockCopy(SetAuthorityDiscriminator, 0, data, 0, SetAuthorityDiscriminator.Length);
        Buffer.BlockCopy(newAuthority.KeyBytes, 0, data, SetAuthorityDiscriminator.Length, newAuthority.KeyBytes.Length);
        return data;
    }

    private static byte[] CreateAnchorDiscriminator(string name)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(name));
        var result = new byte[8];
        Array.Copy(hash, result, 8);
        return result;
    }
}

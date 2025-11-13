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
/// Sends an initialize(namespace) instruction to the configured program using the deployer wallet as fee payer.
/// Attach this to a GameObject in an empty scene to run the transaction when the scene starts.
/// </summary>
public class InitializeNamespaceTransactionSender : MonoBehaviour
{
    private const string DefaultMainnetRpcUrl = "https://alien-orbital-sun.solana-mainnet.quiknode.pro/7817ec8194dbc40eb1f1e123afd98d67e86fbfec/";
    private const string DefaultPrivateKeyEnvironmentVariable = "DEPLOYER_PRIVATE_KEY";
    private static readonly byte[] ConfigSeed = Encoding.UTF8.GetBytes("config");
    private static readonly byte[] AuthSeed = Encoding.UTF8.GetBytes("auth");
    private static readonly byte[] InitializeDiscriminator = CreateAnchorDiscriminator("global:initialize");

    [Header("Solana Settings")]
    [Tooltip("Optional override for the RPC endpoint. Defaults to mainnet-beta if left empty.")]
    [SerializeField] private string rpcUrl = DefaultMainnetRpcUrl;

    [Tooltip("Base58 encoded private key for the deployer wallet. If left empty the value will be read from the DEPLOYER_PRIVATE_KEY environment variable.")]
    [SerializeField] private string deployerPrivateKey;

    [Tooltip("Public key that corresponds to the provided deployer private key. Defaults to the studio deployer wallet.")]
    [SerializeField] private string deployerPublicKey = "E5mQ27muTebiYaohBsdsCwrvPN3MVoRmECFtL4A5Sx9q";

    [Header("Program Settings")]
    [SerializeField] private string programId = "GwMpopxNkDYsnucBRPf47QSEsEzA3rS1o6ioMX78hgqx";

    [Tooltip("Public key of the wallet that will cover rent and sign the transaction. Defaults to the studio's deployer wallet.")]
    [SerializeField] private string feePayerPubKey = "E5mQ27muTebiYaohBsdsCwrvPN3MVoRmECFtL4A5Sx9q";

    [Tooltip("Namespace that defines the registry configuration. Defaults to the live mainnet namespace.")]
    [SerializeField] private string namespacePublicKey = "3Bc5ARkDGM2ZdAe8EjwHMmNrXvpSzQVcPug7MSp4Qhbw";

    [Tooltip("Optional safety check. When set, the derived config PDA must match this value before the transaction is sent.")]
    [SerializeField] private string expectedConfigPdaPublicKey = "5bhVoogdhY5VYuLuUuMXaiNrvP4zbmP1wNWstUUvmiF5";

    [Tooltip("Optional safety check for the mint authority PDA derived from the namespace.")]
    [SerializeField] private string expectedMintAuthorityPdaPublicKey = "G7skWhSjK6oskMKMuCbVuRQSVvrhc1VN1nQYLHR8ewL5";

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
            Debug.LogError($"Failed to send initialize(namespace) transaction: {ex.Message}\n{ex}");
        }
    }

    /// <summary>
    /// Sends the initialize(namespace) transaction using the configured RPC endpoint and deployer wallet.
    /// </summary>
    public async Task<string> SendTransactionAsync()
    {
        var client = ClientFactory.GetClient(GetRpcUrl());
        var account = LoadDeployerAccount();

        var recentBlockHash = await FetchLatestBlockHashAsync(client).ConfigureAwait(false);
        var instruction = BuildInstruction(account);

        var txBuilder = new TransactionBuilder()
            .SetRecentBlockHash(recentBlockHash)
            .SetFeePayer(account)
            .AddInstruction(instruction);

        var signedTransaction = txBuilder.Build(new List<Account> { account });

        // QuickNode (and some other RPC providers) have been observed to return
        // "Unable to parse json" when a binary payload is submitted using the
        // byte[] overload. To avoid any serialization ambiguity we explicitly
        // base64-encode the transaction and use the string overload.
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

        Debug.Log($"initialize({namespacePublicKey}) transaction signature: {sendResult.Result}");
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

    private Account LoadDeployerAccount()
    {
        string privateKey = deployerPrivateKey;
        if (string.IsNullOrWhiteSpace(privateKey))
        {
            privateKey = Environment.GetEnvironmentVariable(DefaultPrivateKeyEnvironmentVariable);
        }

        if (string.IsNullOrWhiteSpace(privateKey))
        {
            throw new InvalidOperationException(
                $"Deployer private key not provided. Set it in the inspector or via the {DefaultPrivateKeyEnvironmentVariable} environment variable.");
        }

        var account = new Account(privateKey, deployerPublicKey);
        if (!string.IsNullOrWhiteSpace(feePayerPubKey) &&
            !string.Equals(account.PublicKey.Key, feePayerPubKey, StringComparison.Ordinal))
        {
            Debug.LogWarning($"Fee payer public key ({feePayerPubKey}) does not match loaded deployer wallet ({account.PublicKey.Key}). Using wallet public key.");
            feePayerPubKey = account.PublicKey.Key;
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

    private TransactionInstruction BuildInstruction(Account authorityAccount)
    {
        if (authorityAccount == null)
        {
            throw new ArgumentNullException(nameof(authorityAccount));
        }

        var programPublicKey = ParsePublicKey("program ID", programId);
        var authorityPublicKey = authorityAccount.PublicKey;
        var payerPublicKey = authorityPublicKey;
        var namespaceKey = ParsePublicKey("namespace", namespacePublicKey);

        var configPda = DeriveRegistryConfigPda(namespaceKey, programPublicKey);
        var authPda = DeriveMintAuthorityPda(configPda, programPublicKey);
        ValidateDerivedPdas(configPda, authPda);
        var instructionData = BuildInstructionData(namespaceKey);
        var systemProgramKey = SystemProgram.ProgramIdKey;

        var accounts = new List<AccountMeta>
        {
            AccountMeta.ReadOnly(authorityPublicKey, true),
            AccountMeta.Writable(payerPublicKey, true),
            AccountMeta.Writable(configPda, false),
            AccountMeta.Writable(authPda, false),
            AccountMeta.ReadOnly(systemProgramKey, false)
        };

        return new TransactionInstruction
        {
            ProgramId = programPublicKey,
            Keys = accounts,
            Data = instructionData
        };
    }

    private void ValidateDerivedPdas(PublicKey derivedConfig, PublicKey derivedAuth)
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

        Debug.Log($"Derived config PDA: {derivedConfig.Key}\nDerived mint authority PDA: {derivedAuth.Key}");
    }

    private static byte[] BuildInstructionData(PublicKey namespaceKey)
    {
        var data = new byte[InitializeDiscriminator.Length + namespaceKey.KeyBytes.Length];
        Buffer.BlockCopy(InitializeDiscriminator, 0, data, 0, InitializeDiscriminator.Length);
        Buffer.BlockCopy(namespaceKey.KeyBytes, 0, data, InitializeDiscriminator.Length, namespaceKey.KeyBytes.Length);
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

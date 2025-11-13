using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Solana.Unity.Wallet;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Represents a single creator entry for NFT metadata produced by the registry.
    /// </summary>
    public sealed class OwnerGovernedAssetLedgerCreator
    {
        public OwnerGovernedAssetLedgerCreator(PublicKey address, bool verified, byte share)
        {
            Address = address ?? throw new ArgumentNullException(nameof(address));
            Verified = verified;
            Share = share;
        }

        public OwnerGovernedAssetLedgerCreator(string address, bool verified, byte share)
            : this(string.IsNullOrWhiteSpace(address) ? throw new ArgumentNullException(nameof(address)) : new PublicKey(address), verified, share)
        {
        }

        public PublicKey Address { get; }
        public bool Verified { get; }
        public byte Share { get; }

        internal void Serialize(BinaryWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            BorshSerializationHelper.WritePublicKey(writer, Address);
            writer.Write(Verified);
            writer.Write(Share);
        }
    }

    /// <summary>
    /// Describes the metadata that will be provided to the on-chain registry
    /// program when minting a new object NFT.
    /// </summary>
    public sealed class OwnerGovernedAssetLedgerMintRequest
    {
        /// <summary>
        /// Maximum manifest URI length supported by the on-chain program.
        /// </summary>
        public const int MaxManifestUriLength = 128;

        /// <summary>
        /// Maximum metadata name length supported by the on-chain program.
        /// </summary>
        public const int MaxMetadataNameLength = 32;

        /// <summary>
        /// Maximum metadata symbol length supported by the on-chain program.
        /// </summary>
        public const int MaxMetadataSymbolLength = 10;

        /// <summary>
        /// Maximum number of creators supported by the on-chain program.
        /// </summary>
        public const int MaxCreatorCount = 5;

        public OwnerGovernedAssetLedgerMintRequest(
            ulong objectId,
            string manifestUri,
            string recipientPublicKey,
            byte[] manifestHash,
            string metadataName,
            string metadataSymbol,
            ushort sellerFeeBasisPoints,
            IEnumerable<OwnerGovernedAssetLedgerCreator> creators,
            string configNamespace = null,
            byte? configBump = null,
            byte? authBump = null,
            byte? manifestBump = null,
            byte? mintBump = null)
        {
            if (string.IsNullOrWhiteSpace(manifestUri))
                throw new ArgumentException("Manifest URI is required.", nameof(manifestUri));
            if (manifestUri.Length > MaxManifestUriLength)
                throw new ArgumentException($"Manifest URI cannot exceed {MaxManifestUriLength} characters.", nameof(manifestUri));
            if (string.IsNullOrWhiteSpace(recipientPublicKey))
                throw new ArgumentException("Recipient public key is required.", nameof(recipientPublicKey));
            if (manifestHash == null)
                throw new ArgumentNullException(nameof(manifestHash));
            if (manifestHash.Length != 32)
                throw new ArgumentException("Manifest hash must be exactly 32 bytes.", nameof(manifestHash));
            if (string.IsNullOrWhiteSpace(metadataName))
                throw new ArgumentException("Metadata name is required.", nameof(metadataName));
            if (metadataName.Length > MaxMetadataNameLength)
                throw new ArgumentException($"Metadata name cannot exceed {MaxMetadataNameLength} characters.", nameof(metadataName));
            if (sellerFeeBasisPoints > 10_000)
                throw new ArgumentOutOfRangeException(nameof(sellerFeeBasisPoints), "Seller fee basis points must be 10000 or less.");
            if (creators == null)
                throw new ArgumentNullException(nameof(creators));

            metadataSymbol ??= string.Empty;
            if (metadataSymbol.Length > MaxMetadataSymbolLength)
                throw new ArgumentException($"Metadata symbol cannot exceed {MaxMetadataSymbolLength} characters.", nameof(metadataSymbol));

            var creatorArray = creators
                .Select(c => c ?? throw new ArgumentException("Creator entries cannot be null.", nameof(creators)))
                .Select(c => new OwnerGovernedAssetLedgerCreator(c.Address, c.Verified, c.Share))
                .ToArray();

            if (creatorArray.Length == 0)
                throw new ArgumentException("At least one creator is required.", nameof(creators));
            if (creatorArray.Length > MaxCreatorCount)
                throw new ArgumentException($"A maximum of {MaxCreatorCount} creators can be included.", nameof(creators));

            int totalShare = creatorArray.Sum(c => c.Share);
            if (totalShare != 100)
                throw new ArgumentException("Creator shares must total 100.", nameof(creators));

            ObjectId = objectId;
            ManifestUri = manifestUri;
            RecipientPublicKey = recipientPublicKey;
            ManifestHash = new byte[32];
            Buffer.BlockCopy(manifestHash, 0, ManifestHash, 0, 32);
            MetadataName = metadataName;
            MetadataSymbol = metadataSymbol;
            SellerFeeBasisPoints = sellerFeeBasisPoints;
            Creators = creatorArray;
            ConfigNamespace = configNamespace;
            ConfigBump = configBump;
            AuthBump = authBump;
            ManifestBump = manifestBump;
            MintBump = mintBump;
        }

        public ulong ObjectId { get; }
        public string ManifestUri { get; }
        public string RecipientPublicKey { get; }
        public byte[] ManifestHash { get; }
        public string MetadataName { get; }
        public string MetadataSymbol { get; }
        public ushort SellerFeeBasisPoints { get; }
        public IReadOnlyList<OwnerGovernedAssetLedgerCreator> Creators { get; }
        public string ConfigNamespace { get; }
        public byte? ConfigBump { get; }
        public byte? AuthBump { get; }
        public byte? ManifestBump { get; }
        public byte? MintBump { get; }
    }

    /// <summary>
    /// Response returned by <see cref="OwnerGovernedAssetLedgerService"/> when minting a
    /// new object NFT succeeds.
    /// </summary>
    public sealed class OwnerGovernedAssetLedgerMintResult
    {
        public OwnerGovernedAssetLedgerMintResult(
            string mintAddress,
            string transactionSignature,
            bool creatorsDowngraded = false)
        {
            if (string.IsNullOrWhiteSpace(mintAddress))
                throw new ArgumentException("Mint address is required.", nameof(mintAddress));

            MintAddress = mintAddress;
            TransactionSignature = transactionSignature;
            CreatorsDowngraded = creatorsDowngraded;
        }

        public string MintAddress { get; }
        public string TransactionSignature { get; }
        /// <summary>
        /// Indicates whether any verified creators were downgraded to unverified because
        /// their mint signatures were unavailable.
        /// </summary>
        public bool CreatorsDowngraded { get; }
    }

    /// <summary>
    /// Parameters used when migrating the registry configuration to a new namespace.
    /// </summary>
    public sealed class OwnerGovernedAssetLedgerMigrationRequest
    {
        public OwnerGovernedAssetLedgerMigrationRequest(
            string newNamespace,
            string expectedOldConfigPda = null,
            string expectedOldAuthPda = null,
            string expectedNewConfigPda = null,
            string expectedNewAuthPda = null)
        {
            if (string.IsNullOrWhiteSpace(newNamespace))
                throw new ArgumentException("New namespace is required.", nameof(newNamespace));

            NewNamespace = newNamespace;
            ExpectedOldConfigPda = expectedOldConfigPda;
            ExpectedOldAuthPda = expectedOldAuthPda;
            ExpectedNewConfigPda = expectedNewConfigPda;
            ExpectedNewAuthPda = expectedNewAuthPda;
        }

        /// <summary>
        /// Public key for the namespace that should receive the migrated configuration.
        /// </summary>
        public string NewNamespace { get; }

        /// <summary>
        /// Optional PDA check for the existing configuration account.
        /// </summary>
        public string ExpectedOldConfigPda { get; }

        /// <summary>
        /// Optional PDA check for the existing mint-authority account.
        /// </summary>
        public string ExpectedOldAuthPda { get; }

        /// <summary>
        /// Optional PDA check for the new configuration account derived from <see cref="NewNamespace"/>.
        /// </summary>
        public string ExpectedNewConfigPda { get; }

        /// <summary>
        /// Optional PDA check for the new mint-authority account derived from the new configuration PDA.
        /// </summary>
        public string ExpectedNewAuthPda { get; }
    }

    /// <summary>
    /// Exception thrown by the registry service when a mint operation fails.
    /// Carries a user-facing message alongside the raw RPC error for logging.
    /// </summary>
    public sealed class OwnerGovernedAssetLedgerException : Exception
    {
        public OwnerGovernedAssetLedgerException(
            string userMessage,
            string rawMessage,
            Exception innerException = null,
            string debugContext = null)
            : base(string.IsNullOrWhiteSpace(rawMessage) ? userMessage : rawMessage, innerException)
        {
            if (string.IsNullOrWhiteSpace(userMessage))
                throw new ArgumentException("A user-friendly message is required.", nameof(userMessage));

            UserMessage = userMessage;
            RawMessage = rawMessage;
            DebugContext = debugContext;
        }

        /// <summary>
        /// Message suitable for displaying to end-users.
        /// </summary>
        public string UserMessage { get; }

        /// <summary>
        /// The unmodified error text returned by the RPC node, if available.
        /// </summary>
        public string RawMessage { get; }

        /// <summary>
        /// Optional machine-readable context that can help developers reproduce
        /// or debug the failure (for example, the serialized transaction).
        /// </summary>
        public string DebugContext { get; }
    }

    /// <summary>
    /// Representation of the on-chain configuration account governing the registry.
    /// </summary>
    public sealed class OwnerGovernedAssetLedgerConfigAccount
    {
        public PublicKey Address { get; private set; }
        public PublicKey Authority { get; private set; }
        public byte ConfigBump { get; private set; }
        public byte AuthBump { get; private set; }
        public ulong ObjectCount { get; private set; }
        public PublicKey Namespace { get; private set; }
        public bool Paused { get; private set; }

        public static OwnerGovernedAssetLedgerConfigAccount Deserialize(byte[] data, PublicKey accountAddress = null)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            using var ms = new MemoryStream(data, writable: false);
            using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

            var discriminator = reader.ReadBytes(8);
            if (discriminator.Length != 8)
                throw new InvalidOperationException("Account discriminator missing or truncated.");

            var authority = BorshSerializationHelper.ReadPublicKey(reader);
            byte configBump = reader.ReadByte();
            byte authBump = reader.ReadByte();
            ulong objectCount = reader.ReadUInt64();
            var namespaceKey = BorshSerializationHelper.ReadPublicKey(reader);
            bool paused = reader.ReadBoolean();

            return new OwnerGovernedAssetLedgerConfigAccount
            {
                Address = accountAddress,
                Authority = authority,
                ConfigBump = configBump,
                AuthBump = authBump,
                ObjectCount = objectCount,
                Namespace = namespaceKey,
                Paused = paused
            };
        }
    }

    /// <summary>
    /// Borsh-serializable arguments for the Anchor mint_object instruction.
    /// </summary>
    public sealed class MintObjectArgs
    {
        public ulong ObjectId { get; set; }
        public string ManifestUri { get; set; } = string.Empty;
        public byte[] ManifestHash { get; set; } = Array.Empty<byte>();
        public string MetadataName { get; set; } = string.Empty;
        public string MetadataSymbol { get; set; } = string.Empty;
        public ushort SellerFeeBasisPoints { get; set; }
        public IReadOnlyList<OwnerGovernedAssetLedgerCreator> Creators { get; set; } = Array.Empty<OwnerGovernedAssetLedgerCreator>();

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            writer.Write(ObjectId);
            BorshSerializationHelper.WriteString(writer, ManifestUri ?? string.Empty);
            if (ManifestHash == null || ManifestHash.Length != 32)
                throw new InvalidOperationException("Manifest hash must be exactly 32 bytes.");
            writer.Write(ManifestHash);
            BorshSerializationHelper.WriteString(writer, MetadataName ?? string.Empty);
            BorshSerializationHelper.WriteString(writer, MetadataSymbol ?? string.Empty);
            writer.Write(SellerFeeBasisPoints);
            if (Creators == null)
                throw new InvalidOperationException("Creators collection must be provided.");
            writer.Write((uint)Creators.Count);
            foreach (var creator in Creators)
            {
                if (creator == null)
                    throw new InvalidOperationException("Creators cannot contain null entries.");
                creator.Serialize(writer);
            }
            writer.Flush();
            return ms.ToArray();
        }
    }

    /// <summary>
    /// Borsh arguments for the update_manifest instruction.
    /// </summary>
    public sealed class UpdateManifestArgs
    {
        public byte[] ManifestHash { get; set; } = Array.Empty<byte>();
        public string MetadataUri { get; set; } = string.Empty;
        public bool IsActive { get; set; }

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

            if (ManifestHash == null || ManifestHash.Length != 32)
                throw new InvalidOperationException("Manifest hash must be exactly 32 bytes.");

            writer.Write(ManifestHash);
            BorshSerializationHelper.WriteString(writer, MetadataUri ?? string.Empty);
            writer.Write(IsActive);
            writer.Flush();
            return ms.ToArray();
        }
    }

    /// <summary>
    /// Representation of the on-chain object manifest account managed by the
    /// registry program.
    /// </summary>
    public sealed class ObjectManifestAccount
    {
        /// <summary>
        /// The public key of the account that stored this manifest data.
        /// </summary>
        public PublicKey Address { get; private set; }
        public PublicKey Config { get; private set; }
        public ulong ObjectId { get; private set; }
        public PublicKey Mint { get; private set; }
        public byte Bump { get; private set; }
        public byte MintBump { get; private set; }
        public bool IsActive { get; private set; }
        public bool Minted { get; private set; }
        public bool Initialized { get; private set; }
        public byte[] ManifestHash { get; private set; } = new byte[32];
        public string MetadataUri { get; private set; } = string.Empty;
        public PublicKey Creator { get; private set; }

        public static ObjectManifestAccount Deserialize(byte[] data, PublicKey accountAddress = null)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            using var ms = new MemoryStream(data, writable: false);
            using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

            var discriminator = reader.ReadBytes(8);
            if (discriminator.Length != 8)
                throw new InvalidOperationException("Account discriminator missing or truncated.");

            var config = BorshSerializationHelper.ReadPublicKey(reader);
            ulong objectId = reader.ReadUInt64();
            var mint = BorshSerializationHelper.ReadPublicKey(reader);
            byte bump = reader.ReadByte();
            byte mintBump = reader.ReadByte();
            bool isActive = reader.ReadBoolean();
            bool minted = reader.ReadBoolean();
            bool initialized = reader.ReadBoolean();
            var manifestHash = BorshSerializationHelper.ReadFixedLengthBytes(reader, 32);
            var metadataUri = BorshSerializationHelper.ReadString(reader);
            const int pubkeyLength = 32;
            PublicKey creator = null;
            if (reader.BaseStream.Position <= reader.BaseStream.Length - pubkeyLength)
            {
                creator = BorshSerializationHelper.ReadPublicKey(reader);
            }
            else
            {
                creator = new PublicKey(new byte[pubkeyLength]);
            }

            return new ObjectManifestAccount
            {
                Address = accountAddress,
                Config = config,
                ObjectId = objectId,
                Mint = mint,
                Bump = bump,
                MintBump = mintBump,
                IsActive = isActive,
                Minted = minted,
                Initialized = initialized,
                ManifestHash = manifestHash,
                MetadataUri = metadataUri,
                Creator = creator
            };
        }
    }

    internal static class BorshSerializationHelper
    {
        public static void WriteString(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            writer.Write((uint)bytes.Length);
            writer.Write(bytes);
        }

        public static void WritePublicKey(BinaryWriter writer, PublicKey value)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (value == null)
                throw new ArgumentNullException(nameof(value));

            writer.Write(value.KeyBytes);
        }

        public static string ReadString(BinaryReader reader)
        {
            uint length = reader.ReadUInt32();
            if (length > int.MaxValue)
                throw new InvalidOperationException("String exceeds maximum supported length.");

            var bytes = reader.ReadBytes((int)length);
            if (bytes.Length != length)
                throw new EndOfStreamException("Unexpected end of data while reading string.");

            return Encoding.UTF8.GetString(bytes);
        }

        public static PublicKey ReadPublicKey(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(32);
            if (bytes.Length != 32)
                throw new EndOfStreamException("Unexpected end of data while reading public key.");

            return new PublicKey(bytes);
        }

        public static byte[] ReadFixedLengthBytes(BinaryReader reader, int length)
        {
            if (length <= 0)
                throw new ArgumentOutOfRangeException(nameof(length), "Length must be positive.");

            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
                throw new EndOfStreamException("Unexpected end of data while reading byte array.");

            return bytes;
        }
    }
}

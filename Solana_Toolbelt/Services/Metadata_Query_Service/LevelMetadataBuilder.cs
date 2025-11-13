using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Solana.Unity.Toolbelt
{
    public interface ILevelMetadataTemplate
    {
        void Configure(LevelMetadataTemplateContext context);
    }

    public sealed class LevelMetadataTemplateContext
    {
        private readonly Func<string, string> _hashComputer;
        private readonly Action<NftMetadata, string> _hashStorage;
        private string _hash;
        private bool _hashComputed;

        internal LevelMetadataTemplateContext(
            NftMetadata metadata,
            string levelJson,
            Func<string, string> hashComputer,
            Action<NftMetadata, string> hashStorage)
        {
            Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
            LevelJson = levelJson;
            _hashComputer = hashComputer;
            _hashStorage = hashStorage;
        }

        public NftMetadata Metadata { get; }

        public string LevelJson { get; }

        public bool IsLevelJsonSuppressed { get; private set; }

        public void SuppressLevelJson() => IsLevelJsonSuppressed = true;

        public string GetHash()
        {
            if (_hashComputed)
                return _hash;

            _hash = _hashComputer != null ? _hashComputer(LevelJson ?? string.Empty) : null;
            _hashComputed = true;
            return _hash;
        }

        public void ApplyHash()
        {
            if (_hashStorage == null)
                return;

            var hash = GetHash();
            if (!string.IsNullOrEmpty(hash))
                _hashStorage(Metadata, hash);
        }

        public Attribute AddAttribute(string traitType, string value)
        {
            Metadata.attributes ??= new List<Attribute>();
            var attribute = new Attribute { trait_type = traitType, value = value };
            Metadata.attributes.Add(attribute);
            return attribute;
        }

        public void SetAdditionalField(string key, object value)
        {
            Metadata.additionalFields ??= new Dictionary<string, object>();
            Metadata.additionalFields[key] = value;
        }
    }

    public sealed class DefaultLevelMetadataTemplate : ILevelMetadataTemplate
    {
        public static readonly DefaultLevelMetadataTemplate Instance = new();

        private DefaultLevelMetadataTemplate()
        {
        }

        public void Configure(LevelMetadataTemplateContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            context.ApplyHash();

            if (!context.IsLevelJsonSuppressed)
                context.SetAdditionalField("level_json", context.LevelJson);
        }
    }

    /// <summary>
    /// Helper for creating NFT metadata JSON for level uploads.
    /// Computes a SHA-256 hash of the provided level JSON and stores it
    /// in the attributes array.
    /// </summary>
    public static class LevelMetadataBuilder
    {
        public static string CreateMetadataJson(string name, string symbol, string description,
            string image, string levelJson)
        {
            return CreateMetadataJson(name, symbol, description, image, levelJson, null, null, null);
        }

        public static string CreateMetadataJson(string name, string symbol, string description,
            string image, string levelJson,
            ILevelMetadataTemplate template = null,
            Func<string, string> hashComputer = null,
            Action<NftMetadata, string> hashStorage = null)
        {
            var meta = new NftMetadata
            {
                name = name,
                symbol = symbol,
                description = description,
                image = image,
                attributes = new List<Attribute>()
            };

            template ??= DefaultLevelMetadataTemplate.Instance;
            hashComputer ??= ComputeSha256;
            hashStorage ??= DefaultHashStorage;

            var context = new LevelMetadataTemplateContext(meta, levelJson, hashComputer, hashStorage);

            template.Configure(context);

            return CompactJsonSerializer.Serialize(meta);
        }

        private static string ComputeSha256(string text)
        {
            using var sha = SHA256.Create();
            byte[] data = Encoding.UTF8.GetBytes(text);
            byte[] hash = sha.ComputeHash(data);
            var sb = new StringBuilder();
            foreach (byte b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static void DefaultHashStorage(NftMetadata metadata, string hash)
        {
            if (metadata == null || string.IsNullOrEmpty(hash))
                return;

            metadata.attributes ??= new List<Attribute>();
            metadata.attributes.Add(new Attribute { trait_type = "sha256", value = hash });
        }
    }
}

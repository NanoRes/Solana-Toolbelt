using System;
using System.Collections.Generic;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Information about a single file uploaded to Bundlr/Irys.
    /// </summary>
    public class BundlrFileUploadResult
    {
        public string FileName { get; }
        public string ContentType { get; }
        public string Id { get; }
        public string Uri { get; }
        public BundlrUploadReceipt Receipt { get; }

        internal BundlrFileUploadResult(
            string fileName,
            string contentType,
            string id,
            string uri,
            BundlrUploadReceipt receipt)
        {
            FileName = fileName;
            ContentType = contentType;
            Id = id;
            Uri = uri;
            Receipt = receipt;
        }
    }

    /// <summary>
    /// Receipt information returned by Irys/Bundlr after a successful upload.
    /// </summary>
    public sealed class BundlrUploadReceipt
    {
        public string Id { get; }
        public string PublicKey { get; }
        public string Signature { get; }
        public long? DeadlineHeight { get; }
        public long? TimestampMilliseconds { get; }
        public DateTimeOffset? Timestamp
        {
            get
            {
                if (TimestampMilliseconds.HasValue)
                    return DateTimeOffset.FromUnixTimeMilliseconds(TimestampMilliseconds.Value);
                return null;
            }
        }

        public string Version { get; }
        public IReadOnlyList<BundlrValidatorSignature> ValidatorSignatures { get; }
        public string RawJson { get; }

        internal BundlrUploadReceipt(
            string id,
            string publicKey,
            string signature,
            long? deadlineHeight,
            long? timestampMilliseconds,
            string version,
            IReadOnlyList<BundlrValidatorSignature> validatorSignatures,
            string rawJson)
        {
            Id = id;
            PublicKey = publicKey;
            Signature = signature;
            DeadlineHeight = deadlineHeight;
            TimestampMilliseconds = timestampMilliseconds;
            Version = version;
            ValidatorSignatures = validatorSignatures ?? Array.Empty<BundlrValidatorSignature>();
            RawJson = rawJson;
        }
    }

    /// <summary>
    /// Signature contributed by a Bundlr validator.
    /// </summary>
    public sealed class BundlrValidatorSignature
    {
        public string Address { get; }
        public string Signature { get; }

        internal BundlrValidatorSignature(string address, string signature)
        {
            Address = address;
            Signature = signature;
        }
    }

    /// <summary>
    /// Result returned when uploading metadata JSON alongside an optional image.
    /// </summary>
    public class BundlrMetadataUploadResult
    {
        public BundlrFileUploadResult Json { get; }
        public BundlrFileUploadResult Image { get; }
        public bool HasImage => Image != null;

        internal BundlrMetadataUploadResult(BundlrFileUploadResult json, BundlrFileUploadResult image)
        {
            Json = json ?? throw new ArgumentNullException(nameof(json));
            Image = image;
        }
    }
}

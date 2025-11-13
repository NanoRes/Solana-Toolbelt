using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Solana.Unity.Toolbelt.Internal
{
    internal sealed class BundlrDataItem
    {
        private readonly byte[] _binary;
        private readonly int _signatureType;
        private readonly int _signatureLength;
        private readonly int _ownerLength;
        private readonly int _targetIndicatorOffset;
        private readonly int _targetDataOffset;
        private readonly int _targetDataLength;
        private readonly int _anchorIndicatorOffset;
        private readonly int _anchorDataOffset;
        private readonly int _anchorDataLength;
        private readonly int _tagsStartOffset;
        private readonly int _tagsPayloadLength;
        private readonly int _dataOffset;

        private BundlrDataItem(
            byte[] binary,
            int signatureType,
            int signatureLength,
            int ownerLength,
            int targetIndicatorOffset,
            int targetDataOffset,
            int targetDataLength,
            int anchorIndicatorOffset,
            int anchorDataOffset,
            int anchorDataLength,
            int tagsStartOffset,
            int tagsPayloadLength,
            int dataOffset)
        {
            _binary = binary;
            _signatureType = signatureType;
            _signatureLength = signatureLength;
            _ownerLength = ownerLength;
            _targetIndicatorOffset = targetIndicatorOffset;
            _targetDataOffset = targetDataOffset;
            _targetDataLength = targetDataLength;
            _anchorIndicatorOffset = anchorIndicatorOffset;
            _anchorDataOffset = anchorDataOffset;
            _anchorDataLength = anchorDataLength;
            _tagsStartOffset = tagsStartOffset;
            _tagsPayloadLength = tagsPayloadLength;
            _dataOffset = dataOffset;
        }

        public byte[] Binary => _binary;

        public static BundlrDataItem Create(
            byte[] data,
            IBundlrSigner signer,
            IReadOnlyList<BundlrTag> tags,
            byte[] target = null,
            byte[] anchor = null)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (signer == null)
                throw new ArgumentNullException(nameof(signer));

            int targetLength = target?.Length ?? 0;
            if (targetLength != 0 && targetLength != 32)
                throw new ArgumentException("Target must be 32 bytes when provided.", nameof(target));

            int anchorLength = anchor?.Length ?? 0;
            if (anchorLength != 0 && anchorLength != 32)
                throw new ArgumentException("Anchor must be 32 bytes when provided.", nameof(anchor));

            var tagBuffer = BundlrTagSerializer.Serialize(tags);
            int tagCount = tags?.Count ?? 0;
            int tagsLength = 16 + tagBuffer.Length;

            int signatureLength = signer.SignatureLength;
            int ownerLength = signer.OwnerLength;

            int totalLength = 2 + signatureLength + ownerLength +
                              (1 + targetLength) +
                              (1 + anchorLength) +
                              tagsLength +
                              data.Length;

            var binary = new byte[totalLength];

            WriteUInt16LittleEndian(binary, 0, (ushort)signer.SignatureType);
            // signature space already zeroed by array initialisation

            var owner = signer.PublicKey;
            if (owner.Length != ownerLength)
                throw new InvalidOperationException($"Signer returned a public key of {owner.Length} bytes but {ownerLength} were expected.");
            Buffer.BlockCopy(owner, 0, binary, 2 + signatureLength, ownerLength);

            int targetIndicatorOffset = 2 + signatureLength + ownerLength;
            int targetDataOffset = targetIndicatorOffset + 1;
            if (targetLength > 0)
            {
                binary[targetIndicatorOffset] = 1;
                Buffer.BlockCopy(target, 0, binary, targetDataOffset, targetLength);
            }

            int anchorIndicatorOffset = targetIndicatorOffset + 1 + targetLength;
            int anchorDataOffset = anchorIndicatorOffset + 1;
            if (anchorLength > 0)
            {
                binary[anchorIndicatorOffset] = 1;
                Buffer.BlockCopy(anchor, 0, binary, anchorDataOffset, anchorLength);
            }

            int tagsStartOffset = anchorIndicatorOffset + 1 + anchorLength;
            WriteUInt64LittleEndian(binary, tagsStartOffset, (ulong)tagCount);
            WriteUInt64LittleEndian(binary, tagsStartOffset + 8, (ulong)tagBuffer.Length);
            if (tagBuffer.Length > 0)
                Buffer.BlockCopy(tagBuffer, 0, binary, tagsStartOffset + 16, tagBuffer.Length);

            int dataOffset = tagsStartOffset + 16 + tagBuffer.Length;
            Buffer.BlockCopy(data, 0, binary, dataOffset, data.Length);

            return new BundlrDataItem(
                binary,
                signer.SignatureType,
                signatureLength,
                ownerLength,
                targetIndicatorOffset,
                targetDataOffset,
                targetLength,
                anchorIndicatorOffset,
                anchorDataOffset,
                anchorLength,
                tagsStartOffset,
                tagBuffer.Length,
                dataOffset);
        }

        public void ApplySignature(byte[] signature)
        {
            if (signature == null)
                throw new ArgumentNullException(nameof(signature));
            if (signature.Length != _signatureLength)
                throw new ArgumentException($"Signature must be {_signatureLength} bytes.", nameof(signature));

            Buffer.BlockCopy(signature, 0, _binary, 2, _signatureLength);
        }

        public byte[] GetSignatureData()
        {
            var dataItemBytes = Encoding.UTF8.GetBytes("dataitem");
            var versionBytes = Encoding.UTF8.GetBytes("1");
            var signatureTypeString = _signatureType.ToString(CultureInfo.InvariantCulture);
            var signatureTypeBytes = Encoding.UTF8.GetBytes(signatureTypeString);

            var chunks = new List<(byte[] Buffer, int Offset, int Count)>(8)
            {
                (dataItemBytes, 0, dataItemBytes.Length),
                (versionBytes, 0, versionBytes.Length),
                (signatureTypeBytes, 0, signatureTypeBytes.Length),
                (_binary, 2 + _signatureLength, _ownerLength),
                (_binary, _targetDataOffset, _targetDataLength),
                (_binary, _anchorDataOffset, _anchorDataLength),
                (_binary, _tagsStartOffset + 16, _tagsPayloadLength),
                (_binary, _dataOffset, _binary.Length - _dataOffset)
            };

            return DeepHashUtility.DeepHash(chunks);
        }

        public string GetId()
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(_binary, 2, _signatureLength);
            return Base64UrlUtility.Encode(hash);
        }

        private static void WriteUInt16LittleEndian(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static void WriteUInt64LittleEndian(byte[] buffer, int offset, ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                buffer[offset + i] = (byte)(value & 0xFF);
                value >>= 8;
            }
        }

    }

    internal static class DeepHashUtility
    {
        private static readonly byte[] BlobLabel = Encoding.UTF8.GetBytes("blob");
        private static readonly byte[] ListLabel = Encoding.UTF8.GetBytes("list");

        public static byte[] DeepHash(IReadOnlyList<(byte[] Buffer, int Offset, int Count)> chunks)
        {
            if (chunks == null)
                throw new ArgumentNullException(nameof(chunks));

            var listLengthBytes = Encoding.UTF8.GetBytes(chunks.Count.ToString(CultureInfo.InvariantCulture));
            var listTag = Concat(ListLabel, listLengthBytes);
            var acc = Sha384(listTag, 0, listTag.Length);

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                var chunkHash = HashBlob(chunk.Buffer, chunk.Offset, chunk.Count);
                acc = HashPair(acc, chunkHash);
            }

            return acc;
        }

        private static byte[] HashBlob(byte[] buffer, int offset, int count)
        {
            var lengthBytes = Encoding.UTF8.GetBytes(count.ToString(CultureInfo.InvariantCulture));
            var tag = Concat(BlobLabel, lengthBytes);
            var tagHash = Sha384(tag, 0, tag.Length);
            var dataHash = Sha384(buffer, offset, count);
            return HashPair(tagHash, dataHash);
        }

        private static byte[] HashPair(byte[] first, byte[] second)
        {
            using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA384);
            incremental.AppendData(first);
            incremental.AppendData(second);
            return incremental.GetHashAndReset();
        }

        private static byte[] Sha384(byte[] buffer, int offset, int count)
        {
            using var sha = SHA384.Create();
            return sha.ComputeHash(buffer, offset, count);
        }

        private static byte[] Concat(byte[] first, byte[] second)
        {
            var result = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, result, 0, first.Length);
            Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
            return result;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Provides shared helpers for constructing the canonical level metadata payload
    /// used by both the editor and mint popup flows.
    /// </summary>
    public static class LevelMintMetadataUtility
    {
        /// <summary>
        /// Result bundle returned when building the canonical metadata payload.
        /// </summary>
        public sealed class MetadataBuildResult<TMetadata>
        {
            internal MetadataBuildResult(TMetadata metadata, string json, byte[] sha256, ulong levelId)
            {
                Metadata = metadata;
                Json = json;
                Sha256 = sha256;
                LevelId = levelId;
            }

            /// <summary>
            /// The structured DTO describing the level configuration.
            /// </summary>
            public TMetadata Metadata { get; }

            /// <summary>
            /// Canonical JSON representation of the metadata.
            /// </summary>
            public string Json { get; }

            /// <summary>
            /// SHA-256 hash of the canonical JSON payload.
            /// </summary>
            public byte[] Sha256 { get; }

            /// <summary>
            /// Deterministic level identifier derived from the hash.
            /// </summary>
            public ulong LevelId { get; }
        }

        /// <summary>
        /// Build the canonical metadata, JSON payload and hash for the supplied level state.
        /// </summary>
        public static MetadataBuildResult<TMetadata> BuildCanonicalMetadata<TPlacement, TMetadata>(
            IEnumerable<TPlacement> placements,
            string levelName,
            int tossesAllowed,
            int goalsToWin,
            bool resetAfterToss,
            ILevelMetadataSerializer<TPlacement, TMetadata> serializer,
            bool prettyPrintJson = true)
        {
            if (placements == null)
                throw new ArgumentNullException(nameof(placements));
            if (serializer == null)
                throw new ArgumentNullException(nameof(serializer));

            var metadata = serializer.CreateLevelMetadata(placements, levelName, tossesAllowed, goalsToWin, resetAfterToss);
            string json = JsonUtility.ToJson(metadata, prettyPrintJson) ?? string.Empty;

            byte[] sha256Bytes;
            using (var sha256 = SHA256.Create())
            {
                sha256Bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(json));
            }

            ulong levelId = DeriveLevelId(sha256Bytes);
            return new MetadataBuildResult<TMetadata>(metadata, json, sha256Bytes, levelId);
        }

        /// <summary>
        /// Derive a deterministic level identifier from the SHA-256 hash bytes.
        /// </summary>
        public static ulong DeriveLevelId(byte[] sha256Bytes)
        {
            if (sha256Bytes == null)
                throw new ArgumentNullException(nameof(sha256Bytes));
            if (sha256Bytes.Length != 32)
                throw new ArgumentException("SHA-256 hash must be exactly 32 bytes.", nameof(sha256Bytes));

            if (BitConverter.IsLittleEndian)
            {
                return BitConverter.ToUInt64(sha256Bytes, 0);
            }

            var buffer = new byte[8];
            Array.Copy(sha256Bytes, buffer, 8);
            Array.Reverse(buffer);
            return BitConverter.ToUInt64(buffer, 0);
        }
    }
}

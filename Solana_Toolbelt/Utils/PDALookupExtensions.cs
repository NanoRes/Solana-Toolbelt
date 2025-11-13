using System.Text;
using Solana.Unity.Metaplex.NFT.Library;
using Solana.Unity.Wallet;
using System.Security.Cryptography;
using System.Linq;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Solana.Unity.Metaplex.Utilities
{
    /// <summary>
    /// Utility helpers for deriving Program Derived Addresses (PDAs).
    /// </summary>
    public static class PDALookupExtensions
    {
        private static readonly byte[] ProgramDerivedAddressConstant = Encoding.UTF8.GetBytes("ProgramDerivedAddress");

        private static (PublicKey pda, byte bump) FindProgramAddress(byte[][] seeds, PublicKey programId)
        {
            byte bump = 255;
            while (bump != 0)
            {
                var seedsWithBump = seeds.Concat(new[] { new[] { bump } }).ToArray();
                if (TryCreateProgramAddress(seedsWithBump, programId, out var address))
                    return (address, bump);
                bump--;
            }

            throw new System.Exception("Unable to find a viable program address.");
        }

        private static bool TryCreateProgramAddress(byte[][] seeds, PublicKey programId, out PublicKey address)
        {
            using var sha = SHA256.Create();
            var data = seeds.SelectMany(s => s)
                .Concat(programId.KeyBytes)
                .Concat(ProgramDerivedAddressConstant)
                .ToArray();
            var hash = sha.ComputeHash(data);

            if (Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.ValidatePublicKeyFull(hash, 0))
            {
                address = null;
                return false;
            }

            address = new PublicKey(hash);
            return true;
        }

        public static PublicKey FindCollectionAuthorityRecordPDA(PublicKey collectionMint, PublicKey authority)
        {
            var seeds = new[]
            {
                Encoding.UTF8.GetBytes("metadata"),
                MetadataProgram.ProgramIdKey.KeyBytes,
                collectionMint.KeyBytes,
                Encoding.UTF8.GetBytes("collection_authority"),
                authority.KeyBytes
            };

            var (pda, _) = FindProgramAddress(seeds, MetadataProgram.ProgramIdKey);
            return pda;
        }

        public static PublicKey FindMetadataPDA(PublicKey mint)
        {
            var seeds = new[]
            {
                Encoding.UTF8.GetBytes("metadata"),
                MetadataProgram.ProgramIdKey.KeyBytes,
                mint.KeyBytes
            };

            var (pda, _) = FindProgramAddress(seeds, MetadataProgram.ProgramIdKey);
            return pda;
        }

        public static PublicKey FindMasterEditionPDA(PublicKey mint)
        {
            var seeds = new[]
            {
                Encoding.UTF8.GetBytes("metadata"),
                MetadataProgram.ProgramIdKey.KeyBytes,
                mint.KeyBytes,
                Encoding.UTF8.GetBytes("edition")
            };

            var (pda, _) = FindProgramAddress(seeds, MetadataProgram.ProgramIdKey);
            return pda;
        }
    }
}

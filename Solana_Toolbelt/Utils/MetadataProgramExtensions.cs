using System.Collections.Generic;
using Solana.Unity.Rpc.Models;
using Solana.Unity.Wallet;

namespace Solana.Unity.Metaplex.Utilities
{
    public static class MetadataProgram
    {
        public static readonly PublicKey ProgramIdKey = new PublicKey("metaqbxxUerdq28cj1RbAWkYQm3ybzjb6a8bt518x1s");

        public static TransactionInstruction VerifyCollection(
            PublicKey metadataAccount,
            PublicKey collectionAuthority,
            PublicKey payer,
            PublicKey collectionMint,
            PublicKey collectionMetadata,
            PublicKey collectionMasterEdition,
            PublicKey collectionAuthorityRecord)
        {
            List<AccountMeta> keys = new()
            {
                AccountMeta.Writable(metadataAccount, false),
                AccountMeta.ReadOnly(collectionAuthority, true),
                AccountMeta.ReadOnly(payer, true),
                AccountMeta.ReadOnly(collectionMint, false),
                AccountMeta.Writable(collectionMetadata, false),
                AccountMeta.Writable(collectionMasterEdition, false),
                AccountMeta.ReadOnly(collectionAuthorityRecord, false)
            };

            // Instruction id for VerifyCollection is 34 in older SDKs
            byte[] data = new byte[] { 34, 0, 0, 0 };
            return new TransactionInstruction
            {
                ProgramId = ProgramIdKey,
                Keys = keys,
                Data = data
            };
        }
    }
}

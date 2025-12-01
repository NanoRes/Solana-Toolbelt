using System;
using System.Collections.Generic;
using Solana.Unity.Metaplex.NFT.Library;
using Solana.Unity.Metaplex.Utilities;
using Solana.Unity.Programs;
using Solana.Unity.Rpc.Builders;
using Solana.Unity.Rpc.Models;
using Solana.Unity.SDK;
using Solana.Unity.Wallet;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Builder responsible for composing the instructions required to mint a new NFT.
    /// </summary>
    public class MintTransactionBuilder
    {
        private readonly TransactionBuilder _transactionBuilder;
        private readonly Account _feePayerAccount;
        private readonly MintRequest _request;
        private readonly List<TransactionInstruction> _instructions = new();

        public MintTransactionBuilder(string recentBlockhash, Account feePayerAccount, MintRequest request)
        {
            _transactionBuilder = new TransactionBuilder()
                .SetRecentBlockHash(recentBlockhash)
                .SetFeePayer(feePayerAccount);

            _feePayerAccount = feePayerAccount ?? throw new ArgumentNullException(nameof(feePayerAccount));
            _request = request ?? throw new ArgumentNullException(nameof(request));

            MintAccount = new Account();
            MintAccountPublicKey = MintAccount.PublicKey;
            AssociatedTokenAccount = AssociatedTokenAccountProgram
                .DeriveAssociatedTokenAccount(_feePayerAccount.PublicKey, MintAccountPublicKey);
            MetadataPda = PDALookupExtensions.FindMetadataPDA(MintAccountPublicKey);
            MasterEditionPda = PDALookupExtensions.FindMasterEditionPDA(MintAccountPublicKey);
        }

        public Account MintAccount { get; }

        public PublicKey MintAccountPublicKey { get; }

        public PublicKey AssociatedTokenAccount { get; }

        public PublicKey MetadataPda { get; }

        public PublicKey MasterEditionPda { get; }

        public IReadOnlyList<TransactionInstruction> Instructions => _instructions;

        public virtual MintTransactionBuilder AddMintAccountCreation(ulong lamportsForMint)
        {
            AddInstruction(SystemProgram.CreateAccount(
                _feePayerAccount,
                MintAccountPublicKey,
                lamportsForMint,
                TokenProgram.MintAccountDataSize,
                TokenProgram.ProgramIdKey));

            AddInstruction(TokenProgram.InitializeMint(
                MintAccountPublicKey,
                0,
                _feePayerAccount.PublicKey,
                _feePayerAccount.PublicKey));

            AddInstruction(AssociatedTokenAccountProgram.CreateAssociatedTokenAccount(
                _feePayerAccount,
                _feePayerAccount.PublicKey,
                MintAccountPublicKey));

            AddInstruction(TokenProgram.MintTo(
                MintAccountPublicKey,
                AssociatedTokenAccount,
                1,
                _feePayerAccount));

            return this;
        }

        public virtual MintTransactionBuilder AddMetadataAccount()
        {
            var metadata = new Metadata
            {
                name = _request.Name,
                symbol = _request.Symbol,
                uri = _request.MetadataUri,
                sellerFeeBasisPoints = _request.SellerFeeBasisPoints,
                creators = _request.Creators
            };

            AddInstruction(Metaplex.NFT.Library.MetadataProgram.CreateMetadataAccount(
                MetadataPda,
                MintAccountPublicKey,
                _feePayerAccount.PublicKey,
                _feePayerAccount.PublicKey,
                _feePayerAccount.PublicKey,
                metadata,
                TokenStandard.NonFungible,
                _request.IsMutable,
                true,
                null,
                (int)MetadataVersion.V3));

            return this;
        }

        public virtual MintTransactionBuilder AddMasterEdition()
        {
            AddInstruction(Metaplex.NFT.Library.MetadataProgram.CreateMasterEdition(
                null,
                MasterEditionPda,
                MintAccount,
                _feePayerAccount,
                _feePayerAccount,
                _feePayerAccount,
                MetadataPda,
                CreateMasterEditionVersion.V3));

            return this;
        }

        public virtual MintTransactionBuilder AddCollectionVerification()
        {
            if (string.IsNullOrEmpty(_request.CollectionMint))
                return this;

            var collectionMint = new PublicKey(_request.CollectionMint);
            var collectionMetadataPda = PDALookupExtensions.FindMetadataPDA(collectionMint);
            var collectionMasterEditionPda = PDALookupExtensions.FindMasterEditionPDA(collectionMint);
            var collectionAuthorityRecordPda = PDALookupExtensions.FindCollectionAuthorityRecordPDA(
                collectionMint,
                _feePayerAccount.PublicKey);

            AddInstruction(Metaplex.Utilities.MetadataProgram.VerifyCollection(
                MetadataPda,
                _feePayerAccount.PublicKey,
                _feePayerAccount.PublicKey,
                collectionMint,
                collectionMetadataPda,
                collectionMasterEditionPda,
                collectionAuthorityRecordPda));

            return this;
        }

        public virtual MintTransactionBuilder AddMemo(string memo)
        {
            var memoInstruction = MemoUtils.CreateMemoInstruction(memo);
            if (memoInstruction != null)
            {
                AddInstruction(memoInstruction);
            }

            return this;
        }

        public virtual Transaction BuildTransaction(IEnumerable<Account> additionalSigners = null)
        {
            var signers = new List<Account> { MintAccount };
            if (additionalSigners != null)
                signers.AddRange(additionalSigners);

            var builtTransaction = _transactionBuilder.Build(signers);
            return Transaction.Deserialize(builtTransaction);
        }

        private void AddInstruction(TransactionInstruction instruction)
        {
            _instructions.Add(instruction);
            _transactionBuilder.AddInstruction(instruction);
        }
    }
}

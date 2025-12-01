using System.Collections.Generic;
using System.Text;
using Solana.Unity.Rpc.Models;
using Solana.Unity.Wallet;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Helper utilities for attaching memo instructions to Solana transactions.
    /// </summary>
    public static class MemoUtils
    {
        /// <summary>
        /// Program ID for the Memo program on Solana mainnet.
        /// </summary>
        public const string MemoProgramId = "MemoSq4gqABAXKb96qnH8TysNcWxMyWCqXgDLGmfcHr";

        /// <summary>
        /// Create a memo instruction with the provided string. Returns null if the memo is null or empty.
        /// </summary>
        public static TransactionInstruction CreateMemoInstruction(string memo)
        {
            if (string.IsNullOrEmpty(memo))
                return null;

            return new TransactionInstruction
            {
                ProgramId = new PublicKey(MemoProgramId),
                Keys = new List<AccountMeta>(),
                Data = Encoding.UTF8.GetBytes(memo)
            };
        }
    }
}

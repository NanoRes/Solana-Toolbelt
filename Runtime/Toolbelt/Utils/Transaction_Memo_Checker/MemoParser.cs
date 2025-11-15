using System;
using System.Collections.Generic;
using System.Text;
using Solana.Unity.Rpc.Models;

namespace Solana.Unity.Toolbelt
{
    public static class MemoParser
    {
        public const string MemoProgramId = MemoUtils.MemoProgramId;

        public static IReadOnlyList<MemoMatch> ExtractMatches(
            TransactionInfo transaction,
            IReadOnlyList<string> memoSearchStrings,
            StringComparison comparison)
        {
            var matches = new List<MemoMatch>();
            if (transaction?.Message?.Instructions == null || transaction.Message.AccountKeys == null)
                return matches;
            if (memoSearchStrings == null || memoSearchStrings.Count == 0)
                return matches;

            foreach (var instruction in transaction.Message.Instructions)
            {
                if (!TransactionFilterEvaluator.TryGetProgramId(transaction.Message, instruction, out var programId) ||
                    programId != MemoProgramId)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(instruction.Data))
                    continue;

                string memoText;
                try
                {
                    memoText = Encoding.UTF8.GetString(Convert.FromBase64String(instruction.Data));
                }
                catch (FormatException)
                {
                    continue;
                }

                foreach (var search in memoSearchStrings)
                {
                    if (memoText.IndexOf(search, comparison) >= 0)
                    {
                        matches.Add(new MemoMatch(memoText, search, ExtractSuffix(memoText, search, comparison)));
                        break;
                    }
                }
            }

            return matches;
        }

        private static string ExtractSuffix(string memo, string prefix, StringComparison comparison)
        {
            if (string.IsNullOrEmpty(memo) || string.IsNullOrEmpty(prefix))
                return memo;

            int index = memo.IndexOf(prefix, comparison);
            if (index < 0)
                return memo;

            string remainder = memo.Substring(index + prefix.Length).Trim();
            remainder = remainder.TrimStart(':', '-', ' ');
            return remainder;
        }
    }

    public readonly struct MemoMatch
    {
        public MemoMatch(string memoText, string matchedTerm, string suffix)
        {
            MemoText = memoText;
            MatchedTerm = matchedTerm;
            Suffix = suffix;
        }

        public string MemoText { get; }
        public string MatchedTerm { get; }
        public string Suffix { get; }
    }
}

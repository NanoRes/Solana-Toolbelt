using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Solana.Unity.Rpc.Models;

namespace Solana.Unity.Toolbelt
{
    public class TransactionFilterBuilder
    {
        private List<string> _memoSearchStrings;
        private bool? _caseSensitive;
        private HashSet<string> _destinationFilters;
        private HashSet<string> _programFilters;

        public TransactionFilterBuilder FromDescriptor(TransactionFilterDescriptor descriptor)
        {
            if (descriptor == null)
                return this;

            _memoSearchStrings = descriptor.MemoSearchStrings?.ToList();
            _caseSensitive = descriptor.Comparison == StringComparison.Ordinal;
            _destinationFilters = descriptor.DestinationFilters != null
                ? new HashSet<string>(descriptor.DestinationFilters, StringComparer.Ordinal)
                : null;
            _programFilters = descriptor.ProgramFilters != null
                ? new HashSet<string>(descriptor.ProgramFilters, StringComparer.Ordinal)
                : null;

            return this;
        }

        public TransactionFilterBuilder WithMemoSearchStrings(IEnumerable<string> memoSearchStrings)
        {
            if (memoSearchStrings == null)
                return this;

            _memoSearchStrings = NormalizeMemoList(memoSearchStrings);
            return this;
        }

        public TransactionFilterBuilder WithCaseSensitivity(bool? caseSensitive)
        {
            if (!caseSensitive.HasValue)
                return this;

            _caseSensitive = caseSensitive.Value;
            return this;
        }

        public TransactionFilterBuilder WithDestinationFilters(IEnumerable<string> filters)
        {
            if (filters == null)
                return this;

            _destinationFilters ??= new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in filters)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                _destinationFilters.Add(value.Trim());
            }

            if (_destinationFilters.Count == 0)
                _destinationFilters = null;

            return this;
        }

        public TransactionFilterBuilder WithProgramFilters(IEnumerable<string> filters)
        {
            if (filters == null)
                return this;

            _programFilters ??= new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in filters)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                _programFilters.Add(value.Trim());
            }

            if (_programFilters.Count == 0)
                _programFilters = null;

            return this;
        }

        public TransactionFilterDescriptor Build()
        {
            if (_memoSearchStrings == null || _memoSearchStrings.Count == 0)
                throw new InvalidOperationException("No memo search strings have been configured for the transaction filter.");

            var memoSearch = _memoSearchStrings;
            bool caseSensitive = _caseSensitive ?? false;

            IReadOnlyList<string> memos = new ReadOnlyCollection<string>(memoSearch);
            IReadOnlyCollection<string> destinations = _destinationFilters != null
                ? new ReadOnlyCollection<string>(_destinationFilters.ToList())
                : null;
            IReadOnlyCollection<string> programs = _programFilters != null
                ? new ReadOnlyCollection<string>(_programFilters.ToList())
                : null;

            return new TransactionFilterDescriptor(
                memos,
                caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase,
                destinations,
                programs);
        }

        private static List<string> NormalizeMemoList(IEnumerable<string> memos)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var memo in memos)
            {
                if (string.IsNullOrWhiteSpace(memo))
                    continue;
                var trimmed = memo.Trim();
                if (seen.Add(trimmed))
                    list.Add(trimmed);
            }

            return list.Count > 0 ? list : null;
        }
    }

    public sealed class TransactionFilterDescriptor
    {
        public TransactionFilterDescriptor(
            IReadOnlyList<string> memoSearchStrings,
            StringComparison comparison,
            IReadOnlyCollection<string> destinationFilters,
            IReadOnlyCollection<string> programFilters)
        {
            MemoSearchStrings = memoSearchStrings ?? throw new ArgumentNullException(nameof(memoSearchStrings));
            Comparison = comparison;
            DestinationFilters = destinationFilters;
            ProgramFilters = programFilters;
        }

        public IReadOnlyList<string> MemoSearchStrings { get; }
        public StringComparison Comparison { get; }
        public IReadOnlyCollection<string> DestinationFilters { get; }
        public IReadOnlyCollection<string> ProgramFilters { get; }
    }

    public static class TransactionFilterEvaluator
    {
        public static bool Matches(TransactionInfo transaction, TransactionFilterDescriptor descriptor)
        {
            if (transaction?.Message?.Instructions == null || transaction.Message.AccountKeys == null)
                return false;

            if (!MatchesProgramFilters(transaction.Message, descriptor.ProgramFilters))
                return false;

            if (!MatchesDestinationFilters(transaction.Message, descriptor.DestinationFilters))
                return false;

            return true;
        }

        internal static bool TryGetProgramId(TransactionContentInfo message, InstructionInfo instruction, out string programId)
        {
            programId = null;
            if (message?.AccountKeys == null || instruction == null)
                return false;

            var index = instruction.ProgramIdIndex;
            if (index < 0 || index >= message.AccountKeys.Length)
                return false;

            programId = message.AccountKeys[index];
            return true;
        }

        private static bool MatchesProgramFilters(TransactionContentInfo message, IReadOnlyCollection<string> programFilters)
        {
            if (programFilters == null || programFilters.Count == 0)
                return true;

            foreach (var instruction in message.Instructions ?? Array.Empty<InstructionInfo>())
            {
                if (TryGetProgramId(message, instruction, out var programId) && programFilters.Contains(programId))
                    return true;
            }

            return false;
        }

        private static bool MatchesDestinationFilters(TransactionContentInfo message, IReadOnlyCollection<string> destinationFilters)
        {
            if (destinationFilters == null || destinationFilters.Count == 0)
                return true;

            foreach (var instruction in message.Instructions ?? Array.Empty<InstructionInfo>())
            {
                if (instruction?.Accounts == null)
                    continue;

                foreach (var accountIndex in instruction.Accounts)
                {
                    if (accountIndex < 0 || accountIndex >= message.AccountKeys?.Length)
                        continue;

                    var key = message.AccountKeys[accountIndex];
                    if (destinationFilters.Contains(key))
                        return true;
                }
            }

            return false;
        }
    }
}

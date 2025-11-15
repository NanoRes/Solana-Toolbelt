using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Represents a persisted payment credit that can be reused when minting levels.
    /// </summary>
    [Serializable]
    public sealed class LevelMintPaymentCredit
    {
        public ulong levelId;
        public string memo;
        public string signature;
        public string currency;
        public ulong rawAmount;
        public string destinationAccount;
        public string destinationTokenMint;
        public string createdAtUtc;
        public bool consumed;

        public DateTime? TryGetCreatedAtUtc()
        {
            if (string.IsNullOrWhiteSpace(createdAtUtc))
            {
                return null;
            }

            if (DateTime.TryParse(
                    createdAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }

            return null;
        }

        public LevelMintPaymentCredit Clone()
        {
            return new LevelMintPaymentCredit
            {
                levelId = levelId,
                memo = memo,
                signature = signature,
                currency = currency,
                rawAmount = rawAmount,
                destinationAccount = destinationAccount,
                destinationTokenMint = destinationTokenMint,
                createdAtUtc = createdAtUtc,
                consumed = consumed
            };
        }
    }

    public interface ILevelMintPaymentLedger : IDisposable
    {
        Task<LevelMintPaymentCredit> TryGetActiveCreditAsync(
            ulong levelId,
            string currency,
            ulong rawAmount,
            CancellationToken cancellationToken = default);

        Task<LevelMintPaymentCredit> RecordCreditAsync(
            ulong levelId,
            string memo,
            string signature,
            string currency,
            ulong rawAmount,
            string destinationAccount,
            string destinationTokenMint,
            CancellationToken cancellationToken = default);

        Task<bool> ConsumeCreditAsync(string signature, CancellationToken cancellationToken = default);

        Task<int> PruneExpiredCreditsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Persists payment credits that can be reused when minting or updating levels.
    /// </summary>
    public sealed class LevelMintPaymentLedger : ILevelMintPaymentLedger
    {
        private const string StorageKey = "level_mint_payment_credits";

        [Serializable]
        private sealed class LedgerState
        {
            public List<LevelMintPaymentCredit> credits = new();
        }

        private readonly ISolanaStorageService storageService;
        private readonly SemaphoreSlim gate = new(1, 1);
        private bool disposed;

        public LevelMintPaymentLedger(ISolanaStorageService storageService)
        {
            this.storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
        }

        public async Task<LevelMintPaymentCredit> TryGetActiveCreditAsync(
            ulong levelId,
            string currency,
            ulong rawAmount,
            CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
                if (state?.credits == null || state.credits.Count == 0)
                {
                    return null;
                }

                foreach (var credit in state.credits)
                {
                    if (credit == null || credit.consumed)
                    {
                        continue;
                    }

                    if (credit.levelId != levelId)
                    {
                        continue;
                    }

                    if (!string.Equals(credit.currency, currency, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (credit.rawAmount != rawAmount)
                    {
                        continue;
                    }

                    return credit.Clone();
                }

                return null;
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<LevelMintPaymentCredit> RecordCreditAsync(
            ulong levelId,
            string memo,
            string signature,
            string currency,
            ulong rawAmount,
            string destinationAccount,
            string destinationTokenMint,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(signature))
            {
                throw new ArgumentException("A transaction signature is required to record a credit.", nameof(signature));
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false) ?? new LedgerState();

                if (state.credits == null)
                {
                    state.credits = new List<LevelMintPaymentCredit>();
                }

                state.credits.RemoveAll(c => string.Equals(c?.signature, signature, StringComparison.Ordinal));

                var credit = new LevelMintPaymentCredit
                {
                    levelId = levelId,
                    memo = memo,
                    signature = signature,
                    currency = currency,
                    rawAmount = rawAmount,
                    destinationAccount = destinationAccount,
                    destinationTokenMint = destinationTokenMint,
                    createdAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    consumed = false
                };

                state.credits.Add(credit);
                await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
                return credit.Clone();
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<bool> ConsumeCreditAsync(string signature, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(signature))
            {
                return false;
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
                if (state?.credits == null || state.credits.Count == 0)
                {
                    return false;
                }

                bool mutated = false;
                foreach (var credit in state.credits)
                {
                    if (credit == null)
                    {
                        continue;
                    }

                    if (!string.Equals(credit.signature, signature, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!credit.consumed)
                    {
                        credit.consumed = true;
                        mutated = true;
                    }
                }

                if (!mutated)
                {
                    return false;
                }

                await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
                return true;
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<int> PruneExpiredCreditsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
                if (state?.credits == null || state.credits.Count == 0)
                {
                    return 0;
                }

                DateTime threshold = DateTime.UtcNow - maxAge;
                int removed = state.credits.RemoveAll(credit => ShouldPrune(credit, threshold));

                if (removed > 0)
                {
                    await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
                }

                return removed;
            }
            finally
            {
                gate.Release();
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            gate.Dispose();
        }

        private static bool ShouldPrune(LevelMintPaymentCredit credit, DateTime threshold)
        {
            if (credit == null)
            {
                return true;
            }

            var createdAtUtc = credit.TryGetCreatedAtUtc();
            if (!createdAtUtc.HasValue)
            {
                return true;
            }

            var createdAt = createdAtUtc.Value;
            if (createdAt <= threshold)
            {
                return true;
            }

            return credit.consumed && createdAt <= DateTime.UtcNow - TimeSpan.FromMinutes(5);
        }

        private async Task<LedgerState> LoadStateAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await storageService
                    .ReadJsonAsync<LedgerState>(StorageKey, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LevelMintPaymentLedger] Failed to load ledger state: {ex.Message}");
                return new LedgerState();
            }
        }

        private Task SaveStateAsync(LedgerState state, CancellationToken cancellationToken)
        {
            return storageService.WriteJsonAsync(StorageKey, state, cancellationToken);
        }
    }
}

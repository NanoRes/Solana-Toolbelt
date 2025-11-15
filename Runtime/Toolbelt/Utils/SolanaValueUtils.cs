using System;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Shared helpers for working with SOL values, lamport conversions, and
    /// pricing utilities that are required across gameplay and toolbelt code.
    /// </summary>
    public static class SolanaValueUtils
    {
        /// <summary>
        /// Number of lamports in a single SOL.
        /// </summary>
        public const ulong LAMPORTS_PER_SOL = 1_000_000_000;

        /// <summary>
        /// Convert lamports to SOL using double precision.
        /// </summary>
        public static double LamportsToSol(ulong lamports)
        {
            return lamports / (double)LAMPORTS_PER_SOL;
        }

        /// <summary>
        /// Convert lamports to SOL using decimal precision.
        /// </summary>
        public static decimal LamportsToSolDecimal(ulong lamports)
        {
            return lamports / (decimal)LAMPORTS_PER_SOL;
        }

        /// <summary>
        /// Convert SOL to lamports using double precision.
        /// </summary>
        public static ulong SolToLamports(double sol)
        {
            return (ulong)Math.Round(sol * LAMPORTS_PER_SOL, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Convert SOL to lamports using decimal precision.
        /// </summary>
        public static ulong SolToLamports(decimal sol)
        {
            return (ulong)Math.Round(sol * LAMPORTS_PER_SOL, MidpointRounding.AwayFromZero);
        }

    }
}

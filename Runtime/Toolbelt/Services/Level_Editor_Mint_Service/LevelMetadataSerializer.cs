using System;
using System.Collections.Generic;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Creates level metadata DTOs from runtime placement data.
    /// </summary>
    public interface ILevelMetadataSerializer<TPlacement, TMetadata>
    {
        /// <summary>
        /// Build a DTO representing the level configuration.
        /// </summary>
        TMetadata CreateLevelMetadata(
            IEnumerable<TPlacement> placements,
            string levelName,
            int tossesAllowed,
            int goalsToWin,
            bool resetAfterToss);
    }

    /// <summary>
    /// Default serializer that maps runtime level editor data into the DTO consumed
    /// by storage uploads and downstream systems.
    /// </summary>
    public sealed class LevelMetadataSerializer<TPlacement, TMetadata> : ILevelMetadataSerializer<TPlacement, TMetadata>
    {
        private readonly Func<IEnumerable<TPlacement>, string, int, int, bool, TMetadata> _factory;

        public LevelMetadataSerializer(Func<IEnumerable<TPlacement>, string, int, int, bool, TMetadata> factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public TMetadata CreateLevelMetadata(
            IEnumerable<TPlacement> placements,
            string levelName,
            int tossesAllowed,
            int goalsToWin,
            bool resetAfterToss)
        {
            if (placements == null)
                throw new ArgumentNullException(nameof(placements));

            return _factory(placements, levelName, tossesAllowed, goalsToWin, resetAfterToss);
        }
    }
}

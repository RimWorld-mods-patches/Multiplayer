using System.Collections.Generic;

namespace Multiplayer.Common
{
    /// <summary>
    /// Pure decision rules for which simulation domains a faction-owned blocking session freezes.
    ///
    /// Lives in Common and is expressed entirely in faction ids so it can be unit tested: RimWorld's
    /// <c>Map</c>/<c>Pawn</c>/<c>Faction</c> can be referenced at compile time but cannot be constructed
    /// outside a running game, so any rule stated directly in terms of them is untestable. The client-side
    /// adapter that pulls these ids off a real <c>Map</c> lives in <c>Multiplayer.Client.PauseDomains</c>.
    ///
    /// See issue #512 and the caravan-encounter design note for the reasoning behind the two clauses.
    /// </summary>
    public static class PauseDomainRules
    {
        /// <summary>Sentinel for "no faction", matching a null <c>Faction</c> reference.</summary>
        public const int NoFaction = -1;

        /// <summary>
        /// Whether a map falls inside <paramref name="ownerFactionId"/>'s pause domain.
        ///
        /// Two clauses, both required:
        /// <list type="number">
        /// <item>The map is the owner's own colony (its parent faction is the owner).</item>
        /// <item>The map is shared or neutral but hosts at least one of the owner's humanlike pawns.
        /// Clause 2 exists because the tick scheduler is map-granular: if two player factions occupy one
        /// map and either is blocked, the whole map has to stop. Per-faction simulation inside a single
        /// map would need a scheduler redesign far larger than this fix.</item>
        /// </list>
        /// </summary>
        /// <param name="mapParentFactionId">
        /// The map's parent faction id, or <see cref="NoFaction"/> when the map has no parent faction.
        /// </param>
        /// <param name="humanlikePawnFactionIds">
        /// Faction ids of the humanlike pawns currently on the map. Animals are excluded by the caller:
        /// a stray owner-faction muffalo is not occupation and must not drag a whole map into the domain.
        /// May be null or empty.
        /// </param>
        /// <param name="ownerFactionId">The faction that owns the blocking session.</param>
        public static bool IsMapInPauseDomain(
            int mapParentFactionId,
            IEnumerable<int> humanlikePawnFactionIds,
            int ownerFactionId)
        {
            // A session with no owner pauses nothing; returning true here would freeze every map.
            if (ownerFactionId == NoFaction)
                return false;

            // Clause 1 — the owner's own colony.
            if (mapParentFactionId == ownerFactionId)
                return true;

            // Clause 2 — a shared or neutral map the owner occupies.
            if (humanlikePawnFactionIds == null)
                return false;

            foreach (int factionId in humanlikePawnFactionIds)
            {
                if (factionId == ownerFactionId)
                    return true;
            }

            return false;
        }
    }
}

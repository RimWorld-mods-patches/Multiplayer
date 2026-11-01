using System.Collections.Generic;
using Multiplayer.Common;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client.Persistent;

/// <summary>
/// Client-side adapter over <see cref="PauseDomainRules"/>. Pulls faction ids off live RimWorld objects
/// and defers every actual decision to the pure, unit-tested rules in Common.
///
/// Keep this file free of branching logic. Anything that can be got wrong belongs in
/// <see cref="PauseDomainRules"/> where it can be tested without a running game.
/// </summary>
public static class PauseDomains
{
    /// <summary>
    /// Whether <paramref name="map"/> falls inside <paramref name="ownerFactionId"/>'s pause domain.
    /// </summary>
    public static bool IsMapInPauseDomain(Map map, int ownerFactionId)
    {
        if (map == null)
            return false;

        return PauseDomainRules.IsMapInPauseDomain(
            map.ParentFaction?.loadID ?? PauseDomainRules.NoFaction,
            HumanlikePawnFactionIds(map),
            ownerFactionId);
    }

    /// <summary>
    /// Faction ids of the humanlike pawns spawned on <paramref name="map"/>. Lazily enumerated so the
    /// common case — the map is the owner's own colony, matched by clause 1 — never walks the pawn list.
    /// </summary>
    private static IEnumerable<int> HumanlikePawnFactionIds(Map map)
    {
        IReadOnlyList<Pawn> pawns = map.mapPawns?.AllPawnsSpawned;
        if (pawns == null)
            yield break;

        for (int i = 0; i < pawns.Count; i++)
        {
            Pawn pawn = pawns[i];
            if (pawn?.Faction != null && pawn.RaceProps is { Humanlike: true })
                yield return pawn.Faction.loadID;
        }
    }

    /// <summary>
    /// Whether a world object's tick should be suppressed because its owning faction is blocked.
    ///
    /// Placeholder: always false until the world tick gate lands (PR-4 / WP6). Returning false here means
    /// the world keeps ticking exactly as it does today, so referencing this early is inert rather than
    /// subtly wrong.
    /// </summary>
    public static bool IsWorldObjectPaused(WorldObject worldObject)
    {
        return false;
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client.Persistent;

/// <summary>
/// Keeps a caravan encounter from moving the camera of a player it does not belong to.
///
/// Vanilla's incident jumps the camera to the caravan it fired on, and its Attack outcome jumps to the
/// spawned pawns on the generated map. Both are ordinary single player behaviour. Both also run on every
/// client, because the incident ticks everywhere and the outcome is invoked inside the synchronized
/// command -- so one faction's encounter yanked every other player's view to a caravan they do not own,
/// and choosing Attack dropped them onto a battle map that was never theirs.
///
/// Multiplayer already suppresses camera jumps while simulating, and suppresses TryHideWorld for commands
/// a player did not issue, but nothing covers a live command that legitimately runs everywhere. Rather
/// than widen that global rule -- every synchronized command in the mod would be affected -- this holds
/// the view still only for the span of an encounter that belongs to someone else.
///
/// With multifaction off every player shares the owning faction, so nothing is ever suppressed and
/// behaviour is unchanged.
/// </summary>
[HarmonyPatch]
public static class CaravanEncounterViewLock
{
    /// <summary>Set only while an encounter this client does not own is running its incident or outcome.</summary>
    private static bool holding;

    /// <summary>
    /// Runs <paramref name="body"/> with this client's camera pinned when <paramref name="ownedLocally"/>
    /// is false. Restored in a finally: leaving the flag set would silently disable camera jumps for the
    /// rest of the session.
    /// </summary>
    public static void While(bool ownedLocally, Action body)
    {
        if (ownedLocally)
        {
            body();
            return;
        }

        bool previous = holding;
        holding = true;

        try
        {
            body();
        }
        finally
        {
            holding = previous;
        }
    }

    /// <summary>Whether the caravan in <paramref name="parms"/> belongs to the faction this client plays.</summary>
    public static bool OwnedLocally(IncidentParms parms)
    {
        if (parms?.target is not Caravan caravan)
            return true;

        return caravan.Faction != null && caravan.Faction == Multiplayer.RealPlayerFaction;
    }

    public static void Begin() => holding = true;

    public static void End() => holding = false;

    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(CameraJumper), nameof(CameraJumper.TrySelect));
        yield return AccessTools.Method(typeof(CameraJumper), nameof(CameraJumper.TryJumpAndSelect));
        yield return AccessTools.Method(
            typeof(CameraJumper),
            nameof(CameraJumper.TryJump),
            new[] { typeof(GlobalTargetInfo), typeof(CameraJumper.MovementMode) });
    }

    static bool Prefix() => !holding;
}

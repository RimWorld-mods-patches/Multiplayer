using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Client.Factions;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client.Persistent;

/// <summary>
/// Carries a caravan encounter's owning faction, and the local player's disinterest in it, across the
/// long event its outcome may queue.
///
/// Vanilla's Attack option does not do its work when clicked. It queues a long event and returns:
///
///     diaOption.action = () => LongEventHandler.QueueLongEvent(() => { ...generate, enter, jump... }, ...)
///
/// So the body runs after the synchronized command has finished and its context has been popped. Two
/// things that were true during the command are no longer true when the body runs.
///
/// The faction is the serious one. That body is written in terms of Faction.OfPlayer -- goodwill, map
/// ownership, which pawns count as the player's -- and outside the command that resolves to whichever
/// faction each client happens to play. One faction's attack then adjusted a different faction's goodwill
/// on every other machine, and CaravansBattlefield later looked for player pawns that were never there.
/// SeedLongEvents already carries the RNG seed across this same boundary; this carries the faction.
///
/// The camera is the cosmetic one. The body jumps to the pawns it spawns, which is right for the faction
/// that chose to attack and wrong for everyone else, who were dropped onto a battle that is not theirs.
///
/// With multifaction off every player shares the owning faction, so the push is a no-op and nothing is
/// ever suppressed.
///
/// State only. The two patches that read it are separate classes below, because Harmony refuses to mix
/// a TargetMethods bulk patch with an individually annotated one in the same class.
/// </summary>
public static class CaravanEncounterOutcomeScope
{
    /// <summary>Set only while an encounter this client does not own is running its incident or outcome.</summary>
    internal static bool holdingCamera;

    /// <summary>Non-null only while an outcome is running and may still queue a long event.</summary>
    internal static Faction pendingOwner;

    internal static bool pendingHoldsCamera;

    /// <summary>Whether the caravan in <paramref name="parms"/> belongs to the faction this client plays.</summary>
    public static bool OwnedLocally(IncidentParms parms)
    {
        if (parms?.target is not Caravan caravan)
            return true;

        return caravan.Faction != null && caravan.Faction == Multiplayer.RealPlayerFaction;
    }

    /// <summary>Holds this client's camera still for the span of an incident it does not own.</summary>
    public static void BeginIncident() => holdingCamera = true;

    public static void EndIncident() => holdingCamera = false;

    /// <summary>
    /// Runs an encounter outcome, and arranges for anything it defers to a long event to run under
    /// <paramref name="owner"/> as well.
    /// </summary>
    public static void RunOutcome(Faction owner, bool ownedLocally, Action body)
    {
        var previousOwner = pendingOwner;
        bool previousHolds = pendingHoldsCamera;
        bool previousCamera = holdingCamera;

        pendingOwner = owner;
        pendingHoldsCamera = !ownedLocally;
        holdingCamera = !ownedLocally;

        try
        {
            body();
        }
        finally
        {
            // Only the immediate call is unwound here. A long event queued by the body carries its own
            // copy of both, applied and undone in the wrapper below.
            pendingOwner = previousOwner;
            pendingHoldsCamera = previousHolds;
            holdingCamera = previousCamera;
        }
    }
}

/// <summary>
/// Pins the camera while <see cref="CaravanEncounterOutcomeScope"/> says this client is a bystander.
/// </summary>
[HarmonyPatch]
static class CaravanEncounterCameraLock
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(CameraJumper), nameof(CameraJumper.TrySelect));
        yield return AccessTools.Method(typeof(CameraJumper), nameof(CameraJumper.TryJumpAndSelect));
        yield return AccessTools.Method(
            typeof(CameraJumper),
            nameof(CameraJumper.TryJump),
            new[] { typeof(GlobalTargetInfo), typeof(CameraJumper.MovementMode) });
    }

    static bool Prefix() => !CaravanEncounterOutcomeScope.holdingCamera;
}

/// <summary>
/// Wraps a long event queued by an outcome so the deferred body sees the same faction and the same camera
/// decision the outcome had.
///
/// Mirrors <c>SeedLongEvents</c>, which does exactly this for the RNG state. Composed as one lambda with
/// try/finally rather than by concatenating delegates, because an exception in a concatenated tail would
/// leave the faction stack unbalanced for the rest of the session.
/// </summary>
[HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.QueueLongEvent),
    typeof(Action), typeof(string), typeof(bool), typeof(Action<Exception>), typeof(bool), typeof(bool),
    typeof(Action))]
static class CaravanEncounterLongEventScope
{
    static void Prefix(ref Action action)
    {
        if (CaravanEncounterOutcomeScope.pendingOwner == null || action == null)
            return;

        var owner = CaravanEncounterOutcomeScope.pendingOwner;
        bool hold = CaravanEncounterOutcomeScope.pendingHoldsCamera;
        var body = action;

        action = () =>
        {
            FactionExtensions.PushFaction(null, owner);
            bool previousCamera = CaravanEncounterOutcomeScope.holdingCamera;
            CaravanEncounterOutcomeScope.holdingCamera = hold;

            try
            {
                body();
            }
            finally
            {
                CaravanEncounterOutcomeScope.holdingCamera = previousCamera;
                FactionExtensions.PopFaction();
            }
        };
    }
}

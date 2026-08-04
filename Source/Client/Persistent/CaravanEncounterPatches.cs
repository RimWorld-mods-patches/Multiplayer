using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.API;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client.Persistent;

/// <summary>
/// Turns a caravan meeting or demand into a <see cref="CaravanEncounterSession"/>, so the encounter is
/// replicated state that can assert a pause and decide who may answer it.
///
/// Vanilla asks for a pause through Dialog_NodeTree.forcePause, and multiplayer deliberately suppresses
/// both routes it could take -- WindowsPausePatch forces WindowStack.WindowsForcePause false, and
/// TickManagerPausePatch blocks TickManager.Pause. Both suppressions are correct: window state is
/// client-local, and a pause derived from it is one the clients disagree about. What was missing is the
/// replacement. Sessions are the supported way to pause, and these two incidents never made one.
/// </summary>
[HarmonyPatch]
public static class CaravanEncounterPatches
{
    [HarmonyPatch(typeof(IncidentWorker_CaravanMeeting), nameof(IncidentWorker_CaravanMeeting.TryExecuteWorker))]
    [HarmonyPostfix]
    public static void CaravanMeetingExecuted(IncidentParms parms, bool __result)
    {
        if (__result)
            TryOpenEncounter(EncounterKind.Meeting, parms);
    }

    [HarmonyPatch(typeof(IncidentWorker_CaravanDemand), nameof(IncidentWorker_CaravanDemand.TryExecuteWorker))]
    [HarmonyPostfix]
    public static void CaravanDemandExecuted(IncidentParms parms, bool __result)
    {
        if (__result)
            TryOpenEncounter(EncounterKind.Demand, parms);
    }

    private static void TryOpenEncounter(EncounterKind kind, IncidentParms parms)
    {
        if (Multiplayer.Client == null)
            return;

        // A meeting fired at a map target is delegated to TravelerGroup by vanilla and is not a caravan
        // encounter at all.
        if (parms?.target is not Caravan caravan)
            return;

        if (caravan.Faction is not { IsPlayer: true })
            return;

        // The dialog vanilla just pushed. Its options carry the closures that actually carry out each
        // outcome, which is what the session needs to bind to.
        if (Find.WindowStack?.WindowOfType<Dialog_NodeTreeWithFactionInfo>() is not { } dialog)
            return;

        var options = MapOptions(kind, dialog.curNode);
        if (options == null)
        {
            // A mod reshaped the node tree. Leaving the session uncreated keeps the legacy behaviour --
            // no pause, but also no half-wired encounter whose buttons route somewhere unexpected.
            MpLog.Debug(
                $"MP: caravan {kind} node tree has an unexpected shape " +
                $"({dialog.curNode?.options?.Count ?? 0} options); leaving it on the legacy path");
            return;
        }

        var session = CreateSessionFor(kind, caravan, dialog.faction);
        if (session == null)
            return;

        session.BindPresentation(options, dialog);

        // Every client ran the incident and built this dialog, but only the owning faction decides. The
        // rest close it immediately; the session still exists and still pauses for them.
        if (!session.LocalPlayerOwnsThis)
            session.CloseWindowLocally();
    }

    /// <summary>
    /// Matches vanilla's option list onto semantic choice ids.
    ///
    /// Matched from the end rather than the start, because the only optional entry is the meeting's Trade
    /// option and it is built first -- Attack and MoveOn are always the last two, and the demand always
    /// has exactly Give then Fight. Returns null when the shape is anything else, which is the signal to
    /// leave the encounter alone rather than guess at a modded tree.
    /// </summary>
    private static Dictionary<EncounterChoiceId, DiaOption> MapOptions(EncounterKind kind, DiaNode node)
    {
        var options = node?.options;
        if (options == null)
            return null;

        if (kind == EncounterKind.Demand)
        {
            if (options.Count != 2)
                return null;

            return new Dictionary<EncounterChoiceId, DiaOption>
            {
                [EncounterChoiceId.AcceptDemand] = options[0],
                [EncounterChoiceId.RejectDemand] = options[1],
            };
        }

        // Meeting: [Trade?] Attack MoveOn
        if (options.Count is not (2 or 3))
            return null;

        var mapped = new Dictionary<EncounterChoiceId, DiaOption>
        {
            [EncounterChoiceId.Attack] = options[options.Count - 2],
            [EncounterChoiceId.MoveOn] = options[options.Count - 1],
        };

        if (options.Count == 3)
            mapped[EncounterChoiceId.Trade] = options[0];

        return mapped;
    }

    /// <summary>
    /// Adds the session for <paramref name="caravan"/>, unless that faction already has a live encounter.
    /// </summary>
    public static CaravanEncounterSession CreateSessionFor(EncounterKind kind, Caravan caravan, Faction encountered)
    {
        var sessionManager = Multiplayer.WorldComp?.sessionManager;
        if (sessionManager == null)
            return null;

        var session = new CaravanEncounterSession(
            kind,
            caravan,
            encountered,
            CaravanEncounterPolicy.SelectPolicy());

        if (!sessionManager.AddSession(session))
        {
            // CanExistWith refused it: this faction is already mid-decision. Stacking a second blocking
            // encounter on one owner is the condition that produced the original dialog deadlock.
            MpLog.Debug(
                $"MP: faction {session.ownerFactionId} already has a live caravan encounter; ignoring a second");
            return null;
        }

        return session;
    }

    /// <summary>
    /// Finds the live encounter whose dialog <paramref name="option"/> belongs to.
    /// </summary>
    public static CaravanEncounterSession FindSessionFor(DiaOption option, out EncounterChoiceId choice)
    {
        choice = default;

        var sessionManager = Multiplayer.WorldComp?.sessionManager;
        if (sessionManager == null)
            return null;

        var sessions = sessionManager.AllSessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            if (sessions[i] is CaravanEncounterSession encounter && encounter.TryGetChoiceFor(option, out choice))
                return encounter;
        }

        return null;
    }
}

/// <summary>
/// Routes a click on an encounter option into the session instead of running it locally.
///
/// Replaces the option-index path for these two incidents. An index is only meaningful against one node
/// on one client, so it mis-routes between concurrent dialogs and cannot say who clicked; a session plus
/// a revision plus a semantic choice says all three. Runs at high priority so it settles the click before
/// the generic node-tree sync sees it.
/// </summary>
[HarmonyPatch(typeof(DiaOption), nameof(DiaOption.Activate))]
public static class CaravanEncounterOptionPatch
{
    [HarmonyPriority(Priority.First)]
    public static bool Prefix(DiaOption __instance)
    {
        if (Multiplayer.Client == null)
            return true;

        // Already inside the synced command: this is the replicated invocation doing the real work, and
        // must be allowed to run rather than bounced back into another command.
        if (Multiplayer.ExecutingCmds)
            return true;

        var session = CaravanEncounterPatches.FindSessionFor(__instance, out var choice);
        if (session == null)
            return true;

        // A non-owner should not have this window at all, but a stray click must never reach vanilla's
        // action locally -- that would apply the outcome on one client only.
        if (!session.LocalPlayerOwnsThis)
            return false;

        session.ApplyChoice(session.revision, choice);
        return false;
    }
}

/// <summary>
/// Picks the pause policy an encounter opens with.
/// </summary>
public static class CaravanEncounterPolicy
{
    /// <summary>
    /// Ownership-scoped pausing is only expressible once maps tick independently *and* there is a way to
    /// hold a faction's world assets still. Neither holds yet, so every encounter opens on a global pause.
    /// The branch that adds the world tick gate replaces this with real capability detection.
    /// </summary>
    public static EncounterPausePolicy SelectPolicy()
    {
        return EncounterPausePolicy.GlobalFallback;
    }
}

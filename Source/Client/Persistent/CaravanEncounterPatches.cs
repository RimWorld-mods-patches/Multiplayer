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
    /// <summary>
    /// Set while one of the two incident workers is running, so the dialog it pushes can be identified by
    /// <em>when</em> it was added rather than by searching the window stack afterwards.
    /// </summary>
    private static bool capturingDialog;

    /// <summary>The dialog the running incident pushed, or null if it pushed none.</summary>
    private static Dialog_NodeTreeWithFactionInfo capturedDialog;

    [HarmonyPatch(typeof(IncidentWorker_CaravanMeeting), nameof(IncidentWorker_CaravanMeeting.TryExecuteWorker))]
    [HarmonyPrefix]
    public static void CaravanMeetingExecuting(IncidentParms parms) => BeginCapture(parms);

    [HarmonyPatch(typeof(IncidentWorker_CaravanMeeting), nameof(IncidentWorker_CaravanMeeting.TryExecuteWorker))]
    [HarmonyFinalizer]
    public static void CaravanMeetingExecuted(IncidentParms parms, bool __result)
        => EndCapture(EncounterKind.Meeting, parms, __result);

    [HarmonyPatch(typeof(IncidentWorker_CaravanDemand), nameof(IncidentWorker_CaravanDemand.TryExecuteWorker))]
    [HarmonyPrefix]
    public static void CaravanDemandExecuting(IncidentParms parms) => BeginCapture(parms);

    [HarmonyPatch(typeof(IncidentWorker_CaravanDemand), nameof(IncidentWorker_CaravanDemand.TryExecuteWorker))]
    [HarmonyFinalizer]
    public static void CaravanDemandExecuted(IncidentParms parms, bool __result)
        => EndCapture(EncounterKind.Demand, parms, __result);

    /// <summary>
    /// Encounter windows that were on the stack when the incident began.
    ///
    /// Recorded because WindowStack.Add opens with an unconditional RemoveWindowsOfType, so pushing this
    /// incident's dialog silently drops any encounter dialog already showing -- they share a type, and
    /// only one of that type may be on the stack at a time. Kept so a dialog displaced that way can be put
    /// back if the incident turns out to have nothing to replace it with. Local view state only.
    /// </summary>
    private static readonly List<CaravanEncounterSession> displacedDialogs = new();

    private static void BeginCapture(IncidentParms parms)
    {
        capturingDialog = true;
        capturedDialog = null;

        // Vanilla's worker jumps the camera to the caravan it fired on, and the incident runs on every
        // client. Hold the view still for players this encounter does not belong to.
        if (!CaravanEncounterViewLock.OwnedLocally(parms))
            CaravanEncounterViewLock.Begin();

        displacedDialogs.Clear();

        var sessions = Multiplayer.WorldComp?.sessionManager?.AllSessions;
        if (sessions == null)
            return;

        for (int i = 0; i < sessions.Count; i++)
            if (sessions[i] is CaravanEncounterSession encounter && encounter.IsWindowOpen)
                displacedDialogs.Add(encounter);
    }

    /// <summary>
    /// Puts back an encounter dialog that this incident's own dialog displaced, once it is clear nothing
    /// took its place. Only ever restores a window this client had open a moment ago, so a dialog the
    /// player deliberately minimised stays minimised.
    /// </summary>
    private static void RestoreDisplacedDialogs()
    {
        for (int i = 0; i < displacedDialogs.Count; i++)
            displacedDialogs[i].OpenWindow(jumpToTarget: false);

        displacedDialogs.Clear();
    }

    private static void EndCapture(EncounterKind kind, IncidentParms parms, bool executed)
    {
        var dialog = capturedDialog;

        // Cleared in a finalizer so a throwing incident cannot leave the flag set and mis-attribute the
        // next unrelated dialog to a caravan encounter.
        capturingDialog = false;
        capturedDialog = null;
        CaravanEncounterViewLock.End();

        if (executed)
            TryOpenEncounter(kind, parms, dialog);

        // Always, not just when the encounter was refused. Adding this incident's dialog displaced
        // whatever encounter dialog was showing, and that is just as wrong when the new one belongs to
        // another faction: the player watching their own encounter had it cleared off the screen by
        // someone else's. Restoring only ever puts back a window this client owns and had open a moment
        // ago, so it cannot fight with a dialog this client legitimately just received.
        RestoreDisplacedDialogs();
    }

    /// <summary>
    /// Records the dialog the running incident pushed.
    ///
    /// Deliberately not a <c>WindowOfType</c> search after the fact. That returns whatever dialog of the
    /// type is topmost, which is not necessarily the one this incident just created -- a client that
    /// already had a node-tree dialog open would bind the wrong one, or fail to bind and skip creating the
    /// session. Session creation calls UniqueIDsManager.GetNextID, a counter every client must advance in
    /// lockstep, so one client skipping it is an immediate desync. Identifying the dialog by when it was
    /// added keeps that decision identical everywhere.
    /// </summary>
    [HarmonyPatch(typeof(WindowStack), nameof(WindowStack.Add))]
    [HarmonyPostfix]
    public static void WindowAdded(Window window)
    {
        if (capturingDialog && window is Dialog_NodeTreeWithFactionInfo dialog)
            capturedDialog = dialog;
    }

    private static void TryOpenEncounter(EncounterKind kind, IncidentParms parms, Dialog_NodeTreeWithFactionInfo dialog)
    {
        if (Multiplayer.Client == null)
            return;

        // A meeting fired at a map target is delegated to TravelerGroup by vanilla and is not a caravan
        // encounter at all.
        if (parms?.target is not Caravan caravan)
            return;

        if (caravan.Faction is not { IsPlayer: true })
            return;

        if (dialog == null)
            return;

        // Derived from the node the incident just built, so it is the same on every client: the option
        // list follows from the incident's own deterministic execution, not from anything local.
        var options = MapOptions(kind, dialog.curNode);
        if (options == null)
        {
            // A mod reshaped the node tree. Leaving the session uncreated keeps the legacy behaviour --
            // no pause, but also no half-wired encounter whose buttons route somewhere unexpected. Safe to
            // branch on because the option count is identical on every client.
            MpLog.Debug(
                $"MP: caravan {kind} node tree has an unexpected shape " +
                $"({dialog.curNode?.options?.Count ?? 0} options); leaving it on the legacy path");
            return;
        }

        var session = CreateSessionFor(kind, caravan, dialog.faction);
        if (session == null)
        {
            // No session adopted this dialog -- normally because the owner already has a live encounter.
            // Leaving it on screen is not the harmless outcome it looks like: the legacy
            // RegisterSyncDialogNodeTree registrations for these two incidents are gone, so its options
            // are not synchronized by anything, and a click would run vanilla's outcome on this client
            // alone. It also carries no minimise button, since nothing owns it.
            //
            // Closing it is deterministic. Every client ran the same incident and refused for the same
            // reason on the same tick, so every client drops the same dialog.
            dialog.Close(doCloseSound: false);
            return;
        }

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

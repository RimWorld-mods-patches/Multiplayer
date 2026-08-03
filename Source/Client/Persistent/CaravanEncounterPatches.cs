using System.Linq;
using HarmonyLib;
using Multiplayer.API;
using Multiplayer.Client.Patches;
using Multiplayer.Common;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client.Persistent;

/// <summary>
/// Creates a <see cref="CaravanEncounterSession"/> when a caravan meeting or demand fires, so the
/// encounter is represented as replicated state that can assert a pause.
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

    /// <summary>
    /// Releases the encounter once the player has answered its dialog.
    ///
    /// Choices still travel over the legacy option-index path, which knows nothing about sessions. Without
    /// this the dialog would close on an answered encounter while the session kept holding its pause until
    /// the timeout expired -- a worse outcome than the bug being fixed. Hooking the synced choice method
    /// rather than the dialog's PostClose keeps the release deterministic: PostClose is UI that fires
    /// whenever a window happens to close, this runs identically on every client.
    ///
    /// The replacement path routes choices through CaravanEncounterSession.ApplyChoice directly, at which
    /// point this bridge goes away.
    /// </summary>
    [HarmonyPatch(typeof(NodeTreeDialogSync), nameof(NodeTreeDialogSync.SyncDialogOptionByIndex))]
    [HarmonyPostfix]
    public static void DialogOptionSynced()
    {
        if (Multiplayer.Client == null)
            return;

        var sessionManager = Multiplayer.WorldComp?.sessionManager;
        if (sessionManager == null)
            return;

        // Resolve only the encounter belonging to the faction that issued this choice. Another faction's
        // concurrent encounter is a separate decision and must keep its pause.
        //
        // This also gates the postfix to real command execution. A [SyncMethod] intercepts local calls in
        // order to send a command instead of running, and a Harmony postfix still fires on that intercepted
        // call -- so without this the issuing client would resolve its session early and diverge from
        // everyone else. Outside command execution the field is reset to NoFaction, so this returns.
        int commandFaction = TickPatch.currentExecutingCmdFactionId;
        if (commandFaction == ScheduledCommand.NoFaction)
            return;

        foreach (var session in sessionManager.AllSessions.ToList())
        {
            if (session is CaravanEncounterSession encounter
                && encounter.ownerFactionId == commandFaction
                && CaravanEncounterRules.AssertsPause(encounter.currentState))
            {
                encounter.Resolve();
            }
        }
    }

    private static void TryOpenEncounter(EncounterKind kind, IncidentParms parms)
    {
        if (Multiplayer.Client == null)
            return;

        // The incident already ran inside a synchronized context on every client, so the session is
        // created identically everywhere without needing its own synced command.
        if (parms?.target is not Caravan caravan)
            return;

        if (caravan.Faction is not { IsPlayer: true })
            return;

        CreateSessionFor(kind, caravan);
    }

    /// <summary>
    /// Adds the session for <paramref name="caravan"/>, unless that faction already has a live encounter.
    /// </summary>
    public static CaravanEncounterSession CreateSessionFor(EncounterKind kind, Caravan caravan)
    {
        var sessionManager = Multiplayer.WorldComp?.sessionManager;
        if (sessionManager == null)
            return null;

        var session = new CaravanEncounterSession(
            kind,
            caravan,
            encounteredFaction: null,
            counterparty: null,
            pausePolicy: CaravanEncounterPolicy.SelectPolicy());

        if (!sessionManager.AddSession(session))
        {
            // CanExistWith refused it: this faction is already mid-decision. Stacking a second blocking
            // encounter on one owner is the condition that produced the original dialog deadlock.
            return sessionManager.GetFirstOfType<CaravanEncounterSession>();
        }

        return session;
    }
}

/// <summary>
/// Picks the pause policy an encounter opens with.
/// </summary>
public static class CaravanEncounterPolicy
{
    /// <summary>
    /// Ownership-scoped pausing is only expressible when maps tick independently. With asyncTime off --
    /// the default -- the effective rate is the minimum across every tickable, so an ownership-scoped
    /// pause still drags all factions to zero. Claiming isolation there would be a lie that happens to
    /// look like it works.
    ///
    /// Until the world tick gate lands there is no way to hold a faction's world assets still, so this
    /// always answers GlobalFallback.
    /// </summary>
    public static EncounterPausePolicy SelectPolicy()
    {
        return EncounterPausePolicy.GlobalFallback;
    }
}

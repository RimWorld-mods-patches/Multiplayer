using System.Collections.Generic;
using Multiplayer.API;
using Multiplayer.Client.Patches;
using Multiplayer.Common;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client.Persistent;

/// <summary>
/// Replicated state for one caravan meeting or caravan demand.
///
/// Registered on the world session manager rather than a map's, because a caravan is a world object and
/// the encounter may have no map at all. Holds semantic state only -- a kind, a lifecycle state and a
/// choice id -- never a <c>Dialog_NodeTree</c>: DiaOption carries Action closures over live incident
/// state, which Scribe cannot serialize.
///
/// The outcome of a choice is still vanilla's own DiaOption action, captured at creation and invoked
/// inside the synchronized command. That is deliberate: reimplementing goodwill changes, map generation,
/// lord creation and the demand payout would be a second copy of vanilla's rules to keep in step with it,
/// and any drift between the two is a desync. What this session replaces is *who may choose and when* --
/// not what choosing does.
/// </summary>
public class CaravanEncounterSession : ExposableSession, ISessionWithCreationRestrictions, ITickingSession
{
    /// <summary>
    /// Bumped on every accepted transition. A choice command carries the revision the client believed it
    /// was acting on, so a duplicate or late click is a deterministic no-op instead of a second outcome.
    /// </summary>
    public int revision;

    /// <summary>
    /// The player faction that owns the target caravan, captured when the incident fires. Authority and
    /// pause scope both derive from this; it is never re-derived from whoever is looking at the screen.
    /// </summary>
    public int ownerFactionId = PauseDomainRules.NoFaction;

    public EncounterKind kind;
    public EncounterState currentState = EncounterState.Pending;
    public EncounterPausePolicy pausePolicy = EncounterPausePolicy.GlobalFallback;

    /// <summary>World tick the encounter opened on, used to bound how long it may hold a global pause.</summary>
    public int createdAtTicks;

    /// <summary>The owner's caravan whose simulation this encounter blocks.</summary>
    public Caravan targetCaravan;

    /// <summary>The non-player faction on the other side of the encounter.</summary>
    public Faction encounteredFaction;

    /// <summary>
    /// Vanilla's own option actions for this encounter, keyed by semantic id.
    ///
    /// Deliberately not serialized, and not serializable: these are closures over the generated
    /// counterparty. That costs nothing across a save, because vanilla does not persist the encounter
    /// either -- CaravanMaker only calls Find.WorldObjects.Add when the starting tile is valid, and the
    /// meeting passes PlanetTile.Invalid, so the met caravan and its pawns are already discarded by a
    /// save/load in the base game. A session that reloads without these retires itself rather than
    /// pretending it can still act, which lands on exactly the vanilla outcome.
    /// </summary>
    private Dictionary<EncounterChoiceId, DiaOption> choiceActions;

    /// <summary>The owner's local view, kept so closing the window can be undone. Not authoritative.</summary>
    private Window dialogWindow;

    /// <summary>World-level session: there is no single map this belongs to.</summary>
    public override Map Map => null;

    public override bool IsSessionValid =>
        ownerFactionId != PauseDomainRules.NoFaction
        && targetCaravan is { Destroyed: false }
        && currentState != EncounterState.Resolved
        && currentState != EncounterState.Invalidated;

    /// <summary>Whether this session still knows how to carry out a choice. False after a save/load.</summary>
    public bool CanApplyOutcomes => choiceActions is { Count: > 0 };

    /// <summary>Mandatory constructor used when the session manager reconstructs this from a save.</summary>
    public CaravanEncounterSession(Map _) : base(null)
    {
    }

    public CaravanEncounterSession(
        EncounterKind kind,
        Caravan targetCaravan,
        Faction encounteredFaction,
        EncounterPausePolicy pausePolicy) : base(null)
    {
        this.kind = kind;
        this.targetCaravan = targetCaravan;
        this.encounteredFaction = encounteredFaction;
        this.pausePolicy = pausePolicy;
        ownerFactionId = targetCaravan?.Faction?.loadID ?? PauseDomainRules.NoFaction;
        createdAtTicks = Find.TickManager?.TicksGame ?? 0;
    }

    /// <summary>
    /// Hands the session vanilla's option actions and the local window they came from. Called on every
    /// client, because every client ran the same incident and built the same dialog.
    /// </summary>
    public void BindPresentation(Dictionary<EncounterChoiceId, DiaOption> options, Window window)
    {
        choiceActions = options;
        dialogWindow = window;

        // Vanilla ships these dialogs with no way out -- Dialog_NodeTree sets closeOnCancel false, so
        // Escape does nothing -- because in single player answering is the only thing left to do and the
        // dialog is the only copy of the decision.
        //
        // Neither holds here. The session is the decision now, so the window is just a view of it, and an
        // unanswerable modal that pauses every player is far worse in multiplayer than in vanilla: one
        // player reading a dialog traps everyone else with no recourse. It also strands
        // GetBlockingWindowOptions, whose only purpose is to bring a dismissed window back.
        //
        // Escape follows TradingWindow, the other session-backed window a player can set aside; restoring
        // the Window default is enough, since only Dialog_NodeTree's constructor turns it off. The visible
        // affordance is the shared MpSwitchToMap button, drawn by SwitchToMapPatch -- see IsAdoptedWindow.
        if (window != null)
            window.closeOnCancel = true;
    }

    /// <summary>
    /// Whether <paramref name="window"/> is a vanilla dialog some live encounter has adopted.
    ///
    /// <see cref="ISwitchToMap"/> is the normal way a window asks for the minimise button, but that is a
    /// marker interface and this dialog is vanilla's own <c>Dialog_NodeTreeWithFactionInfo</c>, which
    /// cannot be made to implement it. Recognising it by ownership instead gets the same button from the
    /// same patch, rather than drawing a second one that only looks like it.
    /// </summary>
    public static bool IsAdoptedWindow(Window window)
    {
        if (window == null)
            return false;

        // Checked before WorldComp is touched at all: that property is game.worldComp with no null guard
        // of its own, so reading it outside a game throws. This runs from SwitchToMapPatch, which draws
        // every window there is -- including the main menu's and the debug log's, long before a game
        // exists -- and an exception there leaves the window blank rather than merely unbuttoned.
        if (Multiplayer.game == null || Multiplayer.Client == null)
            return false;

        var sessions = Multiplayer.WorldComp?.sessionManager?.AllSessions;
        if (sessions == null)
            return false;

        for (int i = 0; i < sessions.Count; i++)
            if (sessions[i] is CaravanEncounterSession encounter && encounter.dialogWindow == window)
                return true;

        return false;
    }

    /// <summary>Whether <paramref name="option"/> is one of this encounter's choices.</summary>
    public bool TryGetChoiceFor(DiaOption option, out EncounterChoiceId choice)
    {
        choice = default;
        if (choiceActions == null)
            return false;

        foreach (var pair in choiceActions)
        {
            if (pair.Value == option)
            {
                choice = pair.Key;
                return true;
            }
        }

        return false;
    }

    public override bool IsCurrentlyPausing(Map map)
    {
        if (!CaravanEncounterRules.AssertsPause(currentState))
            return false;

        // A session that can no longer be carried through must never hold a pause, because under
        // GlobalFallback the pause starves the very tick that would clean it up: pausing the world
        // tickable stops AsyncWorldTimeComp.Tick, which drives TickWorldSessions and therefore Tick()
        // below. Every recovery path lives in that Tick, so a session in this state pauses the world
        // permanently and Tick never runs to notice.
        //
        // Two ways in, both observed as softlocks. A save/load drops the transient choiceActions, leaving
        // a session that can pause but never resolve and whose dialog is gone too, so there is not even a
        // button left to press. And the target caravan can be destroyed mid-decision -- Tick is supposed
        // to invalidate the session for exactly that, but it never runs to do it.
        //
        // Declining to pause hands the world one tick, which is all Tick needs to retire the session.
        if (!CanApplyOutcomes || !IsSessionValid)
            return false;

        return CaravanEncounterRules.PolicyPauses(
            pausePolicy,
            isWorldTickable: map == null,
            isMapInOwnerDomain: map != null && PauseDomains.IsMapInPauseDomain(map, ownerFactionId));
    }

    /// <summary>
    /// One live encounter per owning faction. Two factions may each hold one simultaneously; a single
    /// faction may not stack them, which is the condition that produced the original dialog deadlock.
    /// </summary>
    public bool CanExistWith(Session other)
        => other is not CaravanEncounterSession encounter || encounter.ownerFactionId != ownerFactionId;

    /// <summary>
    /// Whether the local player may act on this encounter. Presentation only -- it decides what to draw,
    /// never whether a command is honoured. Authority is re-checked inside <see cref="ApplyChoice"/> from
    /// the command's own faction, so a client that lies here still cannot mutate the session.
    /// </summary>
    public bool LocalPlayerOwnsThis =>
        Multiplayer.RealPlayerFaction is { } faction && faction.loadID == ownerFactionId;

    public override FloatMenuOption GetBlockingWindowOptions(ColonistBar.Entry entry)
    {
        if (!LocalPlayerOwnsThis || !CaravanEncounterRules.AssertsPause(currentState) || dialogWindow == null)
            return null;

        // The colonist bar asks every session once per group, and draws a button on any group that answers.
        // Answering unconditionally put one on every colony and every caravan the player has, all opening
        // the same dialog, when only one caravan is actually in an encounter. MpTradeSession scopes itself
        // the same way, by the map its negotiator is standing on; a caravan encounter has no map, so the
        // caravan is what identifies the group.
        if (entry.pawn?.GetCaravan() != targetCaravan)
            return null;

        return new FloatMenuOption("MpCaravanEncounterSession".Translate(), () => OpenWindow());
    }

    /// <summary>
    /// Puts the owner's window back. Closing it is presentation, not a decision, so the session survives
    /// and this restores the view rather than rebuilding it -- the dialog holds the closures that carry
    /// out whatever the player picks.
    /// </summary>
    public void OpenWindow(bool jumpToTarget = true)
    {
        if (!LocalPlayerOwnsThis || !IsSessionValid || dialogWindow == null)
            return;

        if (!Find.WindowStack.IsOpen(dialogWindow))
            Find.WindowStack.Add(dialogWindow);

        // Skipped when merely putting back a window vanilla displaced, because that runs inside a
        // synchronized command: jumping the camera would move the local view and the local selection
        // mid-command, on one client only.
        if (jumpToTarget && targetCaravan != null)
            CameraJumper.TryJumpAndSelect(targetCaravan);
    }

    /// <summary>Whether this session's window is on the stack right now. Local view state, never synced.</summary>
    public bool IsWindowOpen => dialogWindow != null && Find.WindowStack.IsOpen(dialogWindow);

    /// <summary>Drops the owner's local window without touching the decision it represents.</summary>
    public void CloseWindowLocally()
    {
        if (dialogWindow != null && Find.WindowStack.IsOpen(dialogWindow))
            dialogWindow.Close(doCloseSound: false);
    }

    /// <summary>
    /// Applies a semantic choice on behalf of the faction that issued the command.
    ///
    /// Every client runs this identically, so acceptance must depend only on replicated state and the
    /// command's stamped faction. A refused command is a deterministic no-op everywhere rather than a
    /// divergence on the client that sent it.
    /// </summary>
    [SyncMethod]
    public void ApplyChoice(int expectedRevision, EncounterChoiceId choice)
    {
        var verdict = CaravanEncounterRules.EvaluateChoice(
            TickPatch.currentExecutingCmdFactionId,
            ownerFactionId,
            expectedRevision,
            revision,
            currentState,
            kind,
            choice);

        if (verdict != EncounterChoiceVerdict.Accept)
        {
            // Stale revisions are ordinary -- two players in one faction both clicked -- and must stay
            // quiet. A wrong-faction command is not ordinary and is worth surfacing.
            if (verdict == EncounterChoiceVerdict.WrongFaction)
                Log.Warning($"MP: refused caravan encounter choice {choice} on session {SessionId}: {verdict}");

            return;
        }

        if (!IsSessionValid)
        {
            Invalidate();
            return;
        }

        CommitChoice(choice);
    }

    /// <summary>
    /// Advances the session and carries the choice out. Order matters: the window closes first so the
    /// outcome's own windows open on top of nothing, and the session is removed last, so the pause is
    /// continuous -- between here and any successor session taking over there is no tick at which nothing
    /// is pausing.
    /// </summary>
    private void CommitChoice(EncounterChoiceId choice)
    {
        revision++;
        currentState = EncounterState.Transitioning;

        CloseWindowLocally();

        int tradesBefore = Multiplayer.WorldComp?.trading?.Count ?? 0;
        Transition(choice);
        ShowTradeToChooser(tradesBefore);

        Resolve();
    }

    /// <summary>
    /// Shows a trade the choice just opened to the player who chose it.
    ///
    /// <see cref="MpTradeSession"/> is created for everyone by DialogTradeCtorPatch, but that patch only
    /// raises a window for two shapes it recognises: a negotiator standing on the current map, and a
    /// Settlement viewed from the planet. A caravan meeting is neither -- the negotiator is in a caravan so
    /// its Map is null, and the trader is a Caravan -- so without this the chooser is left staring at a
    /// paused planet with the trade reachable only through the colonist bar.
    ///
    /// Only the issuing client opens it, matching <see cref="GrowthMomentSession"/>. Everyone else gets the
    /// session in their colonist bar instead of a window appearing over whatever they were doing.
    /// </summary>
    private static void ShowTradeToChooser(int tradesBefore)
    {
        if (!TickPatch.currentExecutingCmdIssuedBySelf)
            return;

        var trades = Multiplayer.WorldComp?.trading;
        if (trades == null || trades.Count <= tradesBefore)
            return;

        // PostAddSession appends, so a trade opened by this choice is the last one.
        trades[trades.Count - 1].OpenWindow();
    }

    /// <summary>
    /// Carries out a choice by invoking vanilla's own action for it.
    ///
    /// Runs inside the synchronized command on every client, which is what makes the follow-on work
    /// replicated rather than local: MP already intercepts <c>new Dialog_Trade(...)</c> while a command is
    /// executing and turns it into an MpTradeSession, so vanilla's Trade action produces a shared trade
    /// on every client instead of a window on one.
    /// </summary>
    private void Transition(EncounterChoiceId choice)
    {
        if (choiceActions == null || !choiceActions.TryGetValue(choice, out var option) || option?.action == null)
        {
            // Reached only if the session outlived the dialog that created it, which a save/load does.
            // Nothing to apply; releasing the pause is the safe direction to fail.
            Log.Warning($"MP: caravan encounter {SessionId} has no action for {choice}; releasing without an outcome");
            return;
        }

        option.action();
    }

    /// <summary>Ends the encounter normally and releases its pause.</summary>
    public void Resolve()
    {
        currentState = EncounterState.Resolved;
        Remove();
    }

    /// <summary>
    /// Ends the encounter because it can no longer be carried out. Distinct from <see cref="Resolve"/> so
    /// the difference between "the player chose" and "the world moved on" stays visible in logs and saves.
    /// </summary>
    public void Invalidate()
    {
        currentState = EncounterState.Invalidated;
        CloseWindowLocally();
        Remove();
    }

    private void Remove()
    {
        Multiplayer.WorldComp?.sessionManager?.RemoveSession(this);
    }

    /// <summary>
    /// Bounds how long an unanswered encounter may hold a global pause, drops the session if its target
    /// disappears, and retires one that outlived its dialog. Runs inside the synchronized tick on every
    /// client, so each of those fires on the same tick everywhere.
    /// </summary>
    public void Tick()
    {
        if (!CaravanEncounterRules.AssertsPause(currentState))
            return;

        if (!IsSessionValid)
        {
            Invalidate();
            return;
        }

        // Restored from a save. Vanilla does not persist this encounter at all -- its met caravan is never
        // added to Find.WorldObjects -- so matching that by dropping it is the honest outcome, and far
        // better than holding a pause that nothing left alive can release.
        if (!CanApplyOutcomes)
        {
            Log.Message($"MP: caravan encounter {SessionId} did not survive a reload; releasing it");
            Invalidate();
            return;
        }

        if (!CaravanEncounterRules.NeedsTimeout(pausePolicy))
            return;

        int now = Find.TickManager?.TicksGame ?? 0;
        if (CaravanEncounterRules.HasTimedOut(createdAtTicks, now))
        {
            var fallback = CaravanEncounterRules.DefaultChoiceFor(kind);
            Log.Message(
                $"MP: caravan encounter {SessionId} timed out after {now - createdAtTicks} ticks; " +
                $"resolving to {fallback}");

            CommitChoice(fallback);
        }
    }

    public override void ExposeData()
    {
        base.ExposeData();

        Scribe_Values.Look(ref revision, "revision");
        Scribe_Values.Look(ref ownerFactionId, "ownerFactionId", PauseDomainRules.NoFaction);
        Scribe_Values.Look(ref kind, "kind");
        Scribe_Values.Look(ref currentState, "currentState", EncounterState.Pending);
        Scribe_Values.Look(ref pausePolicy, "pausePolicy", EncounterPausePolicy.GlobalFallback);
        Scribe_Values.Look(ref createdAtTicks, "createdAtTicks");

        Scribe_References.Look(ref targetCaravan, "targetCaravan");
        Scribe_References.Look(ref encounteredFaction, "encounteredFaction");

        // choiceActions and dialogWindow are intentionally absent -- see their declarations. A session
        // that loads without them retires itself on its next tick.
    }
}

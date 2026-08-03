using Multiplayer.API;
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
/// state, which Scribe cannot serialize, so a session holding one could not survive a save.
///
/// This type is the data model only. Creation, presentation and choice handling land with the wiring
/// that replaces the RegisterSyncDialogNodeTree path.
/// </summary>
public class CaravanEncounterSession : ExposableSession, ISessionWithCreationRestrictions
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

    /// <summary>The owner's caravan whose simulation this encounter blocks.</summary>
    public Caravan targetCaravan;

    /// <summary>The non-player faction on the other side of the encounter.</summary>
    public Faction encounteredFaction;

    /// <summary>
    /// The generated other party. Vanilla builds this caravan with <c>PlanetTile.Invalid</c> and
    /// <c>CaravanMaker.MakeCaravan</c> only registers a caravan with <c>Find.WorldObjects</c> when the
    /// starting tile is valid -- so it is never a registered world object and no reference-based scribe
    /// can resolve it. This session therefore deep-owns it outright.
    /// </summary>
    public Caravan counterparty;

    /// <summary>World-level session: there is no single map this belongs to.</summary>
    public override Map Map => null;

    public override bool IsSessionValid =>
        ownerFactionId != PauseDomainRules.NoFaction
        && targetCaravan is { Destroyed: false }
        && currentState != EncounterState.Resolved
        && currentState != EncounterState.Invalidated;

    /// <summary>Mandatory constructor used when the session manager reconstructs this from a save.</summary>
    public CaravanEncounterSession(Map _) : base(null)
    {
    }

    public CaravanEncounterSession(
        EncounterKind kind,
        Caravan targetCaravan,
        Faction encounteredFaction,
        Caravan counterparty,
        EncounterPausePolicy pausePolicy) : base(null)
    {
        this.kind = kind;
        this.targetCaravan = targetCaravan;
        this.encounteredFaction = encounteredFaction;
        this.counterparty = counterparty;
        this.pausePolicy = pausePolicy;
        ownerFactionId = targetCaravan?.Faction?.loadID ?? PauseDomainRules.NoFaction;
    }

    public override bool IsCurrentlyPausing(Map map)
    {
        if (!CaravanEncounterRules.AssertsPause(currentState))
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

    /// <summary>Placeholder until the presenter lands; non-owners get no interactive surface regardless.</summary>
    public override FloatMenuOption GetBlockingWindowOptions(ColonistBar.Entry entry) => null;

    public override void ExposeData()
    {
        base.ExposeData();

        Scribe_Values.Look(ref revision, "revision");
        Scribe_Values.Look(ref ownerFactionId, "ownerFactionId", PauseDomainRules.NoFaction);
        Scribe_Values.Look(ref kind, "kind");
        Scribe_Values.Look(ref currentState, "currentState", EncounterState.Pending);
        Scribe_Values.Look(ref pausePolicy, "pausePolicy", EncounterPausePolicy.GlobalFallback);

        Scribe_References.Look(ref targetCaravan, "targetCaravan");
        Scribe_References.Look(ref encounteredFaction, "encounteredFaction");

        // Deep, not by reference: the counterparty is not in Find.WorldObjects, so there is nothing for a
        // reference to point at. Losing this is how the encountered pawns would vanish across a reload.
        Scribe_Deep.Look(ref counterparty, "counterparty");
    }
}

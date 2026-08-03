using System.Collections.Generic;

namespace Multiplayer.Common
{
    /// <summary>Which vanilla incident a caravan encounter session stands in for.</summary>
    public enum EncounterKind
    {
        Meeting,
        Demand,
    }

    /// <summary>Lifecycle of a caravan encounter session.</summary>
    public enum EncounterState
    {
        /// <summary>Awaiting the owning faction's decision. This is the state that asserts a pause.</summary>
        Pending,

        /// <summary>A choice was accepted and its successor state is being built. Still pausing.</summary>
        Transitioning,

        /// <summary>Finished normally; the session is about to be removed.</summary>
        Resolved,

        /// <summary>Target went away before a choice was made; cleaned up without applying an outcome.</summary>
        Invalidated,
    }

    /// <summary>
    /// A semantic choice on a caravan encounter. Deliberately not an index into the dialog's option list:
    /// an index is only meaningful against one specific node on one specific client, which is the defect
    /// behind the cross-talk and unresponsive-window failures in #512.
    /// </summary>
    public enum EncounterChoiceId
    {
        Trade,
        Attack,
        MoveOn,
        AcceptDemand,
        RejectDemand,
    }

    /// <summary>How widely an encounter's pause reaches.</summary>
    public enum EncounterPausePolicy
    {
        /// <summary>Pause every tickable. Correct under synchronized time, where the effective rate is the
        /// minimum across all tickables and an ownership-scoped pause would freeze everyone anyway.</summary>
        GlobalFallback,

        /// <summary>Pause only the owning faction's maps and world assets. Requires async time.</summary>
        OwnerFactionAssets,
    }

    /// <summary>Why a choice command was refused. Surfaced rather than swallowed, so a rejected command
    /// is diagnosable instead of looking like a dropped click.</summary>
    public enum EncounterChoiceVerdict
    {
        Accept,

        /// <summary>The command was issued under a faction that does not own this encounter.</summary>
        WrongFaction,

        /// <summary>The client acted on a view of the session that has since moved on.</summary>
        StaleRevision,

        /// <summary>The encounter is no longer awaiting a decision.</summary>
        NotPending,

        /// <summary>The choice is not offered for this encounter kind.</summary>
        ChoiceNotAvailable,
    }

    /// <summary>
    /// Pure rules governing caravan encounters: which choices exist, which one an abandoned encounter
    /// falls back to, and whether an incoming choice command may be applied.
    ///
    /// Stated without RimWorld types so it can be unit tested -- see <see cref="PauseDomainRules"/> for
    /// the same reasoning. The session that holds this state lives in
    /// <c>Multiplayer.Client.Persistent.CaravanEncounterSession</c>.
    /// </summary>
    public static class CaravanEncounterRules
    {
        private static readonly EncounterChoiceId[] MeetingChoices =
        {
            EncounterChoiceId.Trade,
            EncounterChoiceId.Attack,
            EncounterChoiceId.MoveOn,
        };

        private static readonly EncounterChoiceId[] DemandChoices =
        {
            EncounterChoiceId.AcceptDemand,
            EncounterChoiceId.RejectDemand,
        };

        /// <summary>The choices a given encounter kind offers.</summary>
        public static IReadOnlyList<EncounterChoiceId> ChoicesFor(EncounterKind kind)
            => kind == EncounterKind.Demand ? DemandChoices : MeetingChoices;

        public static bool IsChoiceAvailable(EncounterKind kind, EncounterChoiceId choice)
        {
            var choices = ChoicesFor(kind);
            for (int i = 0; i < choices.Count; i++)
            {
                if (choices[i] == choice)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The outcome an unanswered encounter resolves to when its bounded timeout expires.
        ///
        /// Chosen to be the option a player who walked away has effectively taken, and never one that
        /// hands over colony goods on their behalf: a meeting lapses into MoveOn, a demand into
        /// RejectDemand. Note this makes a lapsed demand hostile, which is the honest reading of an
        /// ignored ultimatum -- and the alternative silently pays it.
        /// </summary>
        public static EncounterChoiceId DefaultChoiceFor(EncounterKind kind)
            => kind == EncounterKind.Demand ? EncounterChoiceId.RejectDemand : EncounterChoiceId.MoveOn;

        /// <summary>
        /// Whether a session in <paramref name="state"/> should hold a pause.
        /// Transitioning still pauses: the window between accepting a choice and its successor session
        /// taking over must not contain an unpaused tick.
        /// </summary>
        public static bool AssertsPause(EncounterState state)
            => state == EncounterState.Pending || state == EncounterState.Transitioning;

        /// <summary>
        /// Whether a pause policy freezes the given tickable.
        /// </summary>
        /// <param name="policy">The session's configured policy.</param>
        /// <param name="isWorldTickable">True for the world tickable (RimWorld passes a null map for it).</param>
        /// <param name="isMapInOwnerDomain">
        /// For a map tickable, whether it is in the owner's pause domain per <see cref="PauseDomainRules"/>.
        /// Ignored when <paramref name="isWorldTickable"/> is true.
        /// </param>
        public static bool PolicyPauses(EncounterPausePolicy policy, bool isWorldTickable, bool isMapInOwnerDomain)
        {
            if (policy == EncounterPausePolicy.GlobalFallback)
                return true;

            // Under OwnerFactionAssets the shared world clock keeps running; the owner's world objects are
            // held still by a tick gate instead. Freezing the world tickable here would stop every faction.
            if (isWorldTickable)
                return false;

            return isMapInOwnerDomain;
        }

        /// <summary>
        /// How long an unanswered encounter holds its pause before auto-resolving, in ticks.
        /// One in-game day at normal speed. Long enough that a player reading the dialog is never
        /// rushed, short enough that an abandoned encounter does not strand a server indefinitely.
        /// </summary>
        public const int DefaultTimeoutTicks = 60000;

        /// <summary>
        /// Whether an encounter opened at <paramref name="createdAtTicks"/> has outlived its timeout.
        ///
        /// Measured in simulation ticks, never wall-clock: every client must agree on exactly which tick
        /// the auto-resolve fires, and a real-time timer would fire at a different tick on each machine.
        /// Only meaningful under GlobalFallback, where a stalled owner blocks everyone; an
        /// OwnerFactionAssets encounter blocks only its owner and can wait indefinitely.
        /// </summary>
        public static bool HasTimedOut(int createdAtTicks, int nowTicks, int timeoutTicks = DefaultTimeoutTicks)
        {
            if (timeoutTicks <= 0)
                return false;

            return nowTicks - createdAtTicks >= timeoutTicks;
        }

        /// <summary>Whether a policy needs the timeout at all.</summary>
        public static bool NeedsTimeout(EncounterPausePolicy policy)
            => policy == EncounterPausePolicy.GlobalFallback;

        /// <summary>
        /// Whether an incoming choice command may be applied, and if not, why.
        ///
        /// Authority is decided from the faction the command was issued under -- stamped by the server and
        /// pushed into faction context before the handler runs -- never from anything client-local.
        /// </summary>
        public static EncounterChoiceVerdict EvaluateChoice(
            int commandFactionId,
            int ownerFactionId,
            int expectedRevision,
            int currentRevision,
            EncounterState state,
            EncounterKind kind,
            EncounterChoiceId choice)
        {
            if (ownerFactionId == PauseDomainRules.NoFaction || commandFactionId != ownerFactionId)
                return EncounterChoiceVerdict.WrongFaction;

            if (expectedRevision != currentRevision)
                return EncounterChoiceVerdict.StaleRevision;

            if (state != EncounterState.Pending)
                return EncounterChoiceVerdict.NotPending;

            if (!IsChoiceAvailable(kind, choice))
                return EncounterChoiceVerdict.ChoiceNotAvailable;

            return EncounterChoiceVerdict.Accept;
        }
    }
}

using Multiplayer.Common;

namespace Multiplayer.Client.Persistent;

/// <summary>
/// A blocking session whose pause is scoped to one faction's assets rather than the whole game.
///
/// Exists so the world tick gate can ask "is this world object's faction blocked?" without knowing which
/// kind of session did the blocking. Without a shared contract, every ownership-scoped session would need
/// its own branch inside the gate, and adding one while forgetting that branch would produce the worst
/// failure available here: a caravan that stops moving but keeps foraging and ageing.
/// </summary>
public interface IFactionScopedPauseSession
{
    /// <summary>
    /// The faction whose assets this session freezes, or <see cref="PauseDomainRules.NoFaction"/> when it
    /// cannot be determined -- in which case the session pauses nothing rather than everything.
    /// </summary>
    int PauseOwnerFactionId { get; }

    /// <summary>How widely this session's pause reaches.</summary>
    EncounterPausePolicy PausePolicy { get; }

    /// <summary>Whether the session is currently holding a pause at all.</summary>
    bool IsPauseActive { get; }
}

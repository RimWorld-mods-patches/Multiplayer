using HarmonyLib;
using RimWorld.Planet;

namespace Multiplayer.Client.Persistent;

/// <summary>
/// Holds a blocked faction's caravans still while the shared world clock keeps running.
///
/// Gates <c>Caravan.TickInterval</c> rather than <c>Caravan_PathFollower.Paused</c>, which is the obvious
/// lever and the wrong one twice over. That flag is persistent player intent -- writing it would silently
/// overwrite a pause the player set themselves and never restore it -- and it only suppresses movement.
/// TickInterval also drives forage, carry, beds, needs, babies, drug policy, tending and pollution, all of
/// which would otherwise keep advancing for a caravan whose owner is mid-decision.
/// </summary>
[HarmonyPatch(typeof(Caravan), nameof(Caravan.TickInterval))]
public static class CaravanEncounterWorldGate
{
    /// <summary>
    /// Skips the caravan's whole tick workload when its owning faction holds an ownership-scoped
    /// encounter. Returns true (run normally) in every other case, including under GlobalFallback, where
    /// the tickable itself is already at zero and there is nothing left to suppress.
    /// </summary>
    public static bool Prefix(Caravan __instance)
    {
        return !PauseDomains.IsWorldObjectPaused(__instance);
    }
}

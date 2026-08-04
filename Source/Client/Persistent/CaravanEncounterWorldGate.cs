using HarmonyLib;
using Multiplayer.Client.Util;
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
///
/// The parameter types are stated explicitly because <c>TickInterval</c> is protected: it is only
/// reachable from here at all because Krafs.Publicizer opens it up at compile time, and pinning the
/// signature means a future overload cannot silently redirect this patch onto the wrong method.
/// </summary>
[HarmonyPatch(typeof(Caravan), nameof(Caravan.TickInterval), typeof(int))]
public static class CaravanEncounterWorldGate
{
    /// <summary>
    /// Skips the caravan's whole tick workload when its owning faction holds an ownership-scoped
    /// encounter. Returns true (run normally) in every other case, including under GlobalFallback, where
    /// the tickable itself is already at zero and there is nothing left to suppress.
    ///
    /// Runs last on purpose. <see cref="Patches.WorldObjectMethodPatches"/> already prefixes every
    /// WorldObject's TickInterval at MpFirst to push faction context, and pops it again in a finalizer.
    /// Harmony stops running prefixes once one returns false, but finalizers always run -- so a gate that
    /// ran *before* that push would suppress it while its pop still happened, unbalancing the faction
    /// stack on every gated tick. Ordering after it keeps the push and pop paired.
    /// </summary>
    [HarmonyPriority(MpPriority.MpLast)]
    public static bool Prefix(Caravan __instance)
    {
        return !PauseDomains.IsWorldObjectPaused(__instance);
    }
}

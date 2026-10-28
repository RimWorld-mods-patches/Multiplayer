using LudeonTK;
using Multiplayer.Client;
using Verse;
using MP = Multiplayer.Client.Multiplayer;

namespace Multiplayer.LiveTests;

public static partial class Actions
{
    [DebugAction(Category, "Live: inject desync", allowedGameStates = AllowedGameStates.Playing)]
    public static void InjectDesync()
    {
        Require(MP.LocalServer == null && MP.Client != null, "Inject only on the remote test peer");
        Require(!MP.session.desynced, "Peer was already desynced before injection");
        // Deliberately local: exercise the real RNG-opinion comparison and network path.
        MP.AsyncWorldTime.randState ^= 0x9E3779B900000000UL;
        Emit("desync_injected", new { });
    }
}

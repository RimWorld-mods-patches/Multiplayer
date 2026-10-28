using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using LudeonTK;
using Multiplayer.Client;
using Multiplayer.Common;
using RimWorld;
using Verse;
using MP = Multiplayer.Client.Multiplayer;

namespace Multiplayer.LiveTests;

public static partial class Actions
{
    private static Game gameBeforeRejoin;
    private static Game gameBeforeReload;
    private static object GeneratedPawnSnapshot(Map map, FixtureState state)
    {
        var pawn = map?.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == state.generatedPawnId);
        return pawn == null ? null : new { id = pawn.thingIDNumber, name = pawn.Name.ToStringFull,
            name_hash = pawn.Name.GetHashCode(), kind = pawn.kindDef.defName, faction = pawn.Faction.loadID };
    }

    [DebugAction(Category, "Live: generate pawn", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void GeneratePawnRequest() => GeneratePawn();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void GeneratePawn()
    {
        Require(MP.ExecutingCmds, "Pawn generation must be synchronized");
        Require(State.generatedPawnId == -1, "Pawn fixture already exists");
        var pawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
        var cell = GenRadial.RadialCellsAround(Map.Center, 20, true).First(c => c.InBounds(Map) && c.Standable(Map));
        GenSpawn.Spawn(pawn, cell, Map);
        State.generatedPawnId = pawn.thingIDNumber;
        State.sequence++;
        Emit("pawn_generated", GeneratedPawnSnapshot(Map, State));
    }

    [DebugAction(Category, "Live: arm reload", allowedGameStates = AllowedGameStates.Playing)]
    public static void ArmReload()
    {
        gameBeforeReload = Current.Game;
        Emit("reload_armed", new { });
    }

    [DebugAction(Category, "Live: save reload", allowedGameStates = AllowedGameStates.Playing)]
    public static void SaveReload()
    {
        Require(MP.LocalServer != null, "The host requests the production join point");
        Require(Find.Maps.All(m => m.AsyncTime().Paused), "Pause every map before local reload");
        Autosaving.SendAutosavingRequest(JoinPointRequestReason.Save);
        Emit("reload_requested", new { });
    }

    [DebugAction(Category, "Live: save archive", allowedGameStates = AllowedGameStates.Playing)]
    public static void SaveArchive()
    {
        Require(Autosaving.SaveGameToFile_Overwrite("MP-LiveTests", false), "Multiplayer save failed");
        var path = Path.Combine(MP.ReplaysDir, "MP-LiveTests.zip");
        Emit("archive_saved", new { sequence = State.sequence, path, bytes = new FileInfo(path).Length });
    }

    [DebugAction(Category, "Live: rejoin", allowedGameStates = AllowedGameStates.Playing)]
    public static void Rejoin()
    {
        Require(MP.LocalServer == null, "Only the remote client may rejoin");
        Require(MP.Client != null && !MP.session.desynced, "A healthy connection is required");
        gameBeforeRejoin = Current.Game;
        Rejoiner.DoRejoin();
        Emit("rejoin_requested", new { });
    }

    [DebugAction(Category, "Live: disconnect", allowedGameStates = AllowedGameStates.Playing)]
    public static void Disconnect()
    {
        Require(MP.LocalServer == null && MP.Client != null, "Disconnect only the remote test peer");
        // Use the normal scene transition; leaving MP map data in a play scene is invalid.
        GenScene.GoToMainMenu();
        Emit("disconnected", new { });
    }
}

using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using LudeonTK;
using Multiplayer.Client;
using Multiplayer.Common;
using RimWorld;
using RimWorld.Planet;
using Verse;
using MP = Multiplayer.Client.Multiplayer;

namespace Multiplayer.LiveTests;

public static partial class Actions
{
    [DebugAction(Category, "Live: second map", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void SecondMapRequest() => GenerateSecondMap();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void GenerateSecondMap()
    {
        Require(MP.ExecutingCmds, "Map generation must be synchronized");
        Require(State.secondMapId == -1, "Second map already exists");
        var neighbors = new List<PlanetTile>();
        Find.WorldGrid.GetTileNeighbors(Map.Tile, neighbors);
        var tile = neighbors.First(t => !Find.WorldGrid[t].PrimaryBiome.impassable && Find.WorldObjects.MapParentAt(t) == null);
        var parent = (MapParent)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
        parent.Tile = tile;
        parent.SetFaction(Faction.OfPlayer);
        Find.WorldObjects.Add(parent);
        var map = GetOrGenerateMapUtility.GetOrGenerateMap(tile, new IntVec3(75, 1, 75), WorldObjectDefOf.Settlement);
        map.AsyncTime().DesiredTimeSpeed = TimeSpeed.Paused;
        State.secondMapId = map.uniqueID;
        Emit("second_map_generated", new { id = map.uniqueID });
    }

    [DebugAction(Category, "Live: view second map", allowedGameStates = AllowedGameStates.Playing)]
    public static void ViewSecondMap()
    {
        Current.Game.CurrentMap = Find.Maps.First(m => m.uniqueID == State.secondMapId);
        Emit("view_changed", new { id = State.secondMapId });
    }

    [DebugAction(Category, "Live: view first map", allowedGameStates = AllowedGameStates.Playing)]
    public static void ViewFirstMap()
    {
        Current.Game.CurrentMap = Map;
        Emit("view_changed", new { id = State.mapId });
    }

    [DebugAction(Category, "Live: resume first map", allowedGameStates = AllowedGameStates.Playing)]
    public static void ResumeFirstMap()
    {
        MP.Client.SendCommand(CommandType.MapTimeSpeed, State.mapId, (byte)TimeSpeed.Normal);
        Emit("map_speed_requested", new { id = State.mapId });
    }
}

using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LudeonTK;
using Multiplayer.API;
using Multiplayer.Client;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using RimWorld;
using Verse;
using MP = Multiplayer.Client.Multiplayer;

namespace Multiplayer.LiveTests;

public sealed class LiveTestMod : Mod
{
    public LiveTestMod(ModContentPack content) : base(content)
    {
        Sync.RegisterSyncMethod(typeof(Actions), nameof(Actions.CreateFixture)).SetContext(SyncContext.CurrentMap);
        Sync.RegisterSyncMethod(typeof(Actions), nameof(Actions.AddToStack)).SetContext(SyncContext.CurrentMap);
        Log.Message("[MP-LIVE] registered");
    }
}

// Scribed state survives Multiplayer's host conversion and transfer to a joining peer.
public sealed class FixtureState : GameComponent
{
    public int mapId = -1, thingId = -1, zoneId = -1, sequence;
    public FixtureState(Game game) { }
    public override void ExposeData()
    {
        Scribe_Values.Look(ref mapId, "mpLiveMap", -1);
        Scribe_Values.Look(ref thingId, "mpLiveThing", -1);
        Scribe_Values.Look(ref zoneId, "mpLiveZone", -1);
        Scribe_Values.Look(ref sequence, "mpLiveSequence");
    }
}

public static class Actions
{
    public const string Category = "Multiplayer live tests";
    private static FixtureState State => Current.Game.GetComponent<FixtureState>();
    private static Map Map => Find.Maps.First(m => m.uniqueID == State.mapId);
    private static Thing Stack => Map.listerThings.AllThings.First(t => t.thingIDNumber == State.thingId);
    private static Zone_Stockpile Stockpile => (Zone_Stockpile)Map.zoneManager.AllZones.First(z => z.ID == State.zoneId);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Emit(string phase, object data) =>
        Log.Message("[MP-LIVE] " + JsonSerializer.Serialize(new { phase, peer = MP.username, data }));

    [DebugAction(Category, "Live: host", allowedGameStates = AllowedGameStates.Playing)]
    public static void Host()
    {
        Require(MP.Client == null, "Already connected; refusing a second host request");
        Require(Find.Maps.Count == 1, "Host requires a loaded one-map test colony");
        var settings = new ServerSettings
        {
            gameName = "Multiplayer API live tests", direct = true,
            directAddress = "127.0.0.1:30512", lan = false, steam = false,
            syncConfigs = false, debugMode = true, devModeScope = DevModeScope.Everyone,
            desyncTraces = false, pauseOnJoin = true, pauseOnDesync = true,
            autosaveInterval = 0, autoJoinPoint = 0
        };
        Require(HostWindow.HostProgrammatically(settings), "Host startup was rejected");
        Emit("host_requested", new { port = 30512 });
    }

    [DebugAction(Category, "Live: join", allowedGameStates = AllowedGameStates.Invalid)]
    public static void Join()
    {
        Require(MP.Client == null, "Already connected; refusing a second join request");
        ClientUtil.DoubleLongEvent(() =>
            ClientUtil.TryConnectWithWindow(ConnectorRegistry.LiteNet("127.0.0.1", 30512), false), "MpConnecting");
        Emit("join_requested", new { port = 30512 });
    }

    [DebugAction(Category, "Live: snapshot", allowedGameStates = AllowedGameStates.Invalid)]
    public static void Snapshot()
    {
        var state = Current.Game?.GetComponent<FixtureState>();
        var map = state == null ? null : Find.Maps.FirstOrDefault(m => m.uniqueID == state.mapId);
        var stack = map?.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == state.thingId);
        var zone = map?.zoneManager.AllZones.OfType<Zone_Stockpile>().FirstOrDefault(z => z.ID == state.zoneId);
        Emit("snapshot", new
        {
            connected = MP.Client?.State == ConnectionStateEnum.ClientPlaying,
            desynced = MP.session?.desynced ?? false,
            players = MP.session?.players.Where(p => p.status == PlayerStatus.Playing).Select(p => p.username).OrderBy(n => n).ToArray(),
            simulating = TickPatch.Simulating, timer = TickPatch.Timer,
            last_valid_tick = MP.game?.sync.lastValidTick ?? -1,
            game_tick = Current.Game == null ? -1 : Find.TickManager.TicksGame,
            map_id = state?.mapId ?? -1, thing_id = state?.thingId ?? -1,
            zone_id = state?.zoneId ?? -1, sequence = state?.sequence ?? -1,
            stack_count = stack?.stackCount ?? -1,
            allows_steel = zone?.settings.filter.Allows(ThingDefOf.Steel),
            pawns = map?.mapPawns.FreeColonists.OrderBy(p => p.thingIDNumber)
                .Select(p => new { id = p.thingIDNumber, name = p.Name.ToStringFull }).ToArray()
        });
    }

    [DebugAction(Category, "Live: setup stockpile", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void Setup() => CreateFixture();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void CreateFixture()
    {
        Require(MP.ExecutingCmds, "Fixture must execute through Multiplayer synchronization");
        Require(State.thingId == -1, "Fixture exists; start a fresh run before setup");
        var map = Find.CurrentMap;
        var cell = GenRadial.RadialCellsAround(map.Center, 15, true).First(c =>
            c.InBounds(map) && c.Standable(map) && map.zoneManager.ZoneAt(c) == null && c.GetThingList(map).Count == 0);
        var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
        map.zoneManager.RegisterZone(zone);
        zone.AddCell(cell);
        zone.label = "MP live test stockpile";
        var thing = ThingMaker.MakeThing(ThingDefOf.Steel);
        thing.stackCount = 17;
        GenSpawn.Spawn(thing, cell, map);
        thing.SetForbidden(true, false);
        State.mapId = map.uniqueID;
        State.thingId = thing.thingIDNumber;
        State.zoneId = zone.ID;
        State.sequence = 1;
        Emit("fixture_applied", new { State.mapId, State.thingId, State.zoneId });
    }

    [DebugAction(Category, "Live: add stack", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void AddStack() => AddToStack();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void AddToStack()
    {
        Require(MP.ExecutingCmds, "Stack change must execute through Multiplayer synchronization");
        Require(Stack.stackCount < Stack.def.stackLimit, "Fixture stack full");
        Stack.stackCount++;
        State.sequence++;
        Emit("stack_applied", new { State.sequence, count = Stack.stackCount });
    }

    [DebugAction(Category, "Live: disallow steel", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void DisallowSteel() => ChangeFilter(false);

    [DebugAction(Category, "Live: allow steel", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void AllowSteel() => ChangeFilter(true);

    private static void ChangeFilter(bool allow)
    {
        Require(MP.ShouldSync, "Filter request must originate in a connected interface");
        // Invoke the production synchronized handler, including its real wrapper serializer.
        var method = typeof(SyncThingFilters).GetMethod("AllowThing_Helper", BindingFlags.NonPublic | BindingFlags.Static);
        Require(method != null, "Production stockpile filter handler missing");
        method.Invoke(null, new object[] { new TabStorageWrapper(Stockpile), ThingDefOf.Steel, allow });
        Emit("filter_requested", new { allow });
    }

    [DebugAction(Category, "Live: stockpile cache", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void CheckCache()
    {
        var method = typeof(Listing_TreeThingFilter).GetMethod("GetCachedHiddenSpecialFilters", BindingFlags.NonPublic | BindingFlags.Static);
        Require(method != null, "Hidden-filter cache entry point missing");
        var args = new object[] { ThingCategoryNodeDatabase.RootNode, Stockpile.settings.filter };
        var expected = method.Invoke(null, args);
        for (var i = 0; i < 100; i++)
            Require(ReferenceEquals(expected, method.Invoke(null, args)), "Repeated lookup did not reuse cached value");
        Emit("cache_checked", new { lookups = 101, State.zoneId });
    }

    [DebugAction(Category, "Live: resume", allowedGameStates = AllowedGameStates.Playing)]
    public static void Resume() => SetSpeed(TimeSpeed.Normal);

    [DebugAction(Category, "Live: pause", allowedGameStates = AllowedGameStates.Playing)]
    public static void Pause() => SetSpeed(TimeSpeed.Paused);

    private static void SetSpeed(TimeSpeed speed)
    {
        Require(MP.Client?.State == ConnectionStateEnum.ClientPlaying, "No playing connection");
        MP.Client.SendCommand(CommandType.GlobalTimeSpeed, ScheduledCommand.Global, (byte)speed);
        Emit("speed_requested", new { speed = speed.ToString() });
    }
}

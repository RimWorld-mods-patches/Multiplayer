using System.Diagnostics;
using LiteNetLib;
using Multiplayer.Common;

namespace Tests;

[NonParallelizable]
public class ServerTest
{
    private List<Action> teardownActions = new();

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        ServerLog.detailEnabled = true;
        ServerLog.verboseEnabled = true;
        ServerLog.error = Assert.Fail;
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var action in teardownActions)
            action();
        teardownActions.Clear();
    }

    [Test]
    public void Test()
    {
        var server = MakeServer(out var port);
        ConnectClient(port, typeof(TestJoiningState));

        WaitUntil(
            nameof(Test),
            () => server.InitDataState == InitDataState.Complete && server.playerManager.Players.Count == 0,
            () => $"InitDataState={server.InitDataState} Players={server.playerManager.Players.Count}");
    }

    [Test]
    public async Task JoinPointAbortUnblocksWaiters()
    {
        var server = new MultiplayerServer(new ServerSettings
        {
            gameName = "Test",
            direct = false,
            lan = false
        });

        Assert.That(server.worldData.TryStartJoinPointCreation(true), Is.True);
        Assert.That(server.worldData.CreatingJoinPoint, Is.True);

        var waitTask = server.worldData.WaitJoinPoint();
        Assert.That(waitTask.IsCompleted, Is.False);

        server.worldData.AbortJoinPointCreation();

        var completed = await waitTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.That(completed, Is.SameAs(server.worldData));
        Assert.That(server.worldData.CreatingJoinPoint, Is.False);
        Assert.That(server.worldData.WaitJoinPoint().IsCompleted, Is.True);
    }

    [Test]
    public void LoadingStateHandlesKeepAliveWhileWaitingForJoinPoint()
    {
        var server = MakeServer(out var port);
        Assert.That(server.worldData.TryStartJoinPointCreation(true), Is.True);

        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            server.worldData.EndJoinPointCreation();
        });

        ConnectClient(port, typeof(TestLoadingKeepAliveState));

        WaitUntil(
            nameof(LoadingStateHandlesKeepAliveWhileWaitingForJoinPoint),
            () => server.playerManager.Players.Count == 0,
            () => $"Players={server.playerManager.Players.Count} " +
                  $"CreatingJoinPoint={server.worldData.CreatingJoinPoint}");
    }

    [Test]
    public void StandaloneJoinWithExistingPlayer_DoesNotStartJoinPoint()
    {
        var server = MakeServer(out var port);
        server.IsStandaloneServer = true;

        var existingConn = new RecordingConnection("existing");
        existingConn.ChangeState(ConnectionStateEnum.ServerPlaying);
        var existingPlayer = new ServerPlayer(100, existingConn);
        existingConn.serverPlayer = existingPlayer;
        server.playerManager.Players.Add(existingPlayer);

        ConnectClient(port, typeof(TestJoiningState));

        WaitUntil(
            nameof(StandaloneJoinWithExistingPlayer_DoesNotStartJoinPoint),
            () => server.playerManager.Players.Count == 1,
            () => $"Players={server.playerManager.Players.Count} " +
                  $"CreatingJoinPoint={server.worldData.CreatingJoinPoint}");

        Assert.That(server.worldData.CreatingJoinPoint, Is.False);
    }

    /// <summary>
    /// Polls until <paramref name="condition"/> holds, or fails with what was actually observed.
    ///
    /// These tests drive a real server on a real socket, so how long the condition takes depends on the
    /// machine. "Timeout" on its own says only that something did not happen; it does not say which half
    /// of a compound condition was still false, nor how close to the budget the run came. Both are needed
    /// to choose a defensible budget rather than a superstitious one.
    ///
    /// Every wait emits one measurement line, on success as well as failure, so a repeated run yields the
    /// distribution instead of a single anecdote. Written through TestContext.Progress because NUnit only
    /// surfaces captured Console output for tests that fail, and the successful runs are the interesting
    /// ones here.
    /// </summary>
    private static void WaitUntil(string label, Func<bool> condition, Func<string> describeState,
        int timeoutMs = 2000)
    {
        var watch = Stopwatch.StartNew();
        var polls = 0;

        while (true)
        {
            polls++;

            if (condition())
            {
                Report(label, "ok", watch.ElapsedMilliseconds, polls, describeState());
                return;
            }

            if (watch.ElapsedMilliseconds > timeoutMs)
            {
                var state = describeState();
                Report(label, "timeout", watch.ElapsedMilliseconds, polls, state);
                Assert.Fail($"{label} timed out after {watch.ElapsedMilliseconds} ms " +
                            $"({polls} polls, budget {timeoutMs} ms); observed {state}");
            }

            Thread.Sleep(50);
        }
    }

    /// <summary>One greppable line per wait. Prefixed so a harness can pick it out of ordinary test output.</summary>
    private static void Report(string label, string outcome, long elapsedMs, int polls, string state) =>
        TestContext.Progress.WriteLine(
            $"##WAIT## label={label} outcome={outcome} elapsed_ms={elapsedMs} polls={polls} state=[{state}]");

    private void ConnectClient(int port, Type joiningStateType)
    {
        var clientListener = new TestNetListener(joiningStateType);
        var client = new NetManager(clientListener);
        client.Start();
        var peer = client.Connect("127.0.0.1", port, "");

        teardownActions.Add(() => client.Stop());

        Console.WriteLine($"Connected to {peer}");

        new Thread(() =>
        {
            while (client.IsRunning)
            {
                client.PollEvents();
                Thread.Sleep(50);
            }
        }) { IsBackground = true }.Start();
    }

    private MultiplayerServer MakeServer(out int port)
    {
        var server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings
        {
            gameName = "Test",
            direct = true,
            directAddress = "127.0.0.1:0", // 0 makes the OS choose any free port
            lan = false,
            autoJoinPoint = 0 // Disable: test server has no game simulation to complete join points
        })
        {
            running = true
        };

        server.worldData.savedGame = Array.Empty<byte>();

        var badEndpoint = server.settings.TryParseEndpoints(out var endpoints);
        Assert.That(badEndpoint, Is.Null);
        var success = LiteNetManager.Create(server, endpoints, out var liteNet);
        Assert.That(success, Is.True);
        server.netManagers.Add(liteNet);

        port = liteNet.netManagers[0].manager.LocalPort;

        var serverThread = new Thread(server.Run) { IsBackground = true };
        serverThread.Start();

        teardownActions.Add(() =>
        {
            server.running = false;
            serverThread.Join(timeout: TimeSpan.FromSeconds(2));
        });

        return server;
    }
}

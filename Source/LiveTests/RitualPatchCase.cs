using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using Verse;

namespace Multiplayer.LiveTests;

public static partial class Actions
{
    [DebugAction(Category, "Live: ritual hooks", allowedGameStates = AllowedGameStates.Playing)]
    public static void CheckRitualHooks()
    {
        var loader = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "MultiplayerLoader");
        var type = loader.GetType("MultiplayerLoader.SyncRituals", true);
        var registrations = (IDictionary)type.GetField("runtimeSyncMethods", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        Require(registrations.Count == 10, "Expected five registrations per ritual widget type");
        var targets = registrations.Keys.Cast<object>().Select(key => ((Type, string))key).ToArray();
        Require(targets.GroupBy(key => key.Item1).Count() == 2, "Both ritual widget types must be registered");
        foreach (var (widget, method) in targets)
        {
            var patches = Harmony.GetPatchInfo(AccessTools.Method(widget, method));
            Require(patches != null && patches.Prefixes.Any(p => p.owner == "rwmt.Multiplayer.Rituals"),
                "Missing live Harmony ritual prefix: " + widget.Name + "." + method);
        }
        Emit("ritual_hooks_checked", new { registrations = targets.Length,
            widgets = targets.Select(t => t.Item1.Name).Distinct().ToArray(),
            ideology = ModsConfig.IdeologyActive, anomaly = ModsConfig.AnomalyActive });
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using Verse;

namespace Multiplayer.Client.Patches
{
    // Temporary diagnostics for quest sites appearing at different world tiles on different
    // clients (the FastTileFinder batch race was one cause, #893; this instruments the whole
    // path to find any remaining one). Every entry lands in MpQuestSiteDebug.log in the
    // RimWorld save-data folder so two players can diff their files directly — Player.log
    // truncates after enough messages, this file does not.
    static class QuestSiteDebugLog
    {
        private static string path;
        private static bool announced;

        public static void Write(string entry)
        {
            try
            {
                path ??= Path.Combine(GenFilePaths.SaveDataFolderPath, "MpQuestSiteDebug.log");

                if (!announced)
                {
                    announced = true;
                    File.AppendAllText(path, $"\n===== session {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\n");
                    Log.Message($"[MP-SITE-DEBUG] Writing quest site debug log to {path}");
                }

                File.AppendAllText(path, $"[{Context()}] {entry}\n");
            }
            catch (Exception e)
            {
                if (!announced)
                {
                    announced = true;
                    Log.Warning($"[MP-SITE-DEBUG] Failed to write debug log: {e.Message}");
                }
            }
        }

        // One compact prefix identifying who/when/where-in-the-sync-model the entry came from:
        // H=host C=client, game tick, rand state, and which execution context is active —
        // T=synced ticking, X=executing synced commands, I=interface (unsynced!), n=none.
        private static string Context()
        {
            var role = Multiplayer.LocalServer != null ? "H" : "C";
            var tick = Find.TickManager?.TicksGame ?? -1;
            var ctx = Multiplayer.Ticking ? "T" : Multiplayer.ExecutingCmds ? "X" : Multiplayer.InInterface ? "I" : "n";
            return $"{role} tick={tick} rand={Rand.StateCompressed} ctx={ctx}";
        }

        public static string ShortStack()
        {
            var lines = Environment.StackTrace.Split('\n');
            // Skip the StackTrace/ShortStack/patch frames at the top.
            return string.Join("\n", lines.Skip(4).Take(16).Select(l => "    " + l.Trim()));
        }

        public static string Describe(object value)
        {
            try
            {
                switch (value)
                {
                    case null:
                        return "null";
                    case string s:
                        return s;
                    case IEnumerable en:
                    {
                        var items = en.Cast<object>().Select(x => x?.ToString() ?? "null").ToList();
                        var shown = string.Join(",", items.Take(60));
                        return items.Count > 60 ? $"[{shown} …{items.Count} total]" : $"[{shown}] n={items.Count}";
                    }
                }

                var t = value.GetType();
                if (t.IsPrimitive || t.IsEnum)
                    return value.ToString();

                // Structs like tile query params have no useful ToString — dump their fields.
                var fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
                if (t.IsValueType && fields.Length > 0 && fields.Length <= 12)
                    return $"{t.Name}{{{string.Join(", ", fields.Select(f => $"{f.Name}={f.GetValue(value) ?? "null"}"))}}}";

                return value.ToString();
            }
            catch (Exception e)
            {
                return $"<describe failed: {e.GetType().Name}>";
            }
        }
    }

    // Every world object entering the world, with the stack that created it. If the two
    // players' files show the same site created from different stacks (or one in ctx=I),
    // the creator is the culprit.
    [HarmonyPatch(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.Add))]
    static class DebugLogWorldObjectAdd
    {
        static void Postfix(WorldObject o)
        {
            if (Multiplayer.Client == null || o == null || o is Caravan) return;

            string tile;
            try { tile = o.Tile.ToString(); }
            catch { tile = "<?>"; }

            QuestSiteDebugLog.Write(
                $"WorldObjectAdd {o.GetType().Name} def={o.def?.defName} tile={tile} faction={o.Faction?.Name ?? "null"}\n{QuestSiteDebugLog.ShortStack()}");
        }
    }

    // The candidate tile sets. If host and client log different tile lists for the same
    // query at the same rand state, candidate enumeration is still non-deterministic
    // despite the single-batch fix.
    [HarmonyPatch(typeof(FastTileFinder), nameof(FastTileFinder.Query))]
    static class DebugLogFastTileFinderQuery
    {
        static void Prefix(ref ulong __state) => __state = Rand.StateCompressed;

        static void Postfix(object[] __args, object __result, ulong __state)
        {
            if (Multiplayer.Client == null) return;

            var args = string.Join(" | ", __args.Select(QuestSiteDebugLog.Describe));
            QuestSiteDebugLog.Write(
                $"FastTileFinder.Query randBefore={__state}\n  args: {args}\n  result: {QuestSiteDebugLog.Describe(__result)}");
        }
    }

    // The quest as generated: script, id, and the tiles its parts point at. Lets us match a
    // divergent site back to the quest generation event and its rand state.
    [HarmonyPatch(typeof(QuestGen), nameof(QuestGen.Generate))]
    static class DebugLogQuestGenerate
    {
        static void Postfix(Quest __result)
        {
            if (Multiplayer.Client == null || __result == null) return;

            var sb = new StringBuilder();
            sb.Append($"QuestGen.Generate id={__result.id} root={__result.root?.defName} name={__result.name}");

            foreach (var part in __result.PartsListForReading)
            {
                if (part is QuestPart_SpawnWorldObject spawn && spawn.worldObject != null)
                {
                    string tile;
                    try { tile = spawn.worldObject.Tile.ToString(); }
                    catch { tile = "<?>"; }
                    sb.Append($"\n  part SpawnWorldObject def={spawn.worldObject.def?.defName} tile={tile}");
                }
            }

            QuestSiteDebugLog.Write(sb.ToString());
        }
    }
}

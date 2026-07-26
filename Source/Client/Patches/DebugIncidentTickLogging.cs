using System.Text;
using HarmonyLib;
using Multiplayer.Client.AsyncTime;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Client.Patches
{
    // DEBUG-ONLY diagnostics for "client-generated event" desyncs.
    //
    // Every incident/letter label is resolved with .Translate() against the LOCAL machine's
    // active language, so the locale of a shown event name reveals which machine generated it.
    // An event that fires OUTSIDE the deterministic synced tick (Multiplayer.Ticking == false)
    // is generated client-locally and is a prime desync suspect.
    //
    // These patches log every letter and every fired incident together with the tick context and
    // the active language. Off-tick events are logged as warnings ("OFF-TICK") with a stack trace
    // so the originating code path can be identified.
    //
    // This whole file is a temporary investigation aid and is not meant to be merged.
    static class DebugTickContext
    {
        public static string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("ticking=").Append(Multiplayer.Ticking);
            sb.Append(" world=").Append(AsyncWorldTimeComp.tickingWorld);
            sb.Append(" map=").Append(AsyncTimeComp.tickingMap?.ToString() ?? "null");
            sb.Append(" execCmds=").Append(Multiplayer.ExecutingCmds);
            sb.Append(" lang=").Append(LanguageDatabase.activeLanguage?.folderName ?? "?");
            return sb.ToString();
        }

        public static void Report(string what)
        {
            if (Multiplayer.Ticking)
                Log.Message($"[MP-DBG] {what} | {DebugTickContext.Describe()}");
            else
                Log.Warning(
                    $"[MP-DBG] {what} | {DebugTickContext.Describe()}  <-- OFF-TICK (client-local; likely desync source)\n" +
                    StackTraceUtility.ExtractStackTrace());
        }
    }

    [HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter),
        new[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) })]
    static class DebugLogReceiveLetter
    {
        static void Prefix(Letter let)
        {
            if (Multiplayer.Client == null) return;
            DebugTickContext.Report($"Letter def={let?.def?.defName} label='{let?.Label}'");
        }
    }

    [HarmonyPatch(typeof(Storyteller), nameof(Storyteller.TryFire))]
    static class DebugLogTryFireIncident
    {
        static void Postfix(FiringIncident fi, bool __result)
        {
            if (Multiplayer.Client == null || !__result) return;
            DebugTickContext.Report($"Incident def={fi?.def?.defName} target={fi?.parms?.target}");
        }
    }
}

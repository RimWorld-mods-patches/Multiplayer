using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using Multiplayer.Client;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.LiveTests;

public static partial class Actions
{
    private sealed class InjectedUiFailure : Exception { }

    private static bool injectStorageFailure;

    private static void ThrowAfterUiPrefix()
    {
        var markers = typeof(Sync).Assembly.GetType("Multiplayer.Client.ThingFilterMarkers", true);
        if (injectStorageFailure)
            Require(markers.GetField("thingFilterContext", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null) != null,
                "Production storage prefix did not set the context");
        else
            Require((bool)markers.GetField("drawingThingFilter", BindingFlags.Public | BindingFlags.Static).GetValue(null),
                "Production UI prefix did not set the drawing marker");
        throw new InjectedUiFailure();
    }

    [DebugAction(Category, "Live: marker cleanup", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void CheckMarkerCleanup()
    {
        var markers = typeof(Sync).Assembly.GetType("Multiplayer.Client.ThingFilterMarkers", true);
        var drawing = markers.GetField("drawingThingFilter", BindingFlags.Public | BindingFlags.Static);
        var context = markers.GetField("thingFilterContext", BindingFlags.NonPublic | BindingFlags.Static);
        Require(!(bool)drawing.GetValue(null) && context.GetValue(null) == null, "Markers dirty before test");
        var harmony = new Harmony("rwmt.live-tests.exception-injection");
        var method = AccessTools.Method(typeof(ThingFilterUI), nameof(ThingFilterUI.DoThingFilterConfigWindow));
        try
        {
            injectStorageFailure = false;
            // Throw after the production prefix sets the marker, inside the real finalizer pipeline.
            var injection = new HarmonyMethod(typeof(Actions), nameof(ThrowAfterUiPrefix)) { priority = Priority.Last };
            harmony.Patch(method, prefix: injection);
            try
            {
                ThingFilterUI.DoThingFilterConfigWindow(default(Rect), new ThingFilterUI.UIState(), Stockpile.settings.filter);
                throw new InvalidOperationException("Injected exception did not escape");
            }
            catch (InjectedUiFailure) { }
            Require(!(bool)drawing.GetValue(null), "Drawing marker leaked after exception");

            method = AccessTools.Method(typeof(ITab_Storage), "FillTab");
            injectStorageFailure = true;
            harmony.Patch(method, prefix: injection);
            var selected = Find.Selector.SelectedObjectsListForReading.ToArray();
            try
            {
                Find.Selector.ClearSelection();
                Find.Selector.Select(Stockpile);
                try
                {
                    method.Invoke(new ITab_Storage(), null);
                    throw new InvalidOperationException("Storage exception did not escape");
                }
                catch (TargetInvocationException e) when (e.InnerException is InjectedUiFailure) { }
                Require(context.GetValue(null) == null, "Storage filter context leaked after exception");
            }
            finally
            {
                Find.Selector.ClearSelection();
                foreach (var item in selected) Find.Selector.Select(item);
            }
            Emit("markers_checked", new { drawing_cleared = true, storage_context_cleared = true });
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            injectStorageFailure = false;
        }
    }
}

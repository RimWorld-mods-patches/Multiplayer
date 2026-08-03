using System.Collections.Generic;
using Multiplayer.API;
using Multiplayer.Common;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Sound;

namespace Multiplayer.Client.Persistent
{
    /// <summary>
    /// Represents an active Caravan Split session. This session will track all the pawns and items being split.
    /// </summary>
    public class CaravanSplittingSession
        : ExposableSession, ISessionWithTransferables, ISessionWithCreationRestrictions, IFactionScopedPauseSession
    {
        // This session is only possible on the world, so the map is always null.
        public override Map Map => null;

        /// <summary>
        /// The list of items that can be transferred, along with their count.
        /// </summary>
        private List<TransferableOneWay> transferables;

        /// <summary>
        /// Flag used to indicate that the ui needs to be redrawn.
        /// </summary>
        public bool uiDirty;

        /// <summary>
        /// The caravan being split.
        /// </summary>
        public Caravan Caravan
        {
            get => caravan;
            private set => caravan = value;
        }
        private Caravan caravan;

        /// <summary>
        /// Reference to the dialog that is being displayed.
        /// </summary>
        public CaravanSplittingProxy dialog;

        public override bool IsSessionValid => Caravan != null;

        // Used when saving and loading
        public CaravanSplittingSession(Map map) : base(null)
        {
        }

        /// <summary>
        /// Handles creation of new CaravanSplittingSession.
        /// </summary>
        /// <param name="caravan"></param>
        public static CaravanSplittingSession Of(Caravan caravan)
        {
            var session = new CaravanSplittingSession(null);
            session.Caravan = caravan;
            session.AddItems();
            return session;
        }

        private void AddItems()
        {
            CaravanSplittingProxy.CreatingProxy = true;
            dialog = new CaravanSplittingProxy(Caravan) {
                session = this
            };
            CaravanSplittingProxy.CreatingProxy = false;
            dialog.CalculateAndRecacheTransferables();
            transferables = dialog.transferables;

            Find.WindowStack.Add(dialog);
        }

        /// <summary>
        /// Opens the dialog for a currently ongoing session. This should only be called
        /// when the dialog has been closed but the session still running.
        /// I.E. one player has closed the window without accepting/cancelling the session.
        /// </summary>
        public void OpenWindow(bool sound = true)
        {
            dialog = PrepareDialogProxy();
            if (!sound)
                dialog.soundAppear = null;

            CaravanUIUtility.CreateCaravanTransferableWidgets(
                transferables,
                out dialog.pawnsTransfer,
                out dialog.itemsTransfer,
                out dialog.foodAndMedicineTransfer,
                "SplitCaravanThingCountTip".Translate(),
                IgnorePawnsInventoryMode.Ignore,
                () => dialog.DestMassCapacity - dialog.DestMassUsage,
                false,
                Caravan.Tile
            );

            dialog.CountToTransferChanged();

            Find.WindowStack.Add(dialog);
        }

        private CaravanSplittingProxy PrepareDialogProxy()
        {
            CaravanSplittingProxy.CreatingProxy = true;
            var newProxy = new CaravanSplittingProxy(Caravan)
            {
                transferables = transferables,
                session = this
            };
            CaravanSplittingProxy.CreatingProxy = false;

            return newProxy;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref caravan, "caravan");
            Scribe_Collections.Look(ref transferables, "transferables", LookMode.Deep);
        }

        /// <summary>
        /// Find Transferable by thingId
        /// </summary>
        public Transferable GetTransferableByThingId(int thingId)
        {
            return transferables.Find(tr => tr.things.Any(t => t.thingIDNumber == thingId));
        }

        /// <summary>
        /// Sets the uiDirty flag.
        /// </summary>
        /// <param name="tr"></param>
        public void Notify_CountChanged(Transferable tr)
        {
            uiDirty = true;
        }

        /// <summary>
        /// Cancel a splitting session without accepting it. Closes the dialog and frees Multiplayer.WorldComp.splitSession
        /// </summary>
        [SyncMethod]
        public void CancelSplittingSession() {
            dialog.Close();
            Multiplayer.WorldComp.sessionManager.RemoveSession(this);
        }

        /// <summary>
        /// Resets the counts on all the transferables to 0.
        /// </summary>
        [SyncMethod]
        public void ResetSplittingSession()
        {
            transferables.ForEach(t => t.CountToTransfer = 0);
            uiDirty = true;
        }

        /// <summary>
        /// Accept the splitting session, split the caravan in to two caravans, free Multiplayer.WorldComp.splitSession and close the dialog.
        /// If the caravan fails to split, nothing will happen.
        /// </summary>
        [SyncMethod]
        public void AcceptSplitSession()
        {
            if (dialog.TrySplitCaravan())
            {
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
                dialog.Close(false);
                Multiplayer.WorldComp.sessionManager.RemoveSession(this);
            }
        }

        public override FloatMenuOption GetBlockingWindowOptions(ColonistBar.Entry entry)
        {
            if (!Caravan.pawns.Contains(entry.pawn))
                return null;

            return new FloatMenuOption("MpCaravanSplittingSession".Translate(), () =>
            {
                SwitchToMapOrWorld(entry.map);
                CameraJumper.TryJumpAndSelect(entry.pawn);
                OpenWindow();
            });
        }

        /// <summary>
        /// The faction whose caravan is being split. Derived from the caravan rather than stored, so saves
        /// written before this change still load without a migration step.
        /// </summary>
        public int PauseOwnerFactionId => Caravan?.Faction?.loadID ?? PauseDomainRules.NoFaction;

        public EncounterPausePolicy PausePolicy => CaravanEncounterPolicy.SelectPolicy();

        public bool IsPauseActive => IsSessionValid;

        /// <summary>
        /// Splitting used to pause unconditionally, which was a reasonable shortcut while there was no
        /// shared rule to reuse -- but it means one player opening the split dialog stops every other
        /// faction's colony outright.
        ///
        /// Now it answers the same way a caravan encounter does. On the servers that cannot support
        /// ownership scoping -- synchronized time, or a single player faction -- SelectPolicy returns
        /// GlobalFallback and this stays true for everything, exactly as before.
        /// </summary>
        public override bool IsCurrentlyPausing(Map map)
        {
            if (!IsPauseActive)
                return false;

            return CaravanEncounterRules.PolicyPauses(
                PausePolicy,
                isWorldTickable: map == null,
                isMapInOwnerDomain: map != null && PauseDomains.IsMapInPauseDomain(map, PauseOwnerFactionId));
        }

        public bool CanExistWith(Session other) => other is not CaravanSplittingSession;
    }
}

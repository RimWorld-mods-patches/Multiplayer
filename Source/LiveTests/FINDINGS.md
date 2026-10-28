# Live-test findings

## Cursor packets arrive while a rejoining client is loading

Observed on RimWorld 1.7.4990 with the current runtime-fixes Multiplayer build.
The API-only run `live-suite-20261028-020753-752986` passed all 15 implemented
case assertions. Ideology/Anomaly ritual scenarios were blocked by unavailable
DLC. The overall report is FAIL because the client logged eight instances of:

```text
INFO MpServerLog Error: No handler for packet Server_Cursor in state ClientLoading
```

Reproduce with MP12: host a disposable game, connect a peer, create a production
join point, then invoke `Rejoiner.DoRejoin` on the remote peer. Cursor updates
continue while that peer downloads/reconstructs its game. The rejoin completes,
its fixture remains intact, and later synchronization checks pass. No engine
exception occurred in the final run.

The server's `ServerPlayingState.HandleCursor` broadcasts with `SendToIngame`.
The client handles cursor updates in `ClientPlayingState`, while `ClientLoadingState`
has no matching handler. A loading transition can also leave already-sent
unreliable cursor packets in flight. The evidence establishes an unhandled-packet
diagnostic during rejoin; it does not establish simulation data loss.

The test reporter includes semantic network errors even when the outer engine
severity is INFO. This finding remains unfixed in the production mod; no test-only
patch suppresses the messages.

## Coverage limitations

- MP09/MP10 do not have full ritual fixtures yet: Ideology/Anomaly are absent
  from this installation. MP16 verifies live Harmony 3 registration/prefix
  installation for all ten normal/psychic widget targets; that does not prove
  ritual role assignment serialization or ritual execution.
- MP14 tests real finalizers after a deliberately thrown final prefix. It
  verifies marker initialization and exception cleanup, without rendering a
  full storage tab.
- MP15 deliberately creates a local RNG mismatch as the terminal case. A
  detected desync is required for this case to pass. All earlier cases require
  healthy peers and matching fixture state.

Failed development runs are retained separately in ignored artifacts. The initial
reload probe called SaveAndReload locally and bypassed the join-point protocol;
the final test uses the production autosaving/join-point request. The initial
disconnect probe omitted the normal return-to-menu transition; the final fresh
connection test uses `GenScene.GoToMainMenu` and waits for the entry scene.

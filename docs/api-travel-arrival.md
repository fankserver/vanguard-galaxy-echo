# Arrival-snap on VGModAPI travel observations

Arrival-snap zeroes `IdleManager.updateTimer` when a route genuinely finishes, so
the autopilot's next decision runs on the following frame instead of up to 12 s
later. Until 0.7.0 it did that from its own Harmony postfix on
`TravelManager.TravelToNextWaypoint`. It now consumes VGModAPI's public
`ITravelEvents` `RouteCompleted` fact instead, and owns no travel hook at all.

ETA-sync is a separate feature and is unchanged: it keeps its own postfix on
`IdleManager.Update` and its own distance math.

## Why the same guards still hold

The retired postfix asked the native manager two questions before snapping:
`GamePlayer.current.waypoints.Count == 0` and `TravelManager.TravelActive() == false`
(which returns `usingJumpgate` when there is no travel coroutine, so an
unfinished gate hop still counts as active).

VGModAPI asks exactly those two questions, at exactly the same place.
`TravelPatches.RouteBoundary.Postfix` runs on the same native
`TravelToNextWaypoint` call and delegates to `TravelNativeAdapter.CheckRouteBoundary`,
which emits `RouteCompleted` only when

```csharp
if (_bindings.WaypointCount(player) == 0 && !_bindings.TravelActive(travelManager))
```

and only for a leg the adapter has already recorded a *verified arrival* for
(`_lastCompleted`), in the API's current session, for a live bound player, and at
most once per leg (`_routeCompleted`). So the API's precondition is the old guard
plus arrival attribution and session ownership. VGEcho does not re-read native
travel state; `InstalledGameMetadataTests` pins the native shapes those claims
depend on.

## Frame ordering

Native `Behaviour.Gameplay.IdleManager.Update`:

```csharp
updateTimer -= Time.deltaTime;
if (updateTimer < 0f) FindActivity();
```

The two native routes into the boundary, from the installed 0.8.2.3
`Assembly-CSharp.dll`:

- **In-system final leg** — `StartTravel` → `Travel()` ends with
  `localPoiManager.SpaceshipHasArrived(); TravelToNextWaypoint();`. The arrival
  and the boundary happen in one synchronous statement pair, and
  `TravelToNextWaypoint` sets `travelCoroutine = null` on its empty-waypoint
  branch *before* the postfix runs, so `TravelActive()` is already false there.
- **Jump gate / wormhole** — `JumpToSystem` / `JumpToWormhole` end with
  `usingJumpgate = false; TravelToNextWaypoint();`. `JumpToPOIFrom` starts those
  iterators *without* assigning `travelCoroutine`, so at the terminal call
  `travelCoroutine` is null and `usingJumpgate` is already false:
  `TravelActive()` is false at the postfix, not merely "about to be".

The API hub dispatches synchronously (a fact emitted from inside a callback is
queued and drained inside the same `Emit`), so Echo's callback runs inside that
native call, in the coroutine phase of frame *N* — after every `Update` of frame
*N*. `updateTimer` is therefore 0 when `IdleManager.Update` next runs, in frame
*N+1*, which drives it negative and calls `FindActivity`. That is the same frame
the retired postfix produced.

Because the fact cannot be delivered before those conditions hold, there is **no
pending or deferred snap** and no lease to expire: the callback either snaps now
or does nothing. Nothing can be resurrected at a later idle tick.

If the manager is not live at that moment, `Singleton<IdleManager>.Current` is
null (Unity fake-null included) and the vanilla cycle simply runs. `Current` is
a pure read of the already-registered singleton; `Instance` would run
`FindAnyObjectByType` and write the shared static cache, which an observer has
no business doing.

## What never snaps

`ArrivalSnapReducer` rejects, each with its own reason:

| Situation | Decision |
|---|---|
| Initial or recovered placement (load, first verified position) | `NotFinalRoute` |
| Request, departure, cancellation | `NotFinalRoute` |
| Intermediate `Arrived` (per-hop gate, wormhole, fast-lane chain) | `NotFinalRoute` |
| A kind a newer API adds | `NotFinalRoute` (projected to `Unrecognized`) |
| Fact for a session the service no longer owns, or no session | `ForeignSession` |
| Replayed / out-of-order sequence | `StaleSequence` |
| Completion without a leg identity | `MissingOperation` |
| Second completion of the same leg, including reentrant delivery | `DuplicateRouteCompletion` |
| `TimingEnabled` or `ArrivalSnap` off | `FeatureDisabled` |
| Autopilot not engaged | `AutopilotDisengaged` |
| Observer disposed, or a previous callback faulted it | `ServiceStopped` |

A session replacement resets sequence ordering and the per-leg accounting, so a
freshly loaded session starts clean and old evidence cannot act on it. Consuming
a leg happens before the config gates, so re-enabling a toggle after a route
completed cannot make that route snap late.

## Soft dependency shape

VGModAPI is a `SoftDependency`. When it is absent, `VGModAPI.Abstractions.dll` is
absent too, and Mono resolves a method's type tokens when it *compiles* that
method — before any `try` inside it can catch anything. So:

- no field, parameter or return type on a plugin member names an API type;
- every API token lives in `TravelArrivalBridge` (two `[MethodImpl(NoInlining)]`
  entry points) and `TravelArrivalObserver`;
- `Plugin.Awake` checks `Chainloader.PluginInfos` for `vgmodapi` first and only
  calls into the bridge inside a `try` when the plugin is actually loaded.

`PluginAssemblyShapeTests` reads the built `VGEcho.dll` with Cecil and fails if
any other type acquires an API reference, if either bridge entry point loses
`NoInlining`, or if the build output ever contains a copy of the API assembly.

Admission (`ArrivalSnapBinding.Evaluate`) requires an installed version in
`[0.1.9, 0.2.0)`, a non-null `ModApi.Travel`, and the API reporting its
`native-travel` capability as available. Anything else logs a specific reason and
leaves arrival-snap off. There is no fallback to a direct `TravelManager` hook,
and no other VGEcho feature is affected.

## Status

Host tests only. VGModAPI's native travel group is itself experimental and
unqualified at runtime; this consumer inherits that status. Protected in-game
qualification of the real frame ordering — a true final arrival snapping the
timer before the next idle decision, with no mid-route or on-load firing — is a
separate exercise and is not claimed here.

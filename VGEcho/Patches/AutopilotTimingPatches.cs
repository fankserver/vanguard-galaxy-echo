using Behaviour.Gameplay;
using Behaviour.Managers;
using Behaviour.Util;
using HarmonyLib;
using Source.Player;
using UnityEngine;

namespace VGEcho.Patches;

/// <summary>
/// Autopilot cycle timing. Two mechanisms, one shared master toggle:
///   • <b>ETA-sync</b> (postfix on <c>IdleManager.Update</c>): while the ship is
///     warping, overwrite <c>updateTimer</c>/<c>updateTimerBase</c> with the live
///     travel ETA so the green progress circle visibly completes on drop-out.
///   • <b>Arrival-snap</b> (<see cref="ApplyArrivalSnap"/>): when a genuine final
///     route completes, zero <c>updateTimer</c> so the next
///     <c>IdleManager.Update</c> tick immediately triggers <c>FindActivity</c>.
///
/// Arrival-snap owns no Harmony patch. Its trigger is VGModAPI's public
/// <c>ITravelEvents.RouteCompleted</c> fact, delivered through
/// <see cref="Travel.TravelArrivalObserver"/>; see
/// <see cref="Travel.ArrivalSnapReducer"/> for why that is the same native
/// boundary the retired <c>TravelManager.TravelToNextWaypoint</c> postfix used.
///
/// Both engage only when <c>GamePlayer.current.autoPlay</c> is true and the
/// matching config entry is enabled. Private setters on <c>IdleManager</c>'s
/// auto-properties are written via <see cref="AccessTools.FieldRefAccess"/>
/// against the C# compiler-generated backing fields.
/// </summary>
[HarmonyPatch]
internal static class AutopilotTimingPatches
{
    // Compiler-generated backing-field names for auto-properties on IdleManager.
    // If the game is ever recompiled with different property names, update these.
    private static readonly AccessTools.FieldRef<IdleManager, float> UpdateTimerRef =
        AccessTools.FieldRefAccess<IdleManager, float>("<updateTimer>k__BackingField");

    private static readonly AccessTools.FieldRef<IdleManager, float> UpdateTimerBaseRef =
        AccessTools.FieldRefAccess<IdleManager, float>("<updateTimerBase>k__BackingField");

    // Tracks whether our previous Update tick saw isWarping=true. Used so we
    // can capture the initial updateTimerBase at warp start and allow the
    // vanilla IdleManager cycle to resume naturally after warp ends.
    private static bool _wasSyncing;

    /// <summary>
    /// Echo's own arrival-snap gates, read live when a travel fact arrives:
    /// the master timing toggle, the arrival-snap toggle and whether the player
    /// is actually on autopilot. Same three conditions the retired
    /// <c>TravelToNextWaypoint</c> postfix checked first, in the same order.
    /// </summary>
    internal static Travel.ArrivalSnapGates ReadArrivalSnapGates()
    {
        var player = GamePlayer.current;
        return new Travel.ArrivalSnapGates(
            Plugin.Instance.CfgAutopilotTiming.Value,
            Plugin.Instance.CfgAutopilotArrivalSnap.Value,
            player != null && player.autoPlay);
    }

    /// <summary>
    /// Zeroes <c>IdleManager.updateTimer</c> so the very next
    /// <see cref="IdleManager.Update"/> tick drops it below zero and calls
    /// <c>FindActivity</c> — eliminating the residual 0–12s wait between
    /// drop-out and the next autonomous action.
    ///
    /// <para>Called only from the API's <c>RouteCompleted</c> callback, which
    /// the API dispatches synchronously from its postfix on the same native
    /// <c>TravelManager.TravelToNextWaypoint</c> call Echo used to patch. That
    /// is the coroutine phase of the frame, after every <c>Update</c>, so the
    /// zeroed timer is observed by the next frame's idle decision exactly as
    /// before.</para>
    ///
    /// <para>The two native conditions the retired postfix checked at WRITE time
    /// — an empty <c>waypoints</c> list and the full <c>TravelActive()</c>,
    /// which also reports true for an in-flight jump-gate hop — are re-read here
    /// rather than trusted from the fact. The API validates them when it emits,
    /// but its hub dispatches subscribers synchronously, so a subscriber ahead
    /// of Echo can start a new route inside the same callback chain.
    /// <see cref="Travel.ArrivalSnapApplyGuard"/> owns that decision so both
    /// gates are unit-tested; everything unreadable fails closed.</para>
    ///
    /// <para><see cref="Singleton{T}.Current"/>, not <c>Instance</c>: the latter
    /// runs <c>FindAnyObjectByType</c> and caches the result into the shared
    /// static when the field is empty. A pure read of the managers the game
    /// already registered is enough here, and it cannot seed that cache with an
    /// object found during a scene transition. A missing or destroyed manager
    /// (Unity fake-null) simply leaves the vanilla cycle running.</para>
    /// </summary>
    internal static void ApplyArrivalSnap()
    {
        var idle = Singleton<IdleManager>.Current;
        var player = GamePlayer.current;
        var travel = Singleton<TravelManager>.Current;
        var waypoints = player != null ? player.waypoints : null;

        var decision = Travel.ArrivalSnapApplyGuard.Evaluate(new Travel.NativeTravelState(
            idleManagerLive: idle != null,
            playerLive: player != null,
            travelManagerLive: travel != null,
            remainingWaypoints: waypoints != null ? waypoints.Count : -1,
            travelActive: travel != null && travel.TravelActive()));

        if (decision != Travel.ArrivalSnapApply.WriteTimer)
        {
            Plugin.Log.LogDebug("[autopilot-timing] arrival-snap skipped at write time: " + decision);
            return;
        }

        Plugin.Log.LogDebug("[autopilot-timing] arrival-snap: zeroing updateTimer");
        // WriteTimer is only returned for IdleManagerLive, which is this very
        // reference having passed Unity's null operator above.
        UpdateTimerRef(idle!) = 0f;
    }

    /// <summary>
    /// Postfix on <see cref="IdleManager.Update"/>. Runs every frame. When the
    /// player is on autopilot and <see cref="TravelManager.isWarping"/> is
    /// true, overwrite <c>updateTimer</c> and <c>updateTimerBase</c> so the
    /// autopilot side-tab's green fill circle tracks live <b>distance-based
    /// progress</b> along the trip — not seconds remaining.
    ///
    /// Why distance instead of ETA: the ship accelerates for ~10 s, cruises,
    /// then decelerates. A naive <c>eta = remainingDistance / travelSpeed</c>
    /// blows up on the first tick of warp (<c>travelSpeed ≈ 0</c>), producing
    /// a huge seed that then collapses the moment the ship gets any real
    /// speed — the circle rockets to ~99 % in two frames and crawls the rest.
    /// Distance-based progress is speed-independent and monotonic: the circle
    /// fills at the ship's actual spatial progress (slow during accel/decel,
    /// fast during cruise), which matches the player's intuition.
    ///
    /// We seed <c>updateTimerBase = totalDistance</c> once per warp leg and
    /// grow it if the trip lengthens (waypoint added mid-flight). Each tick
    /// we set <c>updateTimer = remainingDistance</c>. <c>SideTabAutopilot</c>'s
    /// <c>fillAmount = 1 - updateTimer/base</c> then renders as the fraction
    /// of the trip covered.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(IdleManager), "Update")]
    private static void IdleManager_Update_Postfix(IdleManager __instance)
    {
        if (!Plugin.Instance.CfgAutopilotTiming.Value) return;
        if (!Plugin.Instance.CfgAutopilotEtaSync.Value)
        {
            _wasSyncing = false;
            return;
        }

        var player = GamePlayer.current;
        if (player == null || !player.autoPlay)
        {
            _wasSyncing = false;
            return;
        }

        var travel = Singleton<TravelManager>.Instance;
        if (travel == null || !travel.isWarping)
        {
            _wasSyncing = false;
            return;
        }

        float totalDistance = travel.totalDistance;
        float remainingDistance = Mathf.Max(0f, travel.remainingDistance);

        // Guard: TravelManager clears totalDistance to 0 on arrival, and there
        // is a brief window at warp start where isWarping has flipped true
        // but totalDistance hasn't been set yet. In both cases, skip.
        if (totalDistance <= 0f) return;

        if (!_wasSyncing)
        {
            UpdateTimerBaseRef(__instance) = totalDistance;
            Plugin.Log.LogInfo(
                $"[autopilot-timing] eta-sync begin: distance={totalDistance:F1}u, " +
                $"est-eta={EstimateTripSeconds(totalDistance):F1}s");
            _wasSyncing = true;
        }

        // Grow-only base: if the trip lengthens (e.g. waypoint added mid-flight),
        // extend the base so fillAmount never jumps backward.
        float currentBase = UpdateTimerBaseRef(__instance);
        if (totalDistance > currentBase)
        {
            UpdateTimerBaseRef(__instance) = totalDistance;
        }

        UpdateTimerRef(__instance) = remainingDistance;
    }

    /// <summary>
    /// Rough best-case trip duration for logging purposes only. Uses the
    /// ship's configured <c>baseMaxWarpSpeed</c> (no fuel/bonus multipliers,
    /// no accel/decel overhead), so the real trip will always take longer.
    /// Returns 0 if the ship or its configured max speed is unavailable — the
    /// log line just prints "est-eta=0.0s" in that case and the circle itself
    /// is unaffected because it uses distance, not this estimate.
    /// </summary>
    private static float EstimateTripSeconds(float distance)
    {
        var gm = GameplayManager.Instance;
        if (gm == null) return 0f;
        var ship = gm.spaceShip;
        if (ship == null) return 0f;
        float maxSpeed = ship.baseMaxWarpSpeed;
        if (maxSpeed <= 0.1f) return 0f;
        return distance / maxSpeed;
    }
}

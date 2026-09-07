using VGEcho.Travel;
using Xunit;

namespace VGEcho.Tests;

/// <summary>The write-time half of arrival-snap: the two native conditions the
/// retired <c>TravelToNextWaypoint</c> postfix re-read immediately before
/// zeroing the timer, plus the liveness reads around them.</summary>
public sealed class ArrivalSnapApplyGuardTests
{
    private static NativeTravelState Quiet(bool idle = true, bool player = true, bool travel = true,
        int waypoints = 0, bool active = false)
        => new NativeTravelState(idle, player, travel, waypoints, active);

    [Fact]
    public void AQuietWorldWithNoWaypointsAndNoActiveTravelWritesTheTimer()
        => Assert.Equal(ArrivalSnapApply.WriteTimer, ArrivalSnapApplyGuard.Evaluate(Quiet()));

    /// <summary>The reentrancy hazard: a subscriber ahead of Echo queued another
    /// route from inside the same synchronous dispatch, so the fact is already
    /// stale by the time Echo would write.</summary>
    [Fact]
    public void ARouteQueuedAgainBeforeTheWriteRefusesTheSnap()
        => Assert.Equal(ArrivalSnapApply.RouteStillHasWaypoints,
            ArrivalSnapApplyGuard.Evaluate(Quiet(waypoints: 1)));

    /// <summary>The full native <c>TravelActive()</c> also reports an in-flight
    /// jump-gate hop, so an empty waypoint list alone is not enough.</summary>
    [Fact]
    public void TravelActiveAgainWithAnEmptyWaypointListStillRefusesTheSnap()
        => Assert.Equal(ArrivalSnapApply.TravelStillActive,
            ArrivalSnapApplyGuard.Evaluate(Quiet(active: true)));

    [Fact]
    public void AnUnreadableWaypointListIsNeverTreatedAsEmpty()
        => Assert.Equal(ArrivalSnapApply.WaypointsUnavailable,
            ArrivalSnapApplyGuard.Evaluate(Quiet(waypoints: -1)));

    [Fact]
    public void ADeadIdleManagerRefusesTheSnap()
        => Assert.Equal(ArrivalSnapApply.IdleManagerUnavailable,
            ArrivalSnapApplyGuard.Evaluate(Quiet(idle: false)));

    [Fact]
    public void ADeadPlayerRefusesTheSnap()
        => Assert.Equal(ArrivalSnapApply.PlayerUnavailable,
            ArrivalSnapApplyGuard.Evaluate(Quiet(player: false)));

    [Fact]
    public void ADeadTravelManagerRefusesTheSnapInsteadOfAssumingIdleTravel()
        => Assert.Equal(ArrivalSnapApply.TravelManagerUnavailable,
            ArrivalSnapApplyGuard.Evaluate(Quiet(travel: false)));

    /// <summary>Liveness is checked before the state reads, so a torn-down world
    /// never reports a travel-state reason it could not have observed.</summary>
    [Fact]
    public void LivenessIsReportedBeforeTravelState()
        => Assert.Equal(ArrivalSnapApply.IdleManagerUnavailable,
            ArrivalSnapApplyGuard.Evaluate(Quiet(idle: false, waypoints: 3, active: true)));

    /// <summary>Positive control for the model used by the reentrancy tests:
    /// with a quiet world the write happens and the next idle tick decides.</summary>
    [Fact]
    public void TheModelledCycleFiresOnTheNextTickOnlyAfterAWrite()
    {
        var native = new SimulatedNativeTravel();
        native.ApplyArrivalSnap();
        Assert.Equal(1, native.TimerWrites);
        native.IdleTick(0.02f);
        Assert.Equal(1, native.FindActivityCalls);

        var untouched = new SimulatedNativeTravel();
        untouched.IdleTick(0.02f);
        Assert.Equal(0, untouched.FindActivityCalls);
    }
}

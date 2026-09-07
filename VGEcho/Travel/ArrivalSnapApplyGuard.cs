namespace VGEcho.Travel;

/// <summary>Live native travel state, snapshotted by the caller immediately
/// before the timer write. Plugin-free on purpose: the reads are Unity/vanilla,
/// the decision made from them is not.</summary>
internal readonly struct NativeTravelState
{
    /// <summary>A live <c>IdleManager</c> (Unity fake-null already resolved).</summary>
    internal bool IdleManagerLive { get; }

    /// <summary>A live <c>GamePlayer.current</c>.</summary>
    internal bool PlayerLive { get; }

    /// <summary>A live <c>TravelManager</c> (Unity fake-null already resolved).</summary>
    internal bool TravelManagerLive { get; }

    /// <summary>Entries left in <c>GamePlayer.current.waypoints</c>. Negative
    /// means the list could not be read at all, which is never treated as
    /// "no waypoints left".</summary>
    internal int RemainingWaypoints { get; }

    /// <summary>The FULL native <c>TravelManager.TravelActive()</c>, which also
    /// reports true while <c>usingJumpgate</c> is set.</summary>
    internal bool TravelActive { get; }

    internal NativeTravelState(bool idleManagerLive, bool playerLive, bool travelManagerLive,
        int remainingWaypoints, bool travelActive)
    {
        IdleManagerLive = idleManagerLive;
        PlayerLive = playerLive;
        TravelManagerLive = travelManagerLive;
        RemainingWaypoints = remainingWaypoints;
        TravelActive = travelActive;
    }
}

/// <summary>Why the timer write was or was not performed.</summary>
internal enum ArrivalSnapApply
{
    /// <summary>Every apply-time condition holds; zero the cycle timer.</summary>
    WriteTimer,

    IdleManagerUnavailable,
    PlayerUnavailable,
    TravelManagerUnavailable,

    /// <summary>The waypoint list could not be read; refuse rather than assume empty.</summary>
    WaypointsUnavailable,

    /// <summary>A route is queued again: something started travelling between the
    /// observed completion and this write.</summary>
    RouteStillHasWaypoints,

    /// <summary>Native travel (including an in-flight jump-gate hop) is active again.</summary>
    TravelStillActive,
}

/// <summary>
/// The apply-time half of arrival-snap, kept pure so both gates are unit-testable.
///
/// <para>VGModAPI validates the empty-waypoint list and <c>TravelActive()</c>
/// when it EMITS <c>RouteCompleted</c>, but its hub dispatches subscribers
/// synchronously in subscription order. A subscriber registered ahead of Echo
/// can start a new route from inside that same callback chain, so by the time
/// Echo's callback runs the fact can already be stale. Zeroing the cycle timer
/// then would make the next idle tick fire a decision mid-route.</para>
///
/// <para>So the timer write re-reads both native conditions itself, exactly as
/// the retired <c>TravelManager.TravelToNextWaypoint</c> postfix did. These are
/// two cheap current reads, not a substitute for the API fact: the fact still
/// decides WHETHER a genuine owned final route completed
/// (<see cref="ArrivalSnapReducer"/>); this only re-confirms the world has not
/// moved on before the write lands. Anything unreadable fails closed.</para>
/// </summary>
internal static class ArrivalSnapApplyGuard
{
    internal static ArrivalSnapApply Evaluate(in NativeTravelState state)
    {
        if (!state.IdleManagerLive) return ArrivalSnapApply.IdleManagerUnavailable;
        if (!state.PlayerLive) return ArrivalSnapApply.PlayerUnavailable;
        if (!state.TravelManagerLive) return ArrivalSnapApply.TravelManagerUnavailable;
        if (state.RemainingWaypoints < 0) return ArrivalSnapApply.WaypointsUnavailable;
        if (state.RemainingWaypoints != 0) return ArrivalSnapApply.RouteStillHasWaypoints;
        if (state.TravelActive) return ArrivalSnapApply.TravelStillActive;
        return ArrivalSnapApply.WriteTimer;
    }
}

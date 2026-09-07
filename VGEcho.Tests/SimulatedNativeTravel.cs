using VGEcho.Travel;

namespace VGEcho.Tests;

/// <summary>
/// A host MODEL of the two native facts arrival-snap interacts with: the travel
/// state the write-time guard reads, and the autopilot cycle timer it writes.
///
/// <para>This is deliberately NOT a claim of native coverage. It exercises the
/// real production decision (<see cref="ArrivalSnapApplyGuard.Evaluate"/>, the
/// same method <c>AutopilotTimingPatches.ApplyArrivalSnap</c> calls \u2014 pinned by
/// <c>PluginAssemblyShapeTests</c>) against a scripted state, and models the
/// vanilla <c>IdleManager.Update</c> cycle whose actual shape
/// (<c>updateTimer -= deltaTime; if (updateTimer &lt; 0f) FindActivity();</c>) is
/// pinned against the shipped assembly by <c>InstalledGameMetadataTests</c>.
/// Protected in-game qualification of the real reentrant supersession remains
/// separate and is not substituted for by this model.</para>
/// </summary>
internal sealed class SimulatedNativeTravel
{
    /// <summary>Vanilla <c>IdleManager.ActivityDelay</c>.</summary>
    internal const float ActivityDelay = 12f;

    internal bool IdleManagerLive = true;
    internal bool PlayerLive = true;
    internal bool TravelManagerLive = true;
    internal int RemainingWaypoints;
    internal bool TravelActive;

    internal float UpdateTimer = ActivityDelay;
    internal int FindActivityCalls;
    internal int TimerWrites;
    internal ArrivalSnapApply LastDecision = ArrivalSnapApply.WriteTimer;

    internal NativeTravelState Snapshot() => new NativeTravelState(
        IdleManagerLive, PlayerLive, TravelManagerLive, RemainingWaypoints, TravelActive);

    /// <summary>Mirrors <c>ApplyArrivalSnap</c>: snapshot, decide, write only on
    /// <see cref="ArrivalSnapApply.WriteTimer"/>.</summary>
    internal void ApplyArrivalSnap()
    {
        LastDecision = ArrivalSnapApplyGuard.Evaluate(Snapshot());
        if (LastDecision != ArrivalSnapApply.WriteTimer) return;
        UpdateTimer = 0f;
        TimerWrites++;
    }

    /// <summary>One vanilla idle tick.</summary>
    internal void IdleTick(float deltaSeconds)
    {
        UpdateTimer -= deltaSeconds;
        if (UpdateTimer < 0f)
        {
            FindActivityCalls++;
            UpdateTimer = ActivityDelay;
        }
    }

    /// <summary>What a subscriber ahead of Echo does when it reacts to the same
    /// completion by starting another route: the waypoint list refills and
    /// native travel becomes active again.</summary>
    internal void StartNewRoute()
    {
        RemainingWaypoints = 1;
        TravelActive = true;
    }

    /// <summary>A gate hop re-engaged after the waypoint was already consumed:
    /// the list is empty but <c>TravelActive()</c> still reports true through
    /// <c>usingJumpgate</c>.</summary>
    internal void EngageJumpGate()
    {
        RemainingWaypoints = 0;
        TravelActive = true;
    }
}

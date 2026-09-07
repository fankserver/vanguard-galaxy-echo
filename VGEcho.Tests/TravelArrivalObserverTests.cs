using System;
using System.Collections.Generic;
using VGEcho.Travel;
using VGModAPI;
using Xunit;

namespace VGEcho.Tests;

/// <summary>The adapter between the API's public fact stream and the pure
/// reducer: projection of every transition kind, subscription lifetime, and the
/// single fault latch that turns arrival-snap off without touching anything
/// else Echo does.</summary>
public sealed class TravelArrivalObserverTests
{
    private static readonly Guid Session = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private sealed class Harness
    {
        internal readonly FakeTravelEvents Events = new FakeTravelEvents { SessionId = Session };
        internal readonly List<string> Traces = new List<string>();
        internal readonly List<string> Failures = new List<string>();
        internal ArrivalSnapGates Gates = TravelFacts.AllOpen;
        internal int Snaps;
        internal Action? OnSnap;

        internal TravelArrivalObserver Build() => new TravelArrivalObserver(Events,
            () => Gates,
            () => { Snaps++; OnSnap?.Invoke(); },
            Traces.Add,
            Failures.Add);
    }

    [Fact]
    public void SubscriptionUsesEchosOwnOwnerIdentity()
    {
        var harness = new Harness();
        using var observer = harness.Build();
        Assert.Equal(new[] { ArrivalSnapBinding.SubscriptionOwner }, harness.Events.Owners);
        Assert.True(observer.IsListening);
    }

    [Fact]
    public void ARefusedSubscriptionThrowsOutOfTheConstructorForTheCallerToDegrade()
    {
        var harness = new Harness { Events = { RefuseSubscription = new ObjectDisposedException("hub") } };
        Assert.Throws<ObjectDisposedException>(() => harness.Build());
    }

    /// <summary>Every kind the API publishes today has its own local counterpart,
    /// so nothing collapses into the catch-all that would hide a future addition.</summary>
    [Fact]
    public void EveryPublishedKindProjectsOntoItsOwnLocalKind()
    {
        var expected = new Dictionary<TravelTransitionKind, TravelFactKind>
        {
            [TravelTransitionKind.InitialPlacement] = TravelFactKind.InitialPlacement,
            [TravelTransitionKind.Requested] = TravelFactKind.Requested,
            [TravelTransitionKind.Departed] = TravelFactKind.Departed,
            [TravelTransitionKind.Arrived] = TravelFactKind.Arrived,
            [TravelTransitionKind.Cancelled] = TravelFactKind.Cancelled,
            [TravelTransitionKind.RecoveredPlacement] = TravelFactKind.RecoveredPlacement,
            [TravelTransitionKind.RouteCompleted] = TravelFactKind.RouteCompleted,
        };
        Assert.Equal(Enum.GetValues(typeof(TravelTransitionKind)).Length, expected.Count);
        foreach (var pair in expected)
            Assert.Equal(pair.Value, TravelArrivalObserver.Project(TravelFacts.Transition(Session, pair.Key, 1)).Kind);

        // The only local kind without an API counterpart is the catch-all a
        // future API addition would land on; the reducer refuses that one.
        Assert.Equal(expected.Count + 1, Enum.GetValues(typeof(TravelFactKind)).Length);
    }

    /// <summary>Identity, ordering and leg attribution survive the projection —
    /// they are exactly what the reducer's ownership rules run on.</summary>
    [Fact]
    public void ProjectionPreservesSessionSequenceAndLegIdentity()
    {
        var leg = Guid.NewGuid();
        var fact = TravelArrivalObserver.Project(
            TravelFacts.Transition(Session, TravelTransitionKind.RouteCompleted, 42, leg));
        Assert.Equal(Session, fact.SessionId);
        Assert.Equal(42, fact.Sequence);
        Assert.Equal(leg, fact.OperationId);
    }

    [Fact]
    public void AFinalRouteCompletionSnapsOnceAndIsTraced()
    {
        var harness = new Harness();
        using var observer = harness.Build();
        harness.Events.Emit(TravelFacts.Transition(Session, TravelTransitionKind.RouteCompleted, 1));
        Assert.Equal(1, harness.Snaps);
        Assert.Contains(harness.Traces, trace => trace.Contains("final route"));
        Assert.Empty(harness.Failures);
    }

    [Fact]
    public void IntermediateArrivalsAndPlacementsAreSilentAndNeverSnap()
    {
        var harness = new Harness();
        using var observer = harness.Build();
        harness.Events.Emit(TravelFacts.Transition(Session, TravelTransitionKind.InitialPlacement, 1));
        harness.Events.Emit(TravelFacts.Transition(Session, TravelTransitionKind.Arrived, 2));
        harness.Events.Emit(TravelFacts.Transition(Session, TravelTransitionKind.Cancelled, 3));
        Assert.Equal(0, harness.Snaps);
        Assert.Empty(harness.Traces);
    }

    [Fact]
    public void ARefusedCompletionIsTracedWithItsReason()
    {
        var harness = new Harness { Gates = new ArrivalSnapGates(true, true, false) };
        using var observer = harness.Build();
        harness.Events.Emit(TravelFacts.Transition(Session, TravelTransitionKind.RouteCompleted, 1));
        Assert.Equal(0, harness.Snaps);
        Assert.Contains(harness.Traces, trace => trace.Contains(nameof(ArrivalSnapDecision.AutopilotDisengaged)));
    }

    /// <summary>A fact emitted while the snap callback is still running is queued
    /// by the hub and delivered afterwards. The already-consumed leg must not
    /// snap twice, and a genuinely new route still can.</summary>
    [Fact]
    public void AFactEmittedFromInsideTheSnapCallbackIsHandledWithoutDoubleSnapping()
    {
        var harness = new Harness();
        var leg = Guid.NewGuid();
        using var observer = harness.Build();
        var reentered = false;
        harness.OnSnap = () =>
        {
            if (reentered) return;
            reentered = true;
            harness.Events.Emit(TravelFacts.Transition(Session, TravelTransitionKind.RouteCompleted, 2, leg));
        };
        harness.Events.Emit(TravelFacts.Transition(Session, TravelTransitionKind.RouteCompleted, 1, leg));
        Assert.Equal(1, harness.Snaps);
        Assert.Contains(harness.Traces, trace => trace.Contains(nameof(ArrivalSnapDecision.DuplicateRouteCompletion)));
    }

    /// <summary>The reentrancy hazard the write-time guard exists for. The hub
    /// dispatches subscribers synchronously in subscription order, so an earlier
    /// subscriber can start a new route from inside the very callback chain that
    /// is delivering the completion. The reducer still legitimately approves the
    /// fact — it WAS a genuine owned completion — but the write-time guard sees
    /// the refilled waypoint list and refuses, so the modelled next idle tick
    /// makes no decision mid-route.</summary>
    [Fact]
    public void ASubscriberAheadOfEchoThatStartsANewRouteStopsTheWriteAndTheNextIdleTick()
    {
        var harness = new Harness();
        var native = new SimulatedNativeTravel();
        harness.Events.Subscribe("ahead-of-echo", transition =>
        {
            if (transition.Kind == TravelTransitionKind.RouteCompleted) native.StartNewRoute();
        });
        using var observer = harness.Build();
        harness.OnSnap = native.ApplyArrivalSnap;

        harness.Events.Emit(TravelFacts.Transition(Session, TravelTransitionKind.RouteCompleted, 1));

        Assert.Equal(1, harness.Snaps);
        Assert.Equal(ArrivalSnapApply.RouteStillHasWaypoints, native.LastDecision);
        Assert.Equal(0, native.TimerWrites);
        Assert.Equal(SimulatedNativeTravel.ActivityDelay, native.UpdateTimer);

        native.IdleTick(0.02f);
        Assert.Equal(0, native.FindActivityCalls);
    }

    /// <summary>Same hazard through the other native condition: the waypoint was
    /// already consumed but a gate hop is engaged again, which only the full
    /// <c>TravelActive()</c> reports.</summary>
    [Fact]
    public void ASubscriberAheadOfEchoThatEngagesAJumpGateStopsTheWriteAndTheNextIdleTick()
    {
        var harness = new Harness();
        var native = new SimulatedNativeTravel();
        harness.Events.Subscribe("ahead-of-echo", transition =>
        {
            if (transition.Kind == TravelTransitionKind.RouteCompleted) native.EngageJumpGate();
        });
        using var observer = harness.Build();
        harness.OnSnap = native.ApplyArrivalSnap;

        harness.Events.Emit(TravelFacts.Transition(Session, TravelTransitionKind.RouteCompleted, 1));

        Assert.Equal(ArrivalSnapApply.TravelStillActive, native.LastDecision);
        Assert.Equal(0, native.TimerWrites);
        native.IdleTick(0.02f);
        Assert.Equal(0, native.FindActivityCalls);
    }

    /// <summary>Control for both refusals above: with nothing ahead of Echo the
    /// same fact does write, and the next tick decides.</summary>
    [Fact]
    public void WithNothingStartingANewRouteTheSameFactWritesAndTheNextIdleTickDecides()
    {
        var harness = new Harness();
        var native = new SimulatedNativeTravel();
        using var observer = harness.Build();
        harness.OnSnap = native.ApplyArrivalSnap;

        harness.Events.Emit(TravelFacts.Transition(Session, TravelTransitionKind.RouteCompleted, 1));

        Assert.Equal(ArrivalSnapApply.WriteTimer, native.LastDecision);
        Assert.Equal(1, native.TimerWrites);
        native.IdleTick(0.02f);
        Assert.Equal(1, native.FindActivityCalls);
    }

    [Fact]
    public void AFactAttributedToAReplacedSessionIsRejected()
    {
        var harness = new Harness();
        using var observer = harness.Build();
        harness.Events.SessionId = Guid.NewGuid();
        harness.Events.Emit(TravelFacts.Transition(Session, TravelTransitionKind.RouteCompleted, 1));
        Assert.Equal(0, harness.Snaps);
    }

    [Fact]
    public void OneCallbackFailureLatchesTheObserverOffWithoutEscapingIntoTheProvider()
    {
        var harness = new Harness();
        using var observer = harness.Build();
        harness.OnSnap = () => throw new InvalidOperationException("field ref exploded");
        harness.Events.Emit(TravelFacts.Transition(Session, TravelTransitionKind.RouteCompleted, 1));

        Assert.Single(harness.Failures);
        Assert.Contains("InvalidOperationException", harness.Failures[0]);
        Assert.False(observer.IsListening);
        Assert.True(observer.Reducer.Faulted);

        harness.OnSnap = null;
        harness.Events.Emit(TravelFacts.Transition(Session, TravelTransitionKind.RouteCompleted, 2));
        Assert.Equal(1, harness.Snaps);
        Assert.Single(harness.Failures);
    }

    [Fact]
    public void DisposingReleasesTheSubscriptionAndStopsDeciding()
    {
        var harness = new Harness();
        var observer = harness.Build();
        observer.Dispose();

        Assert.Equal(0, harness.Events.LiveSubscriptions);
        Assert.False(observer.IsListening);
        Assert.True(observer.Reducer.Stopped);

        observer.Dispose();
        harness.Events.Emit(TravelFacts.Transition(Session, TravelTransitionKind.RouteCompleted, 1));
        Assert.Equal(0, harness.Snaps);
    }
}

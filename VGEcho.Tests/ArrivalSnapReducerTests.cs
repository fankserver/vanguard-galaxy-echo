using System;
using VGEcho.Travel;
using Xunit;

namespace VGEcho.Tests;

/// <summary>Every branch of the arrival-snap decision, with no game process.
/// The reducer is the whole behavioural contract of the feature: what may snap
/// the autopilot idle timer, and what may never.</summary>
public sealed class ArrivalSnapReducerTests
{
    private static readonly Guid Session = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ArrivalSnapDecision Decide(ArrivalSnapReducer reducer, TravelFact fact, ArrivalSnapGates? gates = null)
        => reducer.Decide(fact, Session, gates ?? TravelFacts.AllOpen);

    [Fact]
    public void AnOwnedFinalRouteCompletionSnaps()
    {
        var reducer = new ArrivalSnapReducer();
        Assert.Equal(ArrivalSnapDecision.Snap,
            Decide(reducer, TravelFacts.Fact(Session, TravelFactKind.RouteCompleted, 1)));
    }

    /// <summary>Initial and recovered placement (load, first verified position),
    /// requests, departures, cancellations, intermediate arrivals and any kind a
    /// future API adds: none of them is a completed route.</summary>
    [Fact]
    public void NoOtherKindEverSnaps()
    {
        foreach (TravelFactKind kind in Enum.GetValues(typeof(TravelFactKind)))
        {
            if (kind == TravelFactKind.RouteCompleted) continue;
            var reducer = new ArrivalSnapReducer();
            Assert.Equal(ArrivalSnapDecision.NotFinalRoute, Decide(reducer, TravelFacts.Fact(Session, kind, 1)));
        }
    }

    /// <summary>The load / first-placement / cancel / intermediate-hop sequence a
    /// jump-gate route actually produces: only the terminal completion snaps.</summary>
    [Fact]
    public void AWholeRouteSnapsExactlyOnceAtItsCompletion()
    {
        var reducer = new ArrivalSnapReducer();
        var leg = Guid.NewGuid();
        var decisions = new[]
        {
            Decide(reducer, TravelFacts.Fact(Session, TravelFactKind.InitialPlacement, 1)),
            Decide(reducer, TravelFacts.Fact(Session, TravelFactKind.Requested, 2, leg)),
            Decide(reducer, TravelFacts.Fact(Session, TravelFactKind.Departed, 3, leg)),
            Decide(reducer, TravelFacts.Fact(Session, TravelFactKind.Arrived, 4, leg)),
            Decide(reducer, TravelFacts.Fact(Session, TravelFactKind.RouteCompleted, 5, leg)),
        };
        Assert.Equal(new[]
        {
            ArrivalSnapDecision.NotFinalRoute, ArrivalSnapDecision.NotFinalRoute,
            ArrivalSnapDecision.NotFinalRoute, ArrivalSnapDecision.NotFinalRoute,
            ArrivalSnapDecision.Snap,
        }, decisions);
    }

    [Fact]
    public void ARepeatedCompletionOfTheSameLegIsDropped()
    {
        var reducer = new ArrivalSnapReducer();
        var leg = Guid.NewGuid();
        Assert.Equal(ArrivalSnapDecision.Snap, Decide(reducer, TravelFacts.Fact(Session, TravelFactKind.RouteCompleted, 1, leg)));
        Assert.Equal(ArrivalSnapDecision.DuplicateRouteCompletion,
            Decide(reducer, TravelFacts.Fact(Session, TravelFactKind.RouteCompleted, 2, leg)));
    }

    [Fact]
    public void ASecondRouteInTheSameSessionSnapsAgain()
    {
        var reducer = new ArrivalSnapReducer();
        Assert.Equal(ArrivalSnapDecision.Snap, Decide(reducer, TravelFacts.Fact(Session, TravelFactKind.RouteCompleted, 1)));
        Assert.Equal(ArrivalSnapDecision.Snap, Decide(reducer, TravelFacts.Fact(Session, TravelFactKind.RouteCompleted, 2)));
    }

    [Fact]
    public void ACompletionWithoutALegIdentityIsUnattributableAndDoesNotSnap()
    {
        var reducer = new ArrivalSnapReducer();
        Assert.Equal(ArrivalSnapDecision.MissingOperation,
            reducer.Decide(new TravelFact(Session, null, 1, TravelFactKind.RouteCompleted), Session, TravelFacts.AllOpen));
    }

    [Fact]
    public void ReplayedAndOutOfOrderSequencesAreDropped()
    {
        var reducer = new ArrivalSnapReducer();
        Assert.Equal(ArrivalSnapDecision.NotFinalRoute, Decide(reducer, TravelFacts.Fact(Session, TravelFactKind.Arrived, 7)));
        Assert.Equal(ArrivalSnapDecision.StaleSequence, Decide(reducer, TravelFacts.Fact(Session, TravelFactKind.RouteCompleted, 7)));
        Assert.Equal(ArrivalSnapDecision.StaleSequence, Decide(reducer, TravelFacts.Fact(Session, TravelFactKind.RouteCompleted, 3)));
        Assert.Equal(ArrivalSnapDecision.Snap, Decide(reducer, TravelFacts.Fact(Session, TravelFactKind.RouteCompleted, 8)));
    }

    [Fact]
    public void EvidenceForASessionTheServiceNoLongerOwnsIsRejected()
    {
        var reducer = new ArrivalSnapReducer();
        Assert.Equal(ArrivalSnapDecision.ForeignSession,
            reducer.Decide(TravelFacts.Fact(Other, TravelFactKind.RouteCompleted, 1), Session, TravelFacts.AllOpen));
        Assert.Equal(ArrivalSnapDecision.ForeignSession,
            reducer.Decide(TravelFacts.Fact(Session, TravelFactKind.RouteCompleted, 1), null, TravelFacts.AllOpen));
    }

    /// <summary>A load or new game replaces the session. The new session starts
    /// its own sequence numbering at 1, which must not look stale, and the old
    /// session's completed legs must not suppress a completion in the new one.</summary>
    [Fact]
    public void ASessionReplacementResetsOrderingAndRouteAccounting()
    {
        var reducer = new ArrivalSnapReducer();
        var leg = Guid.NewGuid();
        Assert.Equal(ArrivalSnapDecision.Snap,
            reducer.Decide(TravelFacts.Fact(Session, TravelFactKind.RouteCompleted, 9, leg), Session, TravelFacts.AllOpen));
        Assert.Equal(ArrivalSnapDecision.Snap,
            reducer.Decide(TravelFacts.Fact(Other, TravelFactKind.RouteCompleted, 1, leg), Other, TravelFacts.AllOpen));
        Assert.Equal(Other, reducer.Session);
    }

    [Fact]
    public void EvidenceQueuedForAReplacedSessionCannotActOnItsReplacement()
    {
        var reducer = new ArrivalSnapReducer();
        Assert.Equal(ArrivalSnapDecision.Snap,
            reducer.Decide(TravelFacts.Fact(Other, TravelFactKind.RouteCompleted, 1), Other, TravelFacts.AllOpen));
        Assert.Equal(ArrivalSnapDecision.ForeignSession,
            reducer.Decide(TravelFacts.Fact(Session, TravelFactKind.RouteCompleted, 2), Other, TravelFacts.AllOpen));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void EitherConfigSwitchSuppressesTheSnap(bool timing, bool arrivalSnap)
    {
        var reducer = new ArrivalSnapReducer();
        Assert.Equal(ArrivalSnapDecision.FeatureDisabled,
            Decide(reducer, TravelFacts.Fact(Session, TravelFactKind.RouteCompleted, 1),
                new ArrivalSnapGates(timing, arrivalSnap, true)));
    }

    [Fact]
    public void AManualPilotIsNeverSnapped()
    {
        var reducer = new ArrivalSnapReducer();
        Assert.Equal(ArrivalSnapDecision.AutopilotDisengaged,
            Decide(reducer, TravelFacts.Fact(Session, TravelFactKind.RouteCompleted, 1),
                new ArrivalSnapGates(true, true, false)));
    }

    /// <summary>Re-enabling the toggle after a route already completed must not
    /// let that same route snap late: the completion was consumed when observed.</summary>
    [Fact]
    public void ReEnablingTheToggleDoesNotResurrectAnAlreadyObservedCompletion()
    {
        var reducer = new ArrivalSnapReducer();
        var leg = Guid.NewGuid();
        Assert.Equal(ArrivalSnapDecision.FeatureDisabled,
            Decide(reducer, TravelFacts.Fact(Session, TravelFactKind.RouteCompleted, 1, leg),
                new ArrivalSnapGates(true, false, true)));
        Assert.Equal(ArrivalSnapDecision.DuplicateRouteCompletion,
            Decide(reducer, TravelFacts.Fact(Session, TravelFactKind.RouteCompleted, 2, leg)));
    }

    [Fact]
    public void AStoppedReducerDecidesNothingFurther()
    {
        var reducer = new ArrivalSnapReducer();
        reducer.Stop();
        Assert.False(reducer.IsListening);
        Assert.Equal(ArrivalSnapDecision.ServiceStopped, Decide(reducer, TravelFacts.Fact(Session, TravelFactKind.RouteCompleted, 1)));
    }

    [Fact]
    public void AFaultedReducerDecidesNothingFurther()
    {
        var reducer = new ArrivalSnapReducer();
        reducer.Fault();
        Assert.False(reducer.IsListening);
        Assert.Equal(ArrivalSnapDecision.ServiceStopped, Decide(reducer, TravelFacts.Fact(Session, TravelFactKind.RouteCompleted, 1)));
    }
}

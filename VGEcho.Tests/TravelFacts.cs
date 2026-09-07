using System;
using VGEcho.Travel;
using VGModAPI;

namespace VGEcho.Tests;

/// <summary>Builders for the two fact shapes the suite uses: the API's real
/// <see cref="TravelTransition"/> (validated by the API's own constructor) and
/// the plugin-free <see cref="TravelFact"/> the reducer consumes.</summary>
internal static class TravelFacts
{
    internal static readonly TravelLocation Somewhere = new TravelLocation("system-1", "poi-1", "Sol", "Earth Station");

    internal static TravelTransition Transition(Guid session, TravelTransitionKind kind, long sequence,
        Guid? operation = null, TravelMode mode = TravelMode.InSystem)
    {
        var placement = kind is TravelTransitionKind.InitialPlacement or TravelTransitionKind.RecoveredPlacement;
        var actual = placement || kind is TravelTransitionKind.Arrived or TravelTransitionKind.RouteCompleted ? Somewhere : null;
        var requested = kind == TravelTransitionKind.Requested ? Somewhere : null;
        return new TravelTransition(session, placement ? null : operation ?? Guid.NewGuid(), sequence, kind, mode,
            null, requested, actual, 1234.0, null);
    }

    internal static TravelFact Fact(Guid session, TravelFactKind kind, long sequence, Guid? operation = null) =>
        new TravelFact(session, kind is TravelFactKind.InitialPlacement or TravelFactKind.RecoveredPlacement
            ? null
            : operation ?? Guid.NewGuid(), sequence, kind);

    internal static ArrivalSnapGates AllOpen => new ArrivalSnapGates(true, true, true);
}

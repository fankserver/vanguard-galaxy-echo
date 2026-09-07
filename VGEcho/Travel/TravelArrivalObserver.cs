using System;
using VGModAPI;

namespace VGEcho.Travel;

/// <summary>
/// Subscribes to <see cref="ITravelEvents"/> and turns each witnessed fact into
/// an <see cref="ArrivalSnapReducer"/> decision. This type and
/// <see cref="TravelArrivalBridge"/> are the only two places in VGEcho that
/// name a VGModAPI type; neither is reachable from a plugin member's signature.
///
/// <para>It observes only. The snap action, the gate reads and all logging are
/// injected as delegates over plugin-free types, which keeps this file free of
/// Unity and BepInEx and lets the tests drive it against a fake service.</para>
/// </summary>
internal sealed class TravelArrivalObserver : IArrivalSnapObserver
{
    private readonly ITravelEvents _events;
    private readonly ArrivalSnapReducer _reducer = new ArrivalSnapReducer();
    private readonly Func<ArrivalSnapGates> _gates;
    private readonly Action _snap;
    private readonly Action<string> _trace;
    private readonly Action<string> _failed;
    private readonly IDisposable _subscription;
    private bool _disposed;

    /// <param name="events">The API's live travel service.</param>
    /// <param name="gates">Reads Echo's master/feature/autopilot gates at decision time.</param>
    /// <param name="snap">Applies the snap. Owns its own readiness checks.</param>
    /// <param name="trace">Debug-level decision log.</param>
    /// <param name="failed">Reports the single observer failure that latches the fault.</param>
    /// <exception cref="Exception">Whatever the provider throws when it refuses the
    /// subscription (disposed hub, off-main-thread install, rejected owner). The
    /// caller treats that as "arrival-snap unavailable", never as a startup failure.</exception>
    internal TravelArrivalObserver(ITravelEvents events, Func<ArrivalSnapGates> gates,
        Action snap, Action<string> trace, Action<string> failed)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _gates = gates ?? throw new ArgumentNullException(nameof(gates));
        _snap = snap ?? throw new ArgumentNullException(nameof(snap));
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _failed = failed ?? throw new ArgumentNullException(nameof(failed));
        _subscription = events.Subscribe(ArrivalSnapBinding.SubscriptionOwner, Receive);
    }

    public bool IsListening => !_disposed && _reducer.IsListening;

    /// <summary>Exposed for the tests that assert the fault/stop latches.</summary>
    internal ArrivalSnapReducer Reducer => _reducer;

    /// <summary>Projects an API fact onto the plugin-free shape the reducer consumes.
    /// An unrecognized future kind maps to <see cref="TravelFactKind.Unrecognized"/>
    /// rather than being guessed at.</summary>
    internal static TravelFact Project(TravelTransition transition) => new TravelFact(
        transition.SessionId, transition.OperationId, transition.Sequence, KindOf(transition.Kind));

    private static TravelFactKind KindOf(TravelTransitionKind kind) => kind switch
    {
        TravelTransitionKind.InitialPlacement => TravelFactKind.InitialPlacement,
        TravelTransitionKind.Requested => TravelFactKind.Requested,
        TravelTransitionKind.Departed => TravelFactKind.Departed,
        TravelTransitionKind.Arrived => TravelFactKind.Arrived,
        TravelTransitionKind.Cancelled => TravelFactKind.Cancelled,
        TravelTransitionKind.RecoveredPlacement => TravelFactKind.RecoveredPlacement,
        TravelTransitionKind.RouteCompleted => TravelFactKind.RouteCompleted,
        _ => TravelFactKind.Unrecognized,
    };

    private void Receive(TravelTransition transition)
    {
        if (!IsListening) return;
        try
        {
            var fact = Project(transition);
            var decision = _reducer.Decide(fact, _events.SessionId, _gates());
            if (decision == ArrivalSnapDecision.Snap)
            {
                _trace("[autopilot-timing] arrival-snap: final route " + fact.OperationId + " completed");
                _snap();
            }
            else if (fact.Kind == TravelFactKind.RouteCompleted)
            {
                _trace("[autopilot-timing] arrival-snap skipped: " + decision);
            }
        }
        catch (Exception error)
        {
            // One failure disables arrival-snap for the rest of the process
            // instead of throwing back into the API's dispatch loop on every
            // subsequent fact. Nothing else in Echo is affected.
            _reducer.Fault();
            try
            {
                _failed("[autopilot-timing] arrival-snap observer failed; the vanilla idle cycle runs unchanged "
                    + "until restart. Every other VGEcho feature is unaffected: "
                    + error.GetType().Name + ": " + error.Message);
            }
            catch
            {
                // A logger that throws must not escape into the API's dispatch loop.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reducer.Stop();
        _subscription.Dispose();
    }
}

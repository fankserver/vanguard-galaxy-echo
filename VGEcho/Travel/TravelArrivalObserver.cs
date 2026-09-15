using System;
using VGModAPI;

namespace VGEcho.Travel;

/// <summary>
/// Subscribes to <see cref="ITravelService"/>'s <c>Transitioned</c> and
/// <c>AvailabilityChanged</c> events and turns each witnessed fact into an
/// <see cref="ArrivalSnapReducer"/> decision. This type and
/// <see cref="TravelArrivalBridge"/> are the only two places in VGEcho that
/// name a VGModAPI type; neither is reachable from a plugin member's signature.
///
/// <para>It observes only. The snap action, the gate reads and all logging are
/// injected as delegates over plugin-free types, which keeps this file free of
/// Unity and BepInEx and lets the tests drive it against a fake service.</para>
///
/// <para>Subscription follows the API's current availability model: register
/// both handlers first, then read the current <c>Availability</c> — registration
/// does not replay. A non-terminal unavailability pauses deciding (it can
/// return); <c>ObserverFault</c> and <c>ApiStopped</c> are terminal and latch
/// this observer off for the rest of the process.</para>
/// </summary>
internal sealed class TravelArrivalObserver : IArrivalSnapObserver
{
    private readonly ITravelService _travel;
    private readonly ArrivalSnapReducer _reducer = new ArrivalSnapReducer();
    private readonly Func<ArrivalSnapGates> _gates;
    private readonly Action _snap;
    private readonly Action<string> _trace;
    private readonly Action<string> _failed;
    private bool _subscribed;
    private bool _disposed;
    private bool _lastReportedUnavailable;

    /// <param name="travel">The API's live travel service.</param>
    /// <param name="gates">Reads Echo's master/feature/autopilot gates at decision time.</param>
    /// <param name="snap">Applies the snap. Owns its own readiness checks.</param>
    /// <param name="trace">Debug-level decision and health log.</param>
    /// <param name="failed">Reports the single observer failure, or a terminal
    /// service fault, that latches the observer off.</param>
    /// <exception cref="Exception">Whatever the service throws when it refuses
    /// the registration (stopped hub, off-main-thread install). The caller
    /// treats that as "arrival-snap unavailable", never as a startup failure.</exception>
    internal TravelArrivalObserver(ITravelService travel, Func<ArrivalSnapGates> gates,
        Action snap, Action<string> trace, Action<string> failed)
    {
        _travel = travel ?? throw new ArgumentNullException(nameof(travel));
        _gates = gates ?? throw new ArgumentNullException(nameof(gates));
        _snap = snap ?? throw new ArgumentNullException(nameof(snap));
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _failed = failed ?? throw new ArgumentNullException(nameof(failed));
        try
        {
            // Marked before the first add: if any registration throws, Unhook
            // removes the handlers that did land, so a half-bound observer never
            // leaves a live AvailabilityChanged handler behind.
            _subscribed = true;
            _travel.AvailabilityChanged += OnAvailabilityChanged;
            _travel.Transitioned += OnTransitioned;
        }
        catch
        {
            Unhook();
            throw;
        }
        // Subscribe first, then read the current state: registration does not
        // replay, so the live availability has to be consumed here once.
        ReportAvailability(_travel.Availability);
    }

    public bool IsListening => !_disposed && _subscribed && _reducer.IsListening && _travel.Availability.IsAvailable;

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

    private void OnTransitioned(TravelTransition transition)
    {
        // A fact from a service that is currently unavailable decides nothing:
        // its facts are unreliable evidence until its health returns. The health
        // read itself is inside the try so a misbehaving getter latches the
        // observer off instead of escaping into the API's dispatch loop.
        try
        {
            if (!IsListening) return;
            var fact = Project(transition);
            var decision = _reducer.Decide(fact, _travel.SessionId, _gates());
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
            LatchOff("arrival-snap observer failed; the vanilla idle cycle runs unchanged "
                + "until restart. Every other VGEcho feature is unaffected: "
                + error.GetType().Name + ": " + error.Message);
        }
    }

    private void OnAvailabilityChanged(ServiceAvailability state)
    {
        if (_disposed) return;
        if (state.Reason is ServiceUnavailableReason.ObserverFault or ServiceUnavailableReason.ApiStopped)
        {
            // Terminal: the API will not deliver usable facts again this process
            // lifetime, and normal session replacement does not repair it.
            LatchOff("arrival-snap observer stopped: VGModAPI travel service reported the terminal "
                + state.Reason + " state; the vanilla idle cycle runs unchanged until restart. "
                + "Every other VGEcho feature is unaffected"
                + (string.IsNullOrEmpty(state.Detail) ? "." : ": " + state.Detail));
            return;
        }
        ReportAvailability(state);
    }

    private void ReportAvailability(ServiceAvailability state)
    {
        // The BindArrivalSnap success log already states the healthy case; only
        // a pause, or a return from one, is worth a line here.
        if (state.IsAvailable && !_lastReportedUnavailable) return;
        _lastReportedUnavailable = !state.IsAvailable;
        try
        {
            _trace(state.IsAvailable
                ? "[autopilot-timing] travel observation available again"
                : "[autopilot-timing] travel observation unavailable (" + state.Reason + ")");
        }
        catch
        {
            // A logger that throws must not escape into the API's dispatch loop.
        }
    }

    private void LatchOff(string message)
    {
        _reducer.Fault();
        Unhook();
        try
        {
            _failed("[autopilot-timing] " + message);
        }
        catch
        {
            // A logger that throws must not escape into the API's dispatch loop.
        }
    }

    private void Unhook()
    {
        if (!_subscribed) return;
        _subscribed = false;
        _travel.AvailabilityChanged -= OnAvailabilityChanged;
        _travel.Transitioned -= OnTransitioned;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reducer.Stop();
        Unhook();
    }
}

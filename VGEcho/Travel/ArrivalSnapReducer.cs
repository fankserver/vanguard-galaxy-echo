using System;
using System.Collections.Generic;

namespace VGEcho.Travel;

/// <summary>
/// The whole arrival-snap decision, as a pure state machine over observed
/// travel facts. It owns no Unity, BepInEx or VGModAPI type and performs no
/// I/O, so every branch below is unit-testable without a game process.
///
/// <para>It replaces the guard chain of the retired
/// <c>TravelManager.TravelToNextWaypoint</c> postfix. That postfix asked the
/// native manager "are the waypoints empty and is <c>TravelActive()</c> (which
/// includes <c>usingJumpgate</c>) false?". Those two reads are now the API's
/// own precondition for emitting <c>RouteCompleted</c>: VGModAPI's
/// <c>TravelNativeAdapter.CheckRouteBoundary</c> runs as a postfix on the very
/// same native method and emits only when
/// <c>WaypointCount(player) == 0 &amp;&amp; !TravelActive(travelManager)</c>,
/// additionally requiring a verified arrival to attribute the completion to.
/// So this reducer does not re-read native travel state; it decides ownership,
/// once-only delivery and Echo's own gates.</para>
///
/// <para>Only <see cref="TravelFactKind.RouteCompleted"/> can snap. Initial and
/// recovered placement (load / first verified position), requests,
/// cancellations and intermediate <see cref="TravelFactKind.Arrived"/> facts —
/// including the per-hop arrivals of a jump-gate or fast-lane chain — are
/// explicitly rejected.</para>
///
/// <para>There is no pending or deferred state by design. The API dispatches
/// synchronously from inside the native boundary, at the instant Echo's own
/// postfix used to run, so a snap either applies now or not at all; nothing can
/// be resurrected at a later idle tick.</para>
/// </summary>
internal sealed class ArrivalSnapReducer
{
    /// <summary>Route legs whose completion has already been accounted for in the
    /// current session. Cleared on session replacement and on <see cref="Stop"/>,
    /// so it is bounded by the completed routes of a single session.</summary>
    private readonly HashSet<Guid> _completedRoutes = new HashSet<Guid>();

    private Guid? _session;
    private long _lastSequence;

    /// <summary>Set once the observer is disposed. A stopped reducer decides nothing.</summary>
    internal bool Stopped { get; private set; }

    /// <summary>Latched by the observer when a callback threw. A faulted reducer
    /// stays silent for the rest of the process: arrival-snap is off, every other
    /// Echo feature keeps running.</summary>
    internal bool Faulted { get; private set; }

    internal bool IsListening => !Stopped && !Faulted;

    /// <summary>Session currently tracked, for diagnostics and tests.</summary>
    internal Guid? Session => _session;

    /// <param name="fact">The observed fact, already projected to plugin-free form.</param>
    /// <param name="serviceSession">The API service's own current session id. A
    /// fact whose session differs is queued/stale evidence and cannot decide anything.</param>
    /// <param name="gates">Echo's live master/feature/autopilot gates.</param>
    internal ArrivalSnapDecision Decide(in TravelFact fact, Guid? serviceSession, in ArrivalSnapGates gates)
    {
        if (!IsListening) return ArrivalSnapDecision.ServiceStopped;

        // Evidence that the provider itself no longer attributes to the live
        // session can never act on the live session's idle timer.
        if (serviceSession == null || serviceSession.Value != fact.SessionId)
            return ArrivalSnapDecision.ForeignSession;

        // A new session (load, new game, replacement) discards all prior ordering
        // and route accounting rather than carrying it across.
        if (_session != fact.SessionId)
        {
            _session = fact.SessionId;
            _lastSequence = 0;
            _completedRoutes.Clear();
        }

        if (fact.Sequence <= _lastSequence) return ArrivalSnapDecision.StaleSequence;
        _lastSequence = fact.Sequence;

        if (fact.Kind != TravelFactKind.RouteCompleted) return ArrivalSnapDecision.NotFinalRoute;
        if (fact.OperationId is not Guid route) return ArrivalSnapDecision.MissingOperation;

        // Once-only accounting is a property of the observed route, not of Echo's
        // configuration: consuming the leg here means flipping a config toggle
        // after a completion can never retroactively snap that same route.
        if (!_completedRoutes.Add(route)) return ArrivalSnapDecision.DuplicateRouteCompletion;

        if (!gates.TimingEnabled || !gates.ArrivalSnapEnabled) return ArrivalSnapDecision.FeatureDisabled;
        if (!gates.AutopilotEngaged) return ArrivalSnapDecision.AutopilotDisengaged;
        return ArrivalSnapDecision.Snap;
    }

    /// <summary>Latches the observer-failure state.</summary>
    internal void Fault() => Faulted = true;

    /// <summary>Permanently stops the reducer and drops all session state.</summary>
    internal void Stop()
    {
        Stopped = true;
        _session = null;
        _lastSequence = 0;
        _completedRoutes.Clear();
    }
}

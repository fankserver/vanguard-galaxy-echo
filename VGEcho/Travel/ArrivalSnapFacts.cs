using System;

namespace VGEcho.Travel;

/// <summary>
/// Plugin-free mirror of <c>VGModAPI.TravelTransitionKind</c>. The reducer is
/// deliberately typed against this local enum rather than the API's, so the
/// decision logic carries no reference to <c>VGModAPI.Abstractions</c> and can
/// be linked into a Unity-free, BepInEx-free test assembly.
///
/// <para><see cref="Unrecognized"/> is the landing spot for any kind a future
/// API adds: an unknown fact must never be mistaken for a completed route.</para>
/// </summary>
internal enum TravelFactKind
{
    Unrecognized,
    InitialPlacement,
    Requested,
    Departed,
    Arrived,
    Cancelled,
    RecoveredPlacement,
    RouteCompleted,
}

/// <summary>Immutable, plugin-free projection of one observed travel fact. Only
/// the fields the arrival-snap decision actually consumes are carried —
/// locations, modes, dwell and game time are irrelevant to zeroing a timer, so
/// they are not copied.</summary>
internal readonly struct TravelFact
{
    internal Guid SessionId { get; }

    /// <summary>The API's leg identity. Null for placements, which never snap.</summary>
    internal Guid? OperationId { get; }

    /// <summary>Session-scoped monotonic ordering supplied by the API hub.</summary>
    internal long Sequence { get; }

    internal TravelFactKind Kind { get; }

    internal TravelFact(Guid sessionId, Guid? operationId, long sequence, TravelFactKind kind)
    {
        SessionId = sessionId;
        OperationId = operationId;
        Sequence = sequence;
        Kind = kind;
    }
}

/// <summary>The live Echo-side gates read at decision time: the same master /
/// feature / autopilot conditions the retired <c>TravelToNextWaypoint</c>
/// postfix checked, snapshotted by the caller so the decision itself stays
/// pure.</summary>
internal readonly struct ArrivalSnapGates
{
    /// <summary><c>[Autopilot] TimingEnabled</c> — master switch shared with ETA-sync.</summary>
    internal bool TimingEnabled { get; }

    /// <summary><c>[Autopilot] ArrivalSnap</c>.</summary>
    internal bool ArrivalSnapEnabled { get; }

    /// <summary><c>GamePlayer.current.autoPlay</c> at the moment the fact arrived.</summary>
    internal bool AutopilotEngaged { get; }

    internal ArrivalSnapGates(bool timingEnabled, bool arrivalSnapEnabled, bool autopilotEngaged)
    {
        TimingEnabled = timingEnabled;
        ArrivalSnapEnabled = arrivalSnapEnabled;
        AutopilotEngaged = autopilotEngaged;
    }
}

/// <summary>Why the reducer did or did not ask for a snap. Every non-snap value
/// is a distinct, loggable reason — there is no catch-all "ignored".</summary>
internal enum ArrivalSnapDecision
{
    /// <summary>A genuine, owned, once-only final-route completion.</summary>
    Snap,

    /// <summary>The observer was disposed or a previous callback faulted it.</summary>
    ServiceStopped,

    /// <summary>The fact belongs to a session the API no longer considers current
    /// (queued evidence for a replaced session, or a stale/foreign emitter).</summary>
    ForeignSession,

    /// <summary>Replayed or out-of-order sequence within the current session.</summary>
    StaleSequence,

    /// <summary>Placement, request, departure, cancellation or an intermediate
    /// arrival — none of which is a final-route completion.</summary>
    NotFinalRoute,

    /// <summary>A route completion without a leg identity; unattributable, so not owned.</summary>
    MissingOperation,

    /// <summary>This leg's route completion was already accounted for.</summary>
    DuplicateRouteCompletion,

    /// <summary><c>TimingEnabled</c> or <c>ArrivalSnap</c> is off.</summary>
    FeatureDisabled,

    /// <summary>The player is not on autopilot, so there is no idle cycle to shorten.</summary>
    AutopilotDisengaged,
}

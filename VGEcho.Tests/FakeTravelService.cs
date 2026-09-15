using System;
using System.Collections.Generic;
using VGModAPI;

namespace VGEcho.Tests;

/// <summary>Stand-in for the API's live travel service, reproducing the
/// dispatch and health properties Echo's observer depends on: subscriber
/// callbacks run synchronously, a fact emitted from inside a callback is QUEUED
/// and delivered after the current one rather than nesting, and availability is
/// separately reported and can go terminal. Event registration may be refused,
/// as a stopped provider is entitled to do.</summary>
internal sealed class FakeTravelService : ITravelService
{
    private readonly List<Action<TravelTransition>> _subscribers = new List<Action<TravelTransition>>();
    private readonly List<Action<ServiceAvailability>> _healthHandlers = new List<Action<ServiceAvailability>>();
    private readonly Queue<TravelTransition> _queue = new Queue<TravelTransition>();
    private bool _dispatching;

    /// <summary>When set, adding a transition handler throws it, mimicking a
    /// provider that refuses the subscription.</summary>
    internal Exception? RefuseSubscription;

    /// <summary>When set, adding a health handler throws it instead, exercising
    /// the half-bound-construction cleanup path.</summary>
    internal Exception? RefuseHealthSubscription;

    internal int LiveSubscriptions => _subscribers.Count;
    internal int LiveHealthHandlers => _healthHandlers.Count;

    public Guid? SessionId { get; set; }
    public TravelLocation? CurrentLocation => null;
    public ServiceAvailability Availability { get; private set; } = ServiceAvailability.Available;

    public event Action<TravelTransition>? Transitioned
    {
        add
        {
            if (RefuseSubscription != null) throw RefuseSubscription;
            _subscribers.Add(value ?? throw new ArgumentNullException(nameof(value)));
        }
        remove { _subscribers.Remove(value ?? throw new ArgumentNullException(nameof(value))); }
    }

    public event Action<ServiceAvailability>? AvailabilityChanged
    {
        add
        {
            if (RefuseHealthSubscription != null) throw RefuseHealthSubscription;
            _healthHandlers.Add(value ?? throw new ArgumentNullException(nameof(value)));
        }
        remove { _healthHandlers.Remove(value ?? throw new ArgumentNullException(nameof(value))); }
    }

    /// <summary>Observation-only consumer: the fake never accepts routes.</summary>
    public TravelRouteResult RequestRoute(string poiId, float speedMultiplier = 1f) =>
        new TravelRouteResult(TravelRouteStatus.ServiceUnavailable, "fake service accepts no routes");

    /// <summary>Registers a handler the way an unrelated subscriber ahead of
    /// Echo would, bypassing the refusal switch.</summary>
    internal void AddExternalSubscriber(Action<TravelTransition> handler) => _subscribers.Add(handler);

    internal void SetAvailability(ServiceAvailability state)
    {
        Availability = state;
        foreach (var handler in _healthHandlers.ToArray()) handler(state);
    }

    internal void Emit(TravelTransition transition)
    {
        _queue.Enqueue(transition);
        if (_dispatching) return;
        _dispatching = true;
        try
        {
            while (_queue.Count > 0)
            {
                var fact = _queue.Dequeue();
                foreach (var subscriber in _subscribers.ToArray()) subscriber(fact);
            }
        }
        finally { _dispatching = false; }
    }
}

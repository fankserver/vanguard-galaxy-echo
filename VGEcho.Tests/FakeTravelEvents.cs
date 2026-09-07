using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI;

namespace VGEcho.Tests;

/// <summary>Stand-in for the API's travel hub, reproducing the two dispatch
/// properties Echo's observer depends on: subscriber callbacks run
/// synchronously, and a fact emitted from inside a callback is QUEUED and
/// delivered after the current one rather than nesting.</summary>
internal sealed class FakeTravelEvents : ITravelEvents
{
    private readonly List<Action<TravelTransition>> _subscribers = new List<Action<TravelTransition>>();
    private readonly Queue<TravelTransition> _queue = new Queue<TravelTransition>();
    private bool _dispatching;

    internal readonly List<string> Owners = new List<string>();

    /// <summary>When set, <see cref="Subscribe"/> throws it, mimicking a provider
    /// that refuses the subscription.</summary>
    internal Exception? RefuseSubscription;

    internal int LiveSubscriptions => _subscribers.Count;

    public Guid? SessionId { get; set; }
    public TravelLocation? CurrentLocation => null;
    public bool IsDispatchingCallbacks => _dispatching;

    public IDisposable Subscribe(string owner, Action<TravelTransition> callback)
    {
        if (RefuseSubscription != null) throw RefuseSubscription;
        Owners.Add(owner);
        _subscribers.Add(callback);
        return new Unsubscribe(this, callback);
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

    private sealed class Unsubscribe : IDisposable
    {
        private readonly FakeTravelEvents _hub;
        private readonly Action<TravelTransition> _callback;
        internal Unsubscribe(FakeTravelEvents hub, Action<TravelTransition> callback) { _hub = hub; _callback = callback; }
        public void Dispose() => _hub._subscribers.Remove(_callback);
    }
}

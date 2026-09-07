using System;

namespace VGEcho.Travel;

/// <summary>
/// The plugin-free handle <see cref="Plugin"/> holds onto the VGModAPI travel
/// subscription.
///
/// <para>This indirection is the whole point of the soft dependency: no field,
/// parameter or return type on any always-JIT plugin member may name a type
/// from <c>VGModAPI.Abstractions</c>, because Mono resolves those tokens when
/// it compiles the member — before any <c>try</c> block inside it can catch the
/// missing-assembly failure. Every API type therefore lives behind
/// <see cref="TravelArrivalBridge"/>'s no-inline factory methods, and the
/// plugin only ever sees this interface.</para>
/// </summary>
internal interface IArrivalSnapObserver : IDisposable
{
    /// <summary>False once disposed, or once a callback faulted the observer.</summary>
    bool IsListening { get; }
}

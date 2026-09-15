using System;
using System.Runtime.CompilerServices;
using VGModAPI;

namespace VGEcho.Travel;

/// <summary>
/// The single guarded doorway between VGEcho and <c>VGModAPI.Abstractions</c>.
///
/// <para>Both public entry points are <see cref="MethodImplOptions.NoInlining"/>
/// on purpose. When the API is not installed, Mono throws while compiling the
/// callee body — at the call site, inside the caller's <c>try</c>. Inlining
/// would move those tokens into <c>Plugin.Awake</c> itself, where the failure
/// would happen before any handler exists and would take the whole plugin down
/// with it. The class holds no fields and neither signature names an API type,
/// so merely loading this type is safe with the API absent.</para>
///
/// <para>There is no fallback path. If the API refuses, arrival-snap stays off;
/// Echo never reinstalls a direct <c>TravelManager</c> hook behind its back.</para>
/// </summary>
internal static class TravelArrivalBridge
{
    /// <summary>Reads the live travel service's availability through
    /// <c>ModApi.Services.Travel</c> and applies
    /// <see cref="ArrivalSnapBinding.Evaluate"/>. Call only after confirming the
    /// API plugin is loaded, and only inside a <c>try</c>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static ArrivalSnapAdmission Admit(Version installedApiVersion) =>
        ArrivalSnapBinding.Evaluate(installedApiVersion, ReadTravelReason());

    /// <summary>Reads the one availability value admission needs. A refused
    /// services-root read (startup not completed, or already shut down) is a
    /// catchable runtime failure inside this already-non-inlined body, so it
    /// becomes a reasoned refusal rather than an exception for the caller. Any
    /// other API failure still surfaces there.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TravelServiceReason ReadTravelReason()
    {
        try
        {
            // Services.Travel is stable and non-null once published; the getter
            // itself throws until the API's startup completes.
            return (TravelServiceReason)(int)ModApi.Services.Travel.Availability.Reason;
        }
        catch (InvalidOperationException)
        {
            return TravelServiceReason.NotStarted;
        }
    }

    /// <summary>Subscribes to the travel service. Call only after
    /// <see cref="Admit"/> returned <see cref="ArrivalSnapAdmission.Admitted"/>,
    /// and only inside a <c>try</c>: the provider is entitled to refuse.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static IArrivalSnapObserver Subscribe(Func<ArrivalSnapGates> gates,
        Action snap, Action<string> trace, Action<string> failed) =>
        new TravelArrivalObserver(
            ModApi.Services.Travel,
            gates, snap, trace, failed);
}

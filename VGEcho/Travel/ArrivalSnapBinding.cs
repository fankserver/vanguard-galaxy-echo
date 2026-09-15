using System;

namespace VGEcho.Travel;

/// <summary>Whether Echo may subscribe to the API's travel observations, and if not, why.</summary>
internal enum ArrivalSnapAdmission
{
    Admitted,

    /// <summary>VGModAPI is not installed. Arrival-snap is simply off; every other
    /// Echo feature loads normally.</summary>
    ApiAbsent,

    /// <summary>Installed, but outside the version window this bridge was written
    /// against. No partial binding is attempted.</summary>
    ApiVersionUnsupported,

    /// <summary>The API plugin is loaded but <c>ModApi.Services</c> refused the
    /// read (its services were never published, or were already cleared at
    /// shutdown). Load order puts the API's Awake before ours when it is
    /// installed, so this is the startup-failure/shutdown race corner.</summary>
    ApiNotStarted,

    /// <summary>The travel group is disabled — <c>[Travel] Enabled = false</c>
    /// in <c>vgmodapi.cfg</c>.</summary>
    TravelDisabled,

    /// <summary>The service object exists (it is always non-null after API
    /// startup) but reports itself unavailable for an environmental reason:
    /// unsupported game, failed binding, or a missing dependency.</summary>
    TravelUnavailable,

    /// <summary>The service reports a terminal <c>ObserverFault</c> or
    /// <c>ApiStopped</c>. These do not repair on session replacement or save
    /// reload; only a game restart does.</summary>
    TravelFaulted,
}

/// <summary>How loudly a refused binding should be reported.</summary>
internal enum ArrivalSnapLogLevel
{
    /// <summary>An expected, designed degraded state — nothing is wrong.</summary>
    Info,

    /// <summary>The API is installed but could not be used as intended.</summary>
    Warning,
}

/// <summary>
/// Plugin-free mirror of <c>VGModAPI.ServiceUnavailableReason</c>, plus the
/// <see cref="NotStarted"/> sentinel for "the services root itself refused the
/// read". The numeric values must equal the API enum's —
/// <c>ArrivalSnapBindingTests</c> pins every member against the real enum so a
/// reordering upstream cannot silently misclassify a refusal.
///
/// <para>The mirror exists so <see cref="ArrivalSnapBinding.Evaluate"/> keeps its
/// shape-scan guarantee: no always-loaded VGEcho type may name an
/// <c>VGModAPI.Abstractions</c> type, and the admission rules are deliberately
/// unit-testable on a bare host with no Unity, BepInEx or API reference.</para>
/// </summary>
internal enum TravelServiceReason
{
    /// <summary><c>ModApi.Services</c> threw (API never started, or already shut down).</summary>
    NotStarted = -1,

    None = 0,
    Disabled = 1,
    UnsupportedGame = 2,
    BindingFailed = 3,
    DependencyUnavailable = 4,
    ObserverFault = 5,
    ApiStopped = 6,
}

/// <summary>Pure admission rules for the VGModAPI travel soft-dependency.
/// Deliberately free of API, BepInEx and Unity types so the version window and
/// the service-health requirements are unit-testable on a bare host.</summary>
internal static class ArrivalSnapBinding
{
    /// <summary>Must equal <c>VGModAPI.ModApi.PluginId</c>. Spelled as a literal
    /// so the BepInEx soft-dependency attribute and the <c>Chainloader</c> lookup
    /// carry no reference to the API assembly; <c>ArrivalSnapBindingTests</c>
    /// pins it against the real constant.</summary>
    internal const string ApiPluginId = "vgmodapi";

    /// <summary>First API release whose public <c>ITravelService.Transitioned</c>
    /// emits <c>RouteCompleted</c> from the verified final-route boundary on the
    /// surface this bridge consumes. Owner decision: the next shipped prerelease
    /// is 0.2.8 (the API checkout's 0.2.10 stamp is corrected before release).</summary>
    internal static readonly Version MinimumApiVersion = new Version(0, 2, 8);

    /// <summary>The API is pre-1.0 and its contracts are still allowed to move
    /// between minor versions, so the bridge refuses anything at or beyond the
    /// next minor rather than guessing.</summary>
    internal static readonly Version FirstUnsupportedApiVersion = new Version(0, 3, 0);

    /// <param name="installedApiVersion">Version reported by BepInEx for the
    /// installed API plugin, or null when it is not installed at all.</param>
    /// <param name="reason">Live travel-service health as read through the
    /// bridge's guarded access, projected onto <see cref="TravelServiceReason"/>.
    /// Ignored unless the version window admits the binding.</param>
    internal static ArrivalSnapAdmission Evaluate(Version? installedApiVersion, TravelServiceReason reason)
    {
        if (installedApiVersion == null) return ArrivalSnapAdmission.ApiAbsent;
        if (installedApiVersion < MinimumApiVersion || installedApiVersion >= FirstUnsupportedApiVersion)
            return ArrivalSnapAdmission.ApiVersionUnsupported;
        return reason switch
        {
            TravelServiceReason.None => ArrivalSnapAdmission.Admitted,
            TravelServiceReason.NotStarted => ArrivalSnapAdmission.ApiNotStarted,
            TravelServiceReason.Disabled => ArrivalSnapAdmission.TravelDisabled,
            TravelServiceReason.ObserverFault or TravelServiceReason.ApiStopped => ArrivalSnapAdmission.TravelFaulted,
            _ => ArrivalSnapAdmission.TravelUnavailable,
        };
    }

    /// <summary>Appended to every refusal so the log never leaves the reader
    /// guessing whether the rest of the plugin still works, or whether Echo
    /// silently fell back to hooking the game directly.</summary>
    private const string Consequence =
        " Autopilot arrival-snap is disabled; ETA-sync and every other VGEcho feature are unaffected, " +
        "and no direct TravelManager hook is installed as a fallback.";

    /// <summary>A missing optional dependency is the documented degraded state,
    /// not a problem to warn about; every other refusal means the API IS
    /// installed but could not be used as intended, which is worth a warning.</summary>
    internal static ArrivalSnapLogLevel LevelFor(ArrivalSnapAdmission admission) =>
        admission == ArrivalSnapAdmission.ApiAbsent ? ArrivalSnapLogLevel.Info : ArrivalSnapLogLevel.Warning;

    /// <summary>Operator-facing explanation for a refused binding.</summary>
    internal static string Explain(ArrivalSnapAdmission admission) => admission switch
    {
        ArrivalSnapAdmission.ApiAbsent =>
            "VGModAPI is not installed." + Consequence,
        ArrivalSnapAdmission.ApiVersionUnsupported =>
            "The installed VGModAPI is outside the supported " + MinimumApiVersion + " to below " + FirstUnsupportedApiVersion + " window." + Consequence,
        ArrivalSnapAdmission.ApiNotStarted =>
            "VGModAPI is loaded but never published its services (its own startup failed, or it is already shutting down)." + Consequence,
        ArrivalSnapAdmission.TravelDisabled =>
            "VGModAPI's travel group is disabled (set [Travel] Enabled = true in vgmodapi.cfg)." + Consequence,
        ArrivalSnapAdmission.TravelUnavailable =>
            "VGModAPI reports its travel service as unavailable for an environmental reason (unsupported game, failed binding, or missing dependency)." + Consequence,
        ArrivalSnapAdmission.TravelFaulted =>
            "VGModAPI's travel service is terminally faulted or stopped; it does not recover without a game restart." + Consequence,
        _ => "Autopilot arrival-snap is bound to VGModAPI travel observations.",
    };
}

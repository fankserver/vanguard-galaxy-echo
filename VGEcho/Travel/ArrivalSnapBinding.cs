using System;

namespace VGEcho.Travel;

/// <summary>Whether Echo may subscribe to the API's travel surface, and if not, why.</summary>
internal enum ArrivalSnapAdmission
{
    Admitted,

    /// <summary>VGModAPI is not installed. Arrival-snap is simply off; every other
    /// Echo feature loads normally.</summary>
    ApiAbsent,

    /// <summary>Installed, but outside the version window this bridge was written
    /// against. No partial binding is attempted.</summary>
    ApiVersionUnsupported,

    /// <summary><c>ModApi.Travel</c> is null — the travel group did not bind, or
    /// <c>[Travel] Enabled</c> is false in <c>vgmodapi.cfg</c>.</summary>
    ServiceUnavailable,

    /// <summary>The service object exists but the API reports the
    /// <c>native-travel</c> capability as unavailable (unbound or faulted).</summary>
    CapabilityUnavailable,
}

/// <summary>How loudly a refused binding should be reported.</summary>
internal enum ArrivalSnapLogLevel
{
    /// <summary>An expected, designed degraded state — nothing is wrong.</summary>
    Info,

    /// <summary>The API is installed but could not be used as intended.</summary>
    Warning,
}

/// <summary>Pure admission rules for the VGModAPI travel soft-dependency.
/// Deliberately free of API, BepInEx and Unity types so the version window and
/// the capability/service requirements are unit-testable on a bare host.</summary>
internal static class ArrivalSnapBinding
{
    /// <summary>Must equal <c>VGModAPI.ModApi.PluginId</c>. Spelled as a literal
    /// so the BepInEx soft-dependency attribute and the <c>Chainloader</c> lookup
    /// carry no reference to the API assembly; <c>ArrivalSnapBindingTests</c>
    /// pins it against the real constant.</summary>
    internal const string ApiPluginId = "vgmodapi";

    /// <summary>Must equal the capability name VGModAPI publishes for its native
    /// travel group.</summary>
    internal const string CapabilityName = "native-travel";

    /// <summary>Subscription owner id reported to the API hub.</summary>
    internal const string SubscriptionOwner = "vgecho.arrival-snap";

    /// <summary>First API release whose public <c>ITravelEvents</c> emits
    /// <c>RouteCompleted</c> from the verified final-route boundary.</summary>
    internal static readonly Version MinimumApiVersion = new Version(0, 1, 9);

    /// <summary>The API is pre-1.0 and its contracts are still allowed to move
    /// between minor versions, so the bridge refuses anything at or beyond the
    /// next minor rather than guessing.</summary>
    internal static readonly Version FirstUnsupportedApiVersion = new Version(0, 2, 0);

    /// <param name="installedApiVersion">Version reported by BepInEx for the
    /// installed API plugin, or null when it is not installed at all.</param>
    /// <param name="serviceExposed">Whether <c>ModApi.Travel</c> is non-null.</param>
    /// <param name="capabilityAvailable">Whether the API reports
    /// <see cref="CapabilityName"/> as available.</param>
    internal static ArrivalSnapAdmission Evaluate(Version? installedApiVersion, bool serviceExposed, bool capabilityAvailable)
    {
        if (installedApiVersion == null) return ArrivalSnapAdmission.ApiAbsent;
        if (installedApiVersion < MinimumApiVersion || installedApiVersion >= FirstUnsupportedApiVersion)
            return ArrivalSnapAdmission.ApiVersionUnsupported;
        if (!serviceExposed) return ArrivalSnapAdmission.ServiceUnavailable;
        if (!capabilityAvailable) return ArrivalSnapAdmission.CapabilityUnavailable;
        return ArrivalSnapAdmission.Admitted;
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
        ArrivalSnapAdmission.ServiceUnavailable =>
            "VGModAPI exposes no travel service (set [Travel] Enabled = true in vgmodapi.cfg)." + Consequence,
        ArrivalSnapAdmission.CapabilityUnavailable =>
            "VGModAPI reports the " + CapabilityName + " capability as unavailable." + Consequence,
        _ => "Autopilot arrival-snap is bound to VGModAPI travel events.",
    };
}

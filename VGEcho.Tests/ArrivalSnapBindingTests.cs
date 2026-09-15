using System;
using VGEcho.Travel;
using Xunit;

namespace VGEcho.Tests;

/// <summary>The soft-dependency admission rules: which installed API states may
/// bind arrival-snap, and the identifiers and enum layout Echo hard-codes to
/// avoid naming an API type on a plugin member.</summary>
public sealed class ArrivalSnapBindingTests
{
    /// <summary>The BepInEx soft-dependency attribute and the Chainloader lookup
    /// use a literal so the plugin's always-JIT members carry no reference to
    /// VGModAPI. This pins that literal to the real constant.</summary>
    [Fact]
    public void TheHardCodedPluginIdMatchesTheApiConstant()
        => Assert.Equal(VGModAPI.ModApi.PluginId, ArrivalSnapBinding.ApiPluginId);

    /// <summary>The build and this test suite must reference the verified
    /// published 0.2.8 contract (the release artifact linked by
    /// <c>make link-api</c>), never a stale sibling build with another stamp —
    /// admission semantics below are only qualified against that exact assembly.</summary>
    [Fact]
    public void TheApiContractUnderTestIsThePublishedRelease()
        => Assert.Equal(new Version(0, 2, 8, 0), typeof(VGModAPI.ModApi).Assembly.GetName().Version);

    /// <summary>The bridge is written against the 0.2.8 prerelease surface (owner
    /// decision; the API checkout's 0.2.10 stamp is corrected before release),
    /// and refuses the next minor because the API is pre-1.0.</summary>
    [Fact]
    public void TheVersionWindowIsTheReleasedTravelServiceSurface()
    {
        Assert.Equal(new Version(0, 2, 8), ArrivalSnapBinding.MinimumApiVersion);
        Assert.Equal(new Version(0, 3, 0), ArrivalSnapBinding.FirstUnsupportedApiVersion);
        Assert.True(ArrivalSnapBinding.MinimumApiVersion < ArrivalSnapBinding.FirstUnsupportedApiVersion);
    }

    /// <summary>The admission logic branches on the plugin-free mirror, whose
    /// members must keep the API enum's numeric values: a reordering upstream
    /// would otherwise silently misclassify health readings.</summary>
    [Theory]
    [InlineData(VGModAPI.ServiceUnavailableReason.None, (int)TravelServiceReason.None)]
    [InlineData(VGModAPI.ServiceUnavailableReason.Disabled, (int)TravelServiceReason.Disabled)]
    [InlineData(VGModAPI.ServiceUnavailableReason.UnsupportedGame, (int)TravelServiceReason.UnsupportedGame)]
    [InlineData(VGModAPI.ServiceUnavailableReason.BindingFailed, (int)TravelServiceReason.BindingFailed)]
    [InlineData(VGModAPI.ServiceUnavailableReason.DependencyUnavailable, (int)TravelServiceReason.DependencyUnavailable)]
    [InlineData(VGModAPI.ServiceUnavailableReason.ObserverFault, (int)TravelServiceReason.ObserverFault)]
    [InlineData(VGModAPI.ServiceUnavailableReason.ApiStopped, (int)TravelServiceReason.ApiStopped)]
    public void TheMirrorReasonKeepsTheApiEnumValue(VGModAPI.ServiceUnavailableReason real, int mirror)
        => Assert.Equal((int)real, (int)mirror);

    /// <summary>The mirror's sentinel for "the services root itself refused the
    /// read" must not collide with any real enum value.</summary>
    [Fact]
    public void TheNotStartedSentinelIsOutsideTheRealEnumRange()
        => Assert.True(
            (int)TravelServiceReason.NotStarted < 0 &&
            (int)TravelServiceReason.NotStarted < (int)VGModAPI.ServiceUnavailableReason.None);

    [Fact]
    public void AnAbsentApiIsAdmissionApiAbsentAndNotAFailure()
        => Assert.Equal(ArrivalSnapAdmission.ApiAbsent, ArrivalSnapBinding.Evaluate(null, TravelServiceReason.None));

    [Theory]
    [InlineData("0.2.7")]
    [InlineData("0.2.0")]
    [InlineData("0.1.9")]
    [InlineData("0.0.9")]
    [InlineData("0.3.0")]
    [InlineData("1.0.0")]
    public void VersionsOutsideTheSupportedWindowAreRefused(string version)
        => Assert.Equal(ArrivalSnapAdmission.ApiVersionUnsupported,
            ArrivalSnapBinding.Evaluate(Version.Parse(version), TravelServiceReason.None));

    [Theory]
    [InlineData("0.2.8")]
    [InlineData("0.2.8.0")]
    [InlineData("0.2.9")]
    [InlineData("0.2.10")]
    [InlineData("0.2.99.4")]
    public void VersionsInsideTheSupportedWindowAreAdmitted(string version)
        => Assert.Equal(ArrivalSnapAdmission.Admitted,
            ArrivalSnapBinding.Evaluate(Version.Parse(version), TravelServiceReason.None));

    /// <summary>The version window is checked before service health, so an
    /// unsupported API is refused with the version reason even while healthy.</summary>
    [Fact]
    public void TheVersionWindowIsConsultedBeforeServiceHealth()
        => Assert.Equal(ArrivalSnapAdmission.ApiVersionUnsupported,
            ArrivalSnapBinding.Evaluate(new Version(0, 3, 0), TravelServiceReason.ObserverFault));

    [Fact]
    public void AServicesRootThatRefusedTheReadIsReportedAsNotStarted()
        => Assert.Equal(ArrivalSnapAdmission.ApiNotStarted,
            ArrivalSnapBinding.Evaluate(ArrivalSnapBinding.MinimumApiVersion, TravelServiceReason.NotStarted));

    [Fact]
    public void ADisabledTravelGroupIsReportedSeparatelyFromEnvironmentalFaults()
        => Assert.Equal(ArrivalSnapAdmission.TravelDisabled,
            ArrivalSnapBinding.Evaluate(ArrivalSnapBinding.MinimumApiVersion, TravelServiceReason.Disabled));
    [Theory]
    [InlineData((int)TravelServiceReason.UnsupportedGame)]
    [InlineData((int)TravelServiceReason.BindingFailed)]
    [InlineData((int)TravelServiceReason.DependencyUnavailable)]
    public void EnvironmentalUnavailabilitySharesOneRefusal(int value)
        => Assert.Equal(ArrivalSnapAdmission.TravelUnavailable,
            ArrivalSnapBinding.Evaluate(ArrivalSnapBinding.MinimumApiVersion, (TravelServiceReason)value));

    [Theory]
    [InlineData((int)TravelServiceReason.ObserverFault)]
    [InlineData((int)TravelServiceReason.ApiStopped)]
    public void TerminalServiceHealthIsReportedAsFaulted(int value)
        => Assert.Equal(ArrivalSnapAdmission.TravelFaulted,
            ArrivalSnapBinding.Evaluate(ArrivalSnapBinding.MinimumApiVersion, (TravelServiceReason)value));

    /// <summary>A missing optional dependency is the documented degraded state
    /// and must not be reported as a problem; every other refusal means the API
    /// is installed but unusable, which is.</summary>
    [Fact]
    public void OnlyTheAbsentApiIsReportedAtInfoLevel()
    {
        Assert.Equal(ArrivalSnapLogLevel.Info, ArrivalSnapBinding.LevelFor(ArrivalSnapAdmission.ApiAbsent));
        foreach (ArrivalSnapAdmission admission in Enum.GetValues(typeof(ArrivalSnapAdmission)))
        {
            if (admission is ArrivalSnapAdmission.ApiAbsent or ArrivalSnapAdmission.Admitted) continue;
            Assert.Equal(ArrivalSnapLogLevel.Warning, ArrivalSnapBinding.LevelFor(admission));
        }
    }

    [Fact]
    public void EveryRefusalStatesThatOnlyArrivalSnapIsLostAndThereIsNoFallback()
    {
        foreach (ArrivalSnapAdmission admission in Enum.GetValues(typeof(ArrivalSnapAdmission)))
        {
            if (admission == ArrivalSnapAdmission.Admitted) continue;
            var explanation = ArrivalSnapBinding.Explain(admission);
            Assert.Contains("arrival-snap is disabled", explanation);
            Assert.Contains("no direct TravelManager hook", explanation);
        }
    }
}

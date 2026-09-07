using System;
using VGEcho.Travel;
using Xunit;

namespace VGEcho.Tests;

/// <summary>The soft-dependency admission rules: which installed API states may
/// bind arrival-snap, and the identifiers Echo hard-codes to avoid naming an
/// API type on a plugin member.</summary>
public sealed class ArrivalSnapBindingTests
{
    /// <summary>The BepInEx soft-dependency attribute and the Chainloader lookup
    /// use a literal so the plugin's always-JIT members carry no reference to
    /// VGModAPI. This pins that literal to the real constant.</summary>
    [Fact]
    public void TheHardCodedPluginIdMatchesTheApiConstant()
        => Assert.Equal(VGModAPI.ModApi.PluginId, ArrivalSnapBinding.ApiPluginId);

    [Fact]
    public void TheMinimumVersionIsTheReleaseThatPublishedRouteCompleted()
    {
        Assert.Equal(new Version(0, 1, 9), ArrivalSnapBinding.MinimumApiVersion);
        Assert.True(ArrivalSnapBinding.MinimumApiVersion < ArrivalSnapBinding.FirstUnsupportedApiVersion);
    }

    [Fact]
    public void AnAbsentApiIsAdmissionApiAbsentAndNotAFailure()
        => Assert.Equal(ArrivalSnapAdmission.ApiAbsent, ArrivalSnapBinding.Evaluate(null, true, true));

    [Theory]
    [InlineData("0.1.8")]
    [InlineData("0.1.0")]
    [InlineData("0.0.9")]
    [InlineData("0.2.0")]
    [InlineData("1.0.0")]
    public void VersionsOutsideTheSupportedWindowAreRefused(string version)
        => Assert.Equal(ArrivalSnapAdmission.ApiVersionUnsupported,
            ArrivalSnapBinding.Evaluate(Version.Parse(version), true, true));

    [Theory]
    [InlineData("0.1.9")]
    [InlineData("0.1.9.0")]
    [InlineData("0.1.10")]
    [InlineData("0.1.99.4")]
    public void VersionsInsideTheSupportedWindowAreAdmitted(string version)
        => Assert.Equal(ArrivalSnapAdmission.Admitted,
            ArrivalSnapBinding.Evaluate(Version.Parse(version), true, true));

    [Fact]
    public void AnUnexposedTravelServiceIsRefusedBeforeTheCapabilityIsConsulted()
        => Assert.Equal(ArrivalSnapAdmission.ServiceUnavailable,
            ArrivalSnapBinding.Evaluate(ArrivalSnapBinding.MinimumApiVersion, false, true));

    [Fact]
    public void AnUnavailableNativeTravelCapabilityIsRefused()
        => Assert.Equal(ArrivalSnapAdmission.CapabilityUnavailable,
            ArrivalSnapBinding.Evaluate(ArrivalSnapBinding.MinimumApiVersion, true, false));

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

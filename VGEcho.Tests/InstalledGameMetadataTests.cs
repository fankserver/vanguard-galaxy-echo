using System;
using System.Linq;
using Mono.Cecil;
using Xunit;

namespace VGEcho.Tests;

/// <summary>
/// Compatibility facts about the ORIGINAL installed <c>Assembly-CSharp.dll</c>,
/// read as metadata with Cecil. Nothing here loads the assembly or invokes a
/// native method: these are the shapes VGEcho's remaining native writes and the
/// API's final-route boundary both depend on.
///
/// <para>Path comes from <c>VG_GAME_ASSEMBLY</c> and its absence throws rather
/// than skipping, so a host without the game cannot report a green
/// compatibility check. The category keeps it out of the asset-free
/// <c>make test</c> run; <c>make check-bindings</c> is the target that supplies
/// the path.</para>
/// </summary>
[Trait("Category", "InstalledGame")]
public sealed class InstalledGameMetadataTests
{
    private static string AssemblyPath => Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
        ?? throw new InvalidOperationException(
            "Run make check-bindings, or set VG_GAME_ASSEMBLY to the installed original Assembly-CSharp.dll.");

    private static ModuleDefinition Read() => ModuleDefinition.ReadModule(AssemblyPath);

    /// <summary>ETA-sync and arrival-snap both write these compiler-generated
    /// backing fields through <c>AccessTools.FieldRefAccess</c>. A rename breaks
    /// the feature at load time, so the names are asserted against the shipped
    /// assembly rather than assumed.</summary>
    [Theory]
    [InlineData("<updateTimer>k__BackingField")]
    [InlineData("<updateTimerBase>k__BackingField")]
    public void IdleManagerStillDeclaresTheAutopilotCycleBackingField(string name)
    {
        using var module = Read();
        var idle = module.GetType("Behaviour.Gameplay.IdleManager");
        var field = Assert.Single(idle.Fields, candidate => candidate.Name == name);
        Assert.Equal("System.Single", field.FieldType.FullName);
        Assert.False(field.IsStatic);
    }

    /// <summary>The snap only helps because <c>Update</c> decrements
    /// <c>updateTimer</c> and calls <c>FindActivity</c> as soon as it goes
    /// negative; zeroing it therefore moves the decision to the next tick.</summary>
    [Fact]
    public void IdleManagerUpdateStillDrivesFindActivityFromTheCycleTimer()
    {
        using var module = Read();
        var idle = module.GetType("Behaviour.Gameplay.IdleManager");
        var update = Assert.Single(idle.Methods, method => method.Name == "Update" && method.Parameters.Count == 0);
        var called = update.Body.Instructions
            .Select(instruction => (instruction.Operand as MethodReference)?.Name)
            .ToArray();
        Assert.Contains("FindActivity", called);
        // The original assembly reaches the auto-property through its accessors;
        // the backing field is what Echo writes behind them.
        Assert.Contains("get_updateTimer", called);
        Assert.Contains("set_updateTimer", called);
    }

    /// <summary>The API emits <c>RouteCompleted</c> from a postfix on this method,
    /// gated on the player's empty waypoint list and on <c>TravelActive()</c> —
    /// which still consults <c>usingJumpgate</c>, so a jump-gate iterator that has
    /// not finished cannot look like a completed route.</summary>
    [Fact]
    public void TheFinalRouteBoundaryMethodsTheApiHooksStillExist()
    {
        using var module = Read();
        var travel = module.GetType("Behaviour.Managers.TravelManager");
        var boundary = Assert.Single(travel.Methods, method => method.Name == "TravelToNextWaypoint" && method.Parameters.Count == 0);
        Assert.True(boundary.IsPublic);
        Assert.Equal("System.Void", boundary.ReturnType.FullName);

        var active = Assert.Single(travel.Methods, method => method.Name == "TravelActive" && method.Parameters.Count == 0);
        Assert.Equal("System.Boolean", active.ReturnType.FullName);
        Assert.True(active.IsPublic);
        // Still "no travel coroutine AND no jump-gate hop", so a jump iterator that
        // has not finished cannot be mistaken for a completed route.
        Assert.Contains(active.Body.Instructions,
            instruction => (instruction.Operand as FieldReference)?.Name == "travelCoroutine");
        Assert.Contains(active.Body.Instructions,
            instruction => (instruction.Operand as MethodReference)?.Name == "get_usingJumpgate");

        var waypoints = Assert.Single(module.GetType("Source.Player.GamePlayer").Fields,
            candidate => candidate.Name == "waypoints");
        Assert.False(waypoints.IsStatic);
    }

    /// <summary>Arrival-snap reads the already-registered manager instead of
    /// <c>Singleton&lt;T&gt;.Instance</c>, whose getter runs
    /// <c>FindAnyObjectByType</c> and writes the shared static cache. Both members
    /// must still exist with those distinct shapes for that choice to hold.</summary>
    [Fact]
    public void SingletonCurrentIsStillThePureReadAndInstanceIsStillTheCachingOne()
    {
        using var module = Read();
        var singleton = module.GetType("Behaviour.Util.Singleton`1");
        var current = Assert.Single(singleton.Methods, method => method.Name == "get_Current");
        Assert.DoesNotContain(current.Body.Instructions,
            instruction => (instruction.Operand as MethodReference)?.Name == "FindAnyObjectByType");

        var instance = Assert.Single(singleton.Methods, method => method.Name == "get_Instance");
        Assert.Contains(instance.Body.Instructions,
            instruction => (instruction.Operand as MethodReference)?.Name == "FindAnyObjectByType");
    }
}

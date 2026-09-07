using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using VGEcho.Travel;
using Xunit;

namespace VGEcho.Tests;

/// <summary>
/// Metadata proofs about the SHIPPED <c>VGEcho.dll</c>, read with Cecil and
/// never loaded or executed.
///
/// <para>The soft dependency is a claim about IL layout, not about source
/// intent: Mono resolves a method's type tokens when it compiles that method,
/// so a single VGModAPI token on <c>Plugin.Awake</c> would crash the whole
/// plugin for players without the API — before any <c>try</c> inside it could
/// run. These tests are the regression net for exactly that.</para>
///
/// <para>The assembly path is explicit (<c>VGECHO_ASSEMBLY</c>, supplied by
/// <c>make test</c>) and its absence throws: a shape test that quietly skips
/// when the build output is missing would be a false pass.</para>
/// </summary>
public sealed class PluginAssemblyShapeTests
{
    private const string ApiAssembly = "VGModAPI.Abstractions";

    /// <summary>The two types allowed to name an API type. Everything else in
    /// VGEcho must be reachable with the API assembly absent.</summary>
    private static readonly string[] ApiFacingTypes =
    {
        "VGEcho.Travel.TravelArrivalBridge",
        "VGEcho.Travel.TravelArrivalObserver",
    };

    private static string AssemblyPath => Environment.GetEnvironmentVariable("VGECHO_ASSEMBLY")
        ?? throw new InvalidOperationException(
            "Set VGECHO_ASSEMBLY to the built VGEcho.dll (make test does this) so the IL shape is checked against a real build.");

    private static ModuleDefinition Read() => ModuleDefinition.ReadModule(AssemblyPath);

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        var pending = new Stack<TypeDefinition>(module.Types);
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            yield return type;
            foreach (var nested in type.NestedTypes) pending.Push(nested);
        }
    }

    /// <summary>True when resolving this reference would need the API assembly:
    /// Cecil records an external type's home assembly as its scope. The walk
    /// follows every construction that can hide a token behind another type:
    /// generic arguments, array/byref/pointer element types, and declaring
    /// types of nested references.</summary>
    internal static bool FromApi(TypeReference? type)
    {
        if (type == null || type is GenericParameter) return false;
        if (type.Scope?.Name == ApiAssembly) return true;
        if (type is GenericInstanceType generic && generic.GenericArguments.Any(FromApi)) return true;
        if (type is TypeSpecification specification && FromApi(specification.ElementType)) return true;
        return type.DeclaringType != null && FromApi(type.DeclaringType);
    }

    /// <summary>Every API type name a type's inheritance, members or IL operands
    /// would make Mono resolve. Base types and interfaces matter because they are
    /// loaded with the type itself, before any method body runs; generic method
    /// type arguments matter because they live on the call site, not on the
    /// callee's signature.</summary>
    internal static IEnumerable<string> ApiReferences(TypeDefinition type)
    {
        if (FromApi(type.BaseType)) yield return type.FullName + " : base " + type.BaseType.FullName;
        foreach (var implemented in type.Interfaces)
            if (FromApi(implemented.InterfaceType)) yield return type.FullName + " : implements " + implemented.InterfaceType.FullName;

        foreach (var field in type.Fields)
            if (FromApi(field.FieldType)) yield return type.FullName + "." + field.Name + " : " + field.FieldType.FullName;

        foreach (var method in type.Methods)
        {
            if (FromApi(method.ReturnType)) yield return method.FullName + " returns " + method.ReturnType.FullName;
            foreach (var parameter in method.Parameters)
                if (FromApi(parameter.ParameterType)) yield return method.FullName + " takes " + parameter.ParameterType.FullName;
            if (!method.HasBody) continue;
            foreach (var variable in method.Body.Variables)
                if (FromApi(variable.VariableType)) yield return method.FullName + " local " + variable.VariableType.FullName;
            foreach (var instruction in method.Body.Instructions)
                foreach (var referenced in Operands(instruction))
                    if (FromApi(referenced)) yield return method.FullName + " -> " + referenced.FullName;
        }
    }

    private static IEnumerable<TypeReference> Operands(Instruction instruction)
    {
        switch (instruction.Operand)
        {
            case TypeReference type: yield return type; break;
            case MethodReference method:
                yield return method.DeclaringType;
                yield return method.ReturnType;
                foreach (var parameter in method.Parameters) yield return parameter.ParameterType;
                if (method is GenericInstanceMethod instantiated)
                    foreach (var argument in instantiated.GenericArguments) yield return argument;
                break;
            case FieldReference field:
                yield return field.DeclaringType;
                yield return field.FieldType;
                break;
        }
    }

    [Fact]
    public void OnlyTheTravelBridgeAndItsObserverNameAnApiType()
    {
        using var module = Read();
        var offenders = AllTypes(module)
            .Where(type => !ApiFacingTypes.Contains(DeclaringChain(type)))
            .SelectMany(ApiReferences)
            .ToArray();
        Assert.Equal(Array.Empty<string>(), offenders);
    }

    /// <summary>Compiler-generated closures and state machines are attributed to
    /// the type that declares them.</summary>
    private static string DeclaringChain(TypeDefinition type)
    {
        var root = type;
        while (root.DeclaringType != null) root = root.DeclaringType;
        return root.FullName;
    }

    [Fact]
    public void TheBridgeStillDoesNameApiTypesSoTheGuardIsProvingSomething()
    {
        using var module = Read();
        var bridge = AllTypes(module).Single(type => type.FullName == "VGEcho.Travel.TravelArrivalBridge");
        Assert.NotEmpty(ApiReferences(bridge));
        Assert.Contains(module.AssemblyReferences, reference => reference.Name == ApiAssembly);
    }

    /// <summary>Neither bridge entry point may be inlined into its caller: the
    /// missing-assembly failure has to happen at the call site, inside the
    /// caller's try block.</summary>
    [Theory]
    [InlineData("Admit")]
    [InlineData("Subscribe")]
    public void TheBridgeEntryPointsAreNoInline(string name)
    {
        using var module = Read();
        var method = AllTypes(module).Single(type => type.FullName == "VGEcho.Travel.TravelArrivalBridge")
            .Methods.Single(candidate => candidate.Name == name);
        Assert.True(method.NoInlining, name + " must be NoInlining.");
        Assert.False(FromApi(method.ReturnType));
        Assert.All(method.Parameters, parameter => Assert.False(FromApi(parameter.ParameterType)));
    }

    /// <summary>The pure write-time guard is only worth testing if production
    /// actually consults it, and it is only correct if production hands it the
    /// real native reads. This pins the whole chain in the shipped IL: the
    /// guard call, both native conditions, and the pure singleton reads.</summary>
    [Fact]
    public void ApplyArrivalSnapCallsThePureGuardWithTheLiveNativeReads()
    {
        using var module = Read();
        var apply = AllTypes(module).Single(type => type.FullName == "VGEcho.Patches.AutopilotTimingPatches")
            .Methods.Single(method => method.Name == "ApplyArrivalSnap");

        var calls = apply.Body.Instructions.Select(instruction => instruction.Operand as MethodReference)
            .Where(reference => reference != null).Select(reference => reference!).ToArray();
        var fields = apply.Body.Instructions.Select(instruction => instruction.Operand as FieldReference)
            .Where(reference => reference != null).Select(reference => reference!).ToArray();

        Assert.Contains(calls, reference => reference.Name == "Evaluate"
            && reference.DeclaringType.FullName == "VGEcho.Travel.ArrivalSnapApplyGuard");
        Assert.Contains(calls, reference => reference.Name == "TravelActive"
            && reference.DeclaringType.FullName == "Behaviour.Managers.TravelManager");
        Assert.Contains(fields, reference => reference.Name == "waypoints"
            && reference.DeclaringType.FullName == "Source.Player.GamePlayer");
        Assert.Contains(fields, reference => reference.Name == "current"
            && reference.DeclaringType.FullName == "Source.Player.GamePlayer");

        // Both managers are read through the pure Current accessor; Instance
        // would run FindAnyObjectByType and seed the shared singleton cache.
        Assert.Contains(calls, reference => reference.Name == "get_Current");
        Assert.DoesNotContain(calls, reference => reference.Name == "get_Instance");
    }

    /// <summary>A missing optional dependency is the documented degraded state,
    /// so the plugin must route it through the level chooser and be able to log
    /// at either level.</summary>
    [Fact]
    public void TheBindingReportRoutesItsLevelThroughTheSharedChooser()
    {
        using var module = Read();
        var bind = AllTypes(module).Single(type => type.FullName == "VGEcho.Plugin")
            .Methods.Single(method => method.Name == "BindArrivalSnap");
        var calls = bind.Body.Instructions.Select(instruction => (instruction.Operand as MethodReference)?.Name)
            .Where(name => name != null).ToArray();

        Assert.Contains("LevelFor", calls);
        Assert.Contains("LogInfo", calls);
        Assert.Contains("LogWarning", calls);
    }

    [Fact]
    public void ThePluginDeclaresTheApiAsASoftDependency()
    {
        using var module = Read();
        var plugin = AllTypes(module).Single(type => type.FullName == "VGEcho.Plugin");
        var dependency = Assert.Single(plugin.CustomAttributes,
            attribute => attribute.AttributeType.Name == "BepInDependency");
        // The blob is parsed by hand: Cecil's typed ConstructorArguments would have
        // to resolve BepInEx's DependencyFlags enum, and this suite deliberately
        // has no BepInEx assembly on disk.
        var (guid, flags) = StringAndEnumArguments(dependency);
        Assert.Equal(ArrivalSnapBinding.ApiPluginId, guid);
        // BepInEx DependencyFlags.SoftDependency == 2.
        Assert.Equal(2, flags);
    }

    /// <summary>Reads a <c>(string, enum)</c> custom-attribute blob directly:
    /// 2-byte prolog, length-prefixed UTF-8 string, then the enum's 4-byte value.</summary>
    private static (string Text, int Value) StringAndEnumArguments(CustomAttribute attribute)
    {
        var blob = attribute.GetBlob();
        var cursor = 2;
        int length = blob[cursor++];
        Assert.InRange(length, 0, 0x7f);
        var text = System.Text.Encoding.UTF8.GetString(blob, cursor, length);
        cursor += length;
        return (text, BitConverter.ToInt32(blob, cursor));
    }

    /// <summary>Arrival-snap must no longer own a hook on the native travel
    /// boundary: the completion fact comes from the API, and a leftover patch
    /// would double-drive the timer. The unrelated opt-in auto-refine postfix on
    /// the same native method is the positive control that this change did not
    /// reach past its own feature.</summary>
    [Fact]
    public void OnlyTheUnrelatedAutoRefineFeatureStillPatchesTravelToNextWaypoint()
    {
        using var module = Read();
        var patched = AllTypes(module).SelectMany(type => type.Methods)
            .Where(method => method.CustomAttributes.Any(attribute =>
                attribute.AttributeType.Name == "HarmonyPatch"
                && attribute.ConstructorArguments.Any(argument => (argument.Value as string) == "TravelToNextWaypoint")))
            .Select(method => method.FullName)
            .ToArray();
        Assert.Equal(new[]
        {
            "System.Void VGEcho.Patches.AutopilotRefineryPatches::TravelToNextWaypoint_AutoRefine_Postfix()",
        }, patched);
    }

    /// <summary>The timing patches now own exactly one native hook, ETA-sync's.
    /// Arrival-snap reaches the game only through the API callback.</summary>
    [Fact]
    public void TheTimingPatchesOwnOnlyTheEtaSyncHook()
    {
        using var module = Read();
        var patched = AllTypes(module).Single(type => type.FullName == "VGEcho.Patches.AutopilotTimingPatches")
            .Methods
            .Where(method => method.CustomAttributes.Any(attribute => attribute.AttributeType.Name == "HarmonyPostfix"
                || attribute.AttributeType.Name == "HarmonyPrefix"
                || attribute.AttributeType.Name == "HarmonyTranspiler"))
            .Select(method => method.Name)
            .ToArray();
        Assert.Equal(new[] { "IdleManager_Update_Postfix" }, patched);
    }

    /// <summary>ETA-sync is a separate feature and keeps its own native patch and
    /// its own backing-field writes; the arrival-snap change must not have moved it.</summary>
    [Fact]
    public void EtaSyncKeepsItsIdleManagerUpdatePatchAndBackingFieldLiterals()
    {
        using var module = Read();
        var timing = AllTypes(module).Single(type => type.FullName == "VGEcho.Patches.AutopilotTimingPatches");
        var update = timing.Methods.Single(method => method.Name == "IdleManager_Update_Postfix");
        Assert.Contains(update.CustomAttributes, attribute => attribute.AttributeType.Name == "HarmonyPostfix");
        Assert.Contains(update.CustomAttributes, attribute => attribute.AttributeType.Name == "HarmonyPatch"
            && attribute.ConstructorArguments.Any(argument => (argument.Value as string) == "Update"));

        var literals = timing.Methods.Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions)
            .Select(instruction => instruction.Operand as string)
            .ToArray();
        Assert.Contains("<updateTimer>k__BackingField", literals);
        Assert.Contains("<updateTimerBase>k__BackingField", literals);
    }

    /// <summary>Consumers must not ship a second copy of the contract; the API
    /// plugin owns the one installed <c>VGModAPI.Abstractions.dll</c>.</summary>
    [Fact]
    public void TheBuildOutputShipsNoCopyOfTheApiAssembly()
    {
        var output = Path.GetDirectoryName(Path.GetFullPath(AssemblyPath))!;
        Assert.False(File.Exists(Path.Combine(output, ApiAssembly + ".dll")),
            "VGEcho's build output must not contain " + ApiAssembly + ".dll (the reference is compile-only).");
    }
}

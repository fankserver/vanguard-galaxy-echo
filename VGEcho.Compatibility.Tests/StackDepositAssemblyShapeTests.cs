using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace VGEcho.Compatibility.Tests;

/// <summary>
/// Metadata/IL checks over the freshly built <c>VGEcho.dll</c>. These are the
/// regression fence for the game 0.8.2.3 signature change: the plugin compiles
/// against a publicized stub that still declares the removed two-argument
/// <c>Inventory.Remove</c>, so "it builds" proves nothing about runtime binding.
///
/// Cecil reads the file; nothing here loads or runs the assembly.
/// </summary>
public class StackDepositAssemblyShapeTests
{
    private const string OpenRemoveDelegateFullName =
        "System.Func`5<Source.Item.Inventory,Behaviour.Item.InventoryItemType,System.Int32,System.Boolean,System.Int32>";

    private static readonly HashSet<Code> ArithmeticOpcodes = new()
    {
        Code.Add, Code.Add_Ovf, Code.Add_Ovf_Un,
        Code.Sub, Code.Sub_Ovf, Code.Sub_Ovf_Un,
        Code.Mul, Code.Mul_Ovf, Code.Mul_Ovf_Un,
        Code.Div, Code.Div_Un,
        Code.Rem, Code.Rem_Un,
    };

    [Fact]
    public void PluginAssembly_ReferencesNoInventoryRemoveMemberAtAll()
    {
        using ModuleDefinition module = CompatibilityAssemblies.ReadModule(CompatibilityAssemblies.PluginAssemblyPath());

        string[] offenders = module.GetMemberReferences()
            .Where(m => m.Name == "Remove"
                        && m.DeclaringType?.FullName == CompatibilityAssemblies.InventoryTypeName)
            .Select(m => m.FullName)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void PluginAssembly_EmitsNoDirectCallToInventoryRemove()
    {
        using ModuleDefinition module = CompatibilityAssemblies.ReadModule(CompatibilityAssemblies.PluginAssemblyPath());

        string[] offenders = module.Types
            .SelectMany(AllMethods)
            .Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions.Select(i => (Method: m, Instruction: i)))
            .Where(x => x.Instruction.OpCode.Code is Code.Call or Code.Callvirt
                        && x.Instruction.Operand is MethodReference callee
                        && callee.Name == "Remove"
                        && callee.DeclaringType?.FullName == CompatibilityAssemblies.InventoryTypeName)
            .Select(x => $"{x.Method.FullName} @ {x.Instruction}")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void StackPatches_CachesTheNativeRemoveAsAFourArgumentOpenDelegate()
    {
        using ModuleDefinition module = CompatibilityAssemblies.ReadModule(CompatibilityAssemblies.PluginAssemblyPath());
        TypeDefinition patches = CompatibilityAssemblies.RequireType(module, CompatibilityAssemblies.StackPatchesTypeName);

        FieldDefinition nativeRemove = Assert.Single(patches.Fields, f => f.Name == "NativeRemove");

        Assert.True(nativeRemove.IsStatic);
        Assert.True(nativeRemove.IsInitOnly);
        Assert.Equal(OpenRemoveDelegateFullName, nativeRemove.FieldType.FullName);
    }

    [Fact]
    public void StackAwareRemove_TakesTheReceiverPlusAllThreeNativeArguments()
    {
        using ModuleDefinition module = CompatibilityAssemblies.ReadModule(CompatibilityAssemblies.PluginAssemblyPath());
        MethodDefinition replacement = StackAwareRemove(module);

        Assert.True(replacement.IsStatic);
        Assert.Equal("System.Int32", replacement.ReturnType.FullName);
        Assert.Equal(
            new[]
            {
                CompatibilityAssemblies.InventoryTypeName,
                CompatibilityAssemblies.InventoryItemTypeName,
                "System.Int32",
                "System.Boolean",
            },
            replacement.Parameters.Select(p => p.ParameterType.FullName).ToArray());
    }

    [Fact]
    public void StackAwareRemove_ForwardsTheNativeFlagUnchangedOnEveryRemovalBranch()
    {
        using ModuleDefinition module = CompatibilityAssemblies.ReadModule(CompatibilityAssemblies.PluginAssemblyPath());
        MethodDefinition replacement = StackAwareRemove(module);

        Instruction[] removals = replacement.Body.Instructions
            .Where(IsOpenRemoveDelegateInvoke)
            .ToArray();

        // One per return path in StackAwareRemove: five guard passthroughs, the
        // unsupported-destination and null-destination passthroughs, and the
        // stack-aware deposit itself.
        Assert.Equal(8, removals.Length);

        string[] notForwarded = removals
            .Where(i => i.Previous?.OpCode.Code != Code.Ldarg_3)
            .Select(i => $"{i} preceded by {i.Previous}")
            .ToArray();

        Assert.Empty(notForwarded);
    }

    [Fact]
    public void StackAwareRemove_NeverRebindsTheNativeFlagArgument()
    {
        using ModuleDefinition module = CompatibilityAssemblies.ReadModule(CompatibilityAssemblies.PluginAssemblyPath());
        MethodDefinition replacement = StackAwareRemove(module);

        // Nothing writes to the skipFavourited parameter, so every load of it is
        // the value the native callsite pushed.
        Assert.DoesNotContain(
            replacement.Body.Instructions,
            i => i.OpCode.Code == Code.Starg || i.OpCode.Code == Code.Starg_S);
    }

    [Fact]
    public void StackAwareRemove_KeepsAmountArithmeticToTheSingleDestinationCapDivision()
    {
        using ModuleDefinition module = CompatibilityAssemblies.ReadModule(CompatibilityAssemblies.PluginAssemblyPath());
        MethodDefinition replacement = StackAwareRemove(module);

        Code[] arithmetic = replacement.Body.Instructions
            .Select(i => i.OpCode.Code)
            .Where(ArithmeticOpcodes.Contains)
            .ToArray();

        // The only arithmetic in the replacement is `dest.GetSpaceAvailable() / item.m3`.
        // Tier percentages and minimums live in ComputeTickAmount; deposit batch
        // sizes stay in vanilla's DropFoundItem prologue.
        Assert.Equal(new[] { Code.Div }, arithmetic);
    }

    [Fact]
    public void DropFoundItemTranspiler_RewritesAtMostOneInstruction()
    {
        using ModuleDefinition module = CompatibilityAssemblies.ReadModule(CompatibilityAssemblies.PluginAssemblyPath());
        TypeDefinition patches = CompatibilityAssemblies.RequireType(module, CompatibilityAssemblies.StackPatchesTypeName);
        MethodDefinition transpiler = Assert.Single(patches.Methods, m => m.Name == "DropFoundItem_Transpiler");

        // A single List<CodeInstruction> indexer store — the one-for-one callsite
        // swap. Any further store would mean the transpiler grew into a general
        // IL rewriter, which this plugin explicitly does not do.
        int stores = transpiler.Body.Instructions.Count(
            i => i.OpCode.Code is Code.Call or Code.Callvirt
                 && i.Operand is MethodReference callee
                 && callee.Name == "set_Item"
                 && callee.DeclaringType.Name == "List`1");

        Assert.Equal(1, stores);
    }

    private static MethodDefinition StackAwareRemove(ModuleDefinition module)
    {
        TypeDefinition patches = CompatibilityAssemblies.RequireType(module, CompatibilityAssemblies.StackPatchesTypeName);
        return Assert.Single(patches.Methods, m => m.Name == "StackAwareRemove");
    }

    private static bool IsOpenRemoveDelegateInvoke(Instruction instruction) =>
        instruction.OpCode.Code is Code.Call or Code.Callvirt
        && instruction.Operand is MethodReference callee
        && callee.Name == "Invoke"
        && callee.DeclaringType.FullName == OpenRemoveDelegateFullName;

    private static IEnumerable<MethodDefinition> AllMethods(TypeDefinition type)
    {
        foreach (MethodDefinition method in type.Methods) yield return method;
        foreach (TypeDefinition nested in type.NestedTypes)
        {
            foreach (MethodDefinition method in AllMethods(nested)) yield return method;
        }
    }
}

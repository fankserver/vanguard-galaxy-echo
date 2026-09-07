using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace VGEcho.Compatibility.Tests;

/// <summary>
/// Compatibility checks against the ORIGINAL installed <c>Assembly-CSharp.dll</c>,
/// read as metadata only — nothing is loaded, executed, copied or published.
/// Requires a local game install, so every fact here is tagged
/// <c>Category=InstalledGame</c> and run by <c>make compat-check-bindings</c>;
/// public CI excludes the category.
/// </summary>
[Trait("Category", "InstalledGame")]
public class InstalledGameRemoveBindingTests
{
    [Fact]
    public void InstalledInventory_DeclaresTheThreeArgumentRemoveAndNotTheLegacyTwoArgumentOne()
    {
        using ModuleDefinition game = CompatibilityAssemblies.ReadModule(CompatibilityAssemblies.GameAssemblyPath());
        TypeDefinition inventory = CompatibilityAssemblies.RequireType(game, CompatibilityAssemblies.InventoryTypeName);

        MethodDefinition removeByType = Assert.Single(inventory.Methods, IsRemoveByItemType);

        Assert.Equal(
            new[] { CompatibilityAssemblies.InventoryItemTypeName, "System.Int32", "System.Boolean" },
            removeByType.Parameters.Select(p => p.ParameterType.FullName).ToArray());
        Assert.Equal("System.Int32", removeByType.ReturnType.FullName);
        Assert.True(removeByType.Parameters[2].IsOptional, "skipFavourited is expected to keep its default value");

        Assert.DoesNotContain(
            inventory.Methods,
            m => m.Name == "Remove"
                 && m.Parameters.Count == 2
                 && m.Parameters[0].ParameterType.FullName == CompatibilityAssemblies.InventoryItemTypeName);
    }

    [Fact]
    public void InstalledDropFoundItem_HasExactlyOneNativeRemoveCallAndPushesTheFlagBeforeIt()
    {
        using ModuleDefinition game = CompatibilityAssemblies.ReadModule(CompatibilityAssemblies.GameAssemblyPath());
        TypeDefinition idleManager = CompatibilityAssemblies.RequireType(game, CompatibilityAssemblies.IdleManagerTypeName);
        MethodDefinition dropFoundItem = Assert.Single(idleManager.Methods, m => m.Name == "DropFoundItem");

        Instruction[] removeCalls = dropFoundItem.Body.Instructions
            .Where(i => i.OpCode.Code is Code.Call or Code.Callvirt
                        && i.Operand is MethodReference callee
                        && callee.Name == "Remove"
                        && callee.DeclaringType.FullName == CompatibilityAssemblies.InventoryTypeName)
            .ToArray();

        // The transpiler rewrites exactly one instruction and bails out otherwise.
        Instruction call = Assert.Single(removeCalls);
        var callee = (MethodReference)call.Operand;

        Assert.Equal(
            new[] { CompatibilityAssemblies.InventoryItemTypeName, "System.Int32", "System.Boolean" },
            callee.Parameters.Select(p => p.ParameterType.FullName).ToArray());

        // DropFoundItem omits the optional argument, so the compiler pushed the
        // default `false`. That is the fourth operand StackAwareRemove consumes,
        // which is why the replacement is a one-for-one instruction swap.
        Assert.Equal(Code.Ldc_I4_0, call.Previous.OpCode.Code);
    }

    [Fact]
    public void StackAwareRemove_MatchesTheInstalledRemoveSignaturePlusTheReceiver()
    {
        using ModuleDefinition game = CompatibilityAssemblies.ReadModule(CompatibilityAssemblies.GameAssemblyPath());
        using ModuleDefinition plugin = CompatibilityAssemblies.ReadModule(CompatibilityAssemblies.PluginAssemblyPath());

        TypeDefinition inventory = CompatibilityAssemblies.RequireType(game, CompatibilityAssemblies.InventoryTypeName);
        MethodDefinition nativeRemove = Assert.Single(inventory.Methods, IsRemoveByItemType);

        TypeDefinition patches = CompatibilityAssemblies.RequireType(plugin, CompatibilityAssemblies.StackPatchesTypeName);
        MethodDefinition replacement = Assert.Single(patches.Methods, m => m.Name == "StackAwareRemove");

        Assert.True(replacement.IsStatic);
        Assert.Equal(nativeRemove.ReturnType.FullName, replacement.ReturnType.FullName);
        Assert.Equal(inventory.FullName, replacement.Parameters[0].ParameterType.FullName);
        Assert.Equal(
            nativeRemove.Parameters.Select(p => p.ParameterType.FullName).ToArray(),
            replacement.Parameters.Skip(1).Select(p => p.ParameterType.FullName).ToArray());
    }

    private static bool IsRemoveByItemType(MethodDefinition method) =>
        method.Name == "Remove"
        && method.Parameters.Count == 3
        && method.Parameters[0].ParameterType.FullName == CompatibilityAssemblies.InventoryItemTypeName;
}

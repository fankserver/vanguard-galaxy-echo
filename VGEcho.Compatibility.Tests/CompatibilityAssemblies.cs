using System;
using System.IO;
using Mono.Cecil;

namespace VGEcho.Compatibility.Tests;

/// <summary>
/// Locates the two assemblies this suite reads as metadata: the freshly built
/// plugin and — for the <c>InstalledGame</c> category only — the original
/// <c>Assembly-CSharp.dll</c> from a local game install.
///
/// Both are opened with Mono.Cecil in read-only mode. Neither is loaded into
/// the test process, so no Unity type ever initialises here.
/// </summary>
internal static class CompatibilityAssemblies
{
    internal const string PluginAssemblyVariable = "VGECHO_ASSEMBLY";
    internal const string GameAssemblyVariable = "VG_GAME_ASSEMBLY";

    internal const string InventoryTypeName = "Source.Item.Inventory";
    internal const string InventoryItemTypeName = "Behaviour.Item.InventoryItemType";
    internal const string IdleManagerTypeName = "Behaviour.Gameplay.IdleManager";
    internal const string StackPatchesTypeName = "VGEcho.Patches.AutopilotStackPatches";

    /// <summary>
    /// Path to the built <c>VGEcho.dll</c>. <c>make compat-test</c> exports
    /// <see cref="PluginAssemblyVariable"/>; when it is unset we walk up from the
    /// test output directory to the repo root and pick the plugin build that
    /// matches this test run's configuration.
    /// </summary>
    internal static string PluginAssemblyPath()
    {
        string? configured = Environment.GetEnvironmentVariable(PluginAssemblyVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!File.Exists(configured))
            {
                throw new FileNotFoundException(
                    $"{PluginAssemblyVariable} points at '{configured}', which does not exist. " +
                    "Build the plugin first (make build).", configured);
            }
            return configured;
        }

        string configuration = ThisConfiguration();
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "VGEcho", "bin", configuration, "netstandard2.1", "VGEcho.dll");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            $"Could not find VGEcho/bin/{configuration}/netstandard2.1/VGEcho.dll above {AppContext.BaseDirectory}. " +
            $"Run 'make compat-test', or set {PluginAssemblyVariable} to the built plugin.");
    }

    /// <summary>
    /// Path to the ORIGINAL installed <c>Assembly-CSharp.dll</c>. Required — the
    /// tests that call this are all tagged <c>Category=InstalledGame</c> and are
    /// only run by <c>make compat-check-bindings</c>, which exports the variable.
    /// Public CI excludes that category.
    /// </summary>
    internal static string GameAssemblyPath()
    {
        string? configured = Environment.GetEnvironmentVariable(GameAssemblyVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{GameAssemblyVariable} is not set. These checks read the original installed " +
                "Assembly-CSharp.dll as metadata; run 'make compat-check-bindings' on a machine " +
                "with the game installed.");
        }

        if (!File.Exists(configured))
        {
            throw new FileNotFoundException(
                $"{GameAssemblyVariable} points at '{configured}', which does not exist.", configured);
        }

        return configured;
    }

    internal static ModuleDefinition ReadModule(string path) =>
        ModuleDefinition.ReadModule(path, new ReaderParameters(ReadingMode.Deferred) { ReadSymbols = false });

    internal static TypeDefinition RequireType(ModuleDefinition module, string fullName) =>
        module.GetType(fullName)
        ?? throw new InvalidOperationException($"Type '{fullName}' not found in {module.FileName}.");

    private static string ThisConfiguration() =>
        AppContext.BaseDirectory.Replace('\\', '/').Contains("/bin/Release/") ? "Release" : "Debug";
}

using System;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using VGEcho.Travel;

namespace VGEcho;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInProcess("VanguardGalaxy.exe")]
// Soft, because arrival-snap is one feature out of eight: a missing API must not
// stop VGEcho from loading. It still orders the API's Awake before ours when it
// IS installed, which is what makes ModApi.Travel readable below. The id is the
// literal VGModAPI publishes as ModApi.PluginId; ArrivalSnapBindingTests pins the
// two together so a rename cannot silently turn this into a dead dependency.
[BepInDependency(ArrivalSnapBinding.ApiPluginId, BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "vgecho";
    public const string PluginName = "Vanguard Galaxy Echo";
    // BepInEx parses PluginVersion through System.Version which rejects SemVer
    // pre-release suffixes, so stick to the plain N.N.N form.
    public const string PluginVersion = "0.7.0";

    internal static Plugin Instance { get; private set; } = null!;
    internal static ManualLogSource Log { get; private set; } = null!;

    internal ConfigEntry<bool> CfgAutopilotTiming = null!;
    internal ConfigEntry<bool> CfgAutopilotEtaSync = null!;
    internal ConfigEntry<bool> CfgAutopilotArrivalSnap = null!;
    internal ConfigEntry<Patches.StackDepositMode> CfgAutopilotStackDepositMode = null!;
    internal ConfigEntry<bool> CfgAutopilotRefineryRoute = null!;
    internal ConfigEntry<int> CfgAutopilotRefineryMaxHops = null!;
    internal ConfigEntry<bool> CfgAutopilotAutoRefine = null!;
    internal ConfigEntry<bool> CfgAutopilotAutoLbrtr = null!;
    internal ConfigEntry<float> CfgAutopilotAutoLbrtrRange = null!;
    internal ConfigEntry<bool> CfgAutopilotAutoSafeCracker = null!;
    internal ConfigEntry<float> CfgAutopilotAutoSafeCrackerRange = null!;

    private Harmony _harmony = null!;

    /// <summary>The VGModAPI travel subscription, or null when arrival-snap is
    /// unavailable. Typed as VGEcho's own interface: no plugin field may name a
    /// <c>VGModAPI.Abstractions</c> type, or loading this class would fail
    /// outright when the API is not installed.</summary>
    private IArrivalSnapObserver? _arrivalSnap;

    private void Awake()
    {
        Instance = this;
        Log = Logger;

        CfgAutopilotTiming = Config.Bind("Autopilot", "TimingEnabled", true,
            "Master enable for autopilot (IdleManager) timing tweaks. When false, both " +
            "ETA-sync and arrival-snap are skipped and the vanilla 12s cycle runs unchanged.");
        CfgAutopilotEtaSync = Config.Bind("Autopilot", "EtaSync", true,
            "While warping, drive the IdleManager cycle timer from the ship's distance-based " +
            "travel progress (remainingDistance / totalDistance) instead of the vanilla 12s " +
            "loop. The Autopilot side-tab's green fill circle becomes a travel-progress " +
            "indicator that completes exactly on drop-out. Requires TimingEnabled.");
        CfgAutopilotArrivalSnap = Config.Bind("Autopilot", "ArrivalSnap", true,
            "When the ship completes its final route, zero the IdleManager cycle timer so the " +
            "next task fires on the following Update tick instead of waiting up to 12s. Covers " +
            "jump-gate transitions where ETA is unavailable. Requires TimingEnabled, and requires " +
            "VGModAPI " + ArrivalSnapBinding.MinimumApiVersion + " or newer with [Travel] Enabled = true: the arrival " +
            "fact comes from that API's verified RouteCompleted observation. Without it this " +
            "toggle does nothing and no direct game hook is installed instead.");
        CfgAutopilotStackDepositMode = Config.Bind("Autopilot", "StackDepositMode", Patches.StackDepositMode.Tiered,
            new ConfigDescription(
                "Controls how each autopilot deposit cycle moves cargo (non-ammo / non-currency " +
                "items only — ammo and currency keep their vanilla per-cycle batches). Cycle " +
                "cadence is unchanged in all modes (still 400/cargoCapacity seconds, still gated " +
                "by ship-progression and the Prompt Engineering skill tree). Per-tick amount is " +
                "capped by destination free space.\n\n" +
                "Mastery is granted automatically by every autopilot tick — no skill points " +
                "required — and is visible in the skill tree UI under the Engineering specialisation.\n\n" +
                "  • Off — pure vanilla. ECHO deposits one unit per tick.\n\n" +
                "  • Tiered (default) — per-tick amount scales with autopilot mastery, mirroring " +
                "vanilla's milestonesMastery tier cadence (every 10 levels). Frames stack-deposit " +
                "as a developing capability consistent with the Prompt Engineering doctrine:\n" +
                "        Mastery  0–9   → 1 unit/tick (vanilla)\n" +
                "        Mastery 10–19  → 25% of stack/tick (min 5)\n" +
                "        Mastery 20–29  → 50% of stack/tick (min 10)\n" +
                "        Mastery 30–39  → 75% of stack/tick (min 25)\n" +
                "        Mastery 40+    → full stack/tick\n\n" +
                "  • Always — full stack from level 0. Pure quality-of-life override that " +
                "ignores the progression curve. Recommended for players who installed the mod " +
                "specifically to skip vanilla cargo drain.\n\n" +
                "The mastery badge tooltip in the skill tree UI shows the current per-tick " +
                "amount and the next tier threshold while Tiered is active."));
        CfgAutopilotRefineryRoute = Config.Bind("Autopilot", "RefineryRoute", false,
            "When the autopilot would fly back to your home station with a cargo hold containing ore, " +
            "instead divert to the nearest friendly dockable station within RefineryMaxHops that has " +
            "a refinery. Saves travel time when mining far from home. Only engages when cargo contains " +
            "at least one ore item AND your home station has no refinery AND no idle mission targets a " +
            "station. Falls back to vanilla home-routing if no refinery station is within range. " +
            "Independent of TimingEnabled.");
        CfgAutopilotRefineryMaxHops = Config.Bind("Autopilot", "RefineryMaxHops", 2,
            new ConfigDescription(
                "Maximum jump-gate hops to search for a refinery station when RefineryRoute is enabled. " +
                "Larger values search further but take longer to travel. Default 2 matches the vanilla " +
                "mission-station search range.",
                new AcceptableValueRange<int>(1, 10)));
        CfgAutopilotAutoRefine = Config.Bind("Autopilot", "AutoRefine", false,
            "When the autopilot arrives at a station that has a refinery, flip the refinery's Auto-Refine " +
            "toggle on. The station will then automatically queue refinement jobs for ore in material " +
            "storage (and the ship's cargo while docked), up to the refinery's max-jobs limit and while " +
            "credits allow. The setting sticks per-station and can still be toggled manually in the " +
            "refinery UI. Independent of TimingEnabled.");
        CfgAutopilotAutoLbrtr = Config.Bind("Autopilot", "AutoLbrtr", false,
            "When ECHO autopilot is engaged and the LB-RTR Bot salvage ability is equipped and " +
            "off cooldown, auto-fire it at the closest wreck within AutoLbrtrRange that still has " +
            "salvage. Effectively turns the activated ability into a triggered one for the duration " +
            "of autopilot — same cooldown, same payload, no manual targeting. Skipped while you're " +
            "mid-manual-cast. Beyond VGEcho's default 'fix UI/timing' scope, so opt-in.");
        CfgAutopilotAutoLbrtrRange = Config.Bind("Autopilot", "AutoLbrtrRange", 80f,
            new ConfigDescription(
                "Maximum distance (world units) from the player ship to scan for wrecks when " +
                "AutoLbrtr is enabled. Tune up if the drone reliably reaches further wrecks " +
                "before its duration expires; down if it keeps falling short.",
                new AcceptableValueRange<float>(20f, 500f)));
        CfgAutopilotAutoSafeCracker = Config.Bind("Autopilot", "AutoSafeCracker", false,
            "When ECHO autopilot is engaged and the Crackshot Drone mining ability is " +
            "equipped and off cooldown, auto-fire it at the closest asteroid within " +
            "AutoSafeCrackerRange that still has surface ore. Effectively turns the " +
            "activated ability into a triggered one for the duration of autopilot -- same " +
            "cooldown, same payload, no manual targeting. Skipped while you're mid-manual-cast. " +
            "Beyond VGEcho's default 'fix UI/timing' scope, so opt-in.");
        CfgAutopilotAutoSafeCrackerRange = Config.Bind("Autopilot", "AutoSafeCrackerRange", 80f,
            new ConfigDescription(
                "Maximum distance (world units) from the player ship to scan for asteroids " +
                "when AutoSafeCracker is enabled. Tune up if the drone reliably reaches " +
                "further asteroids before its duration expires; down if it keeps falling short.",
                new AcceptableValueRange<float>(20f, 500f)));

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll(typeof(Patches.AutopilotTimingPatches));
        _harmony.PatchAll(typeof(Patches.AutopilotStackPatches));
        _harmony.PatchAll(typeof(Patches.AutopilotRefineryPatches));
        _harmony.PatchAll(typeof(Patches.AutopilotUIPatches));
        _harmony.PatchAll(typeof(Patches.AutopilotLbrtrPatches));
        _harmony.PatchAll(typeof(Patches.AutopilotSafeCrackerPatches));
        BindArrivalSnap();
        Log.LogInfo($"{PluginName} v{PluginVersion} loaded ({_harmony.GetPatchedMethods().Count()} patches)");
    }

    /// <summary>
    /// Binds arrival-snap to VGModAPI's travel observations, or explains why it
    /// stays off. Every API type is reached through
    /// <see cref="TravelArrivalBridge"/>'s no-inline methods inside this
    /// <c>try</c>, so a missing or broken API assembly costs exactly this one
    /// feature and never the plugin's startup.
    /// </summary>
    private void BindArrivalSnap()
    {
        var apiVersion = InstalledApiVersion();
        var admission = ArrivalSnapAdmission.ApiAbsent;
        try
        {
            if (apiVersion != null)
            {
                admission = TravelArrivalBridge.Admit(apiVersion);
                if (admission == ArrivalSnapAdmission.Admitted)
                {
                    _arrivalSnap = TravelArrivalBridge.Subscribe(
                        Patches.AutopilotTimingPatches.ReadArrivalSnapGates,
                        Patches.AutopilotTimingPatches.ApplyArrivalSnap,
                        message => Log.LogDebug(message),
                        message => Log.LogError(message));
                }
            }
        }
        catch (Exception failure)
        {
            _arrivalSnap = null;
            Log.LogError("VGModAPI travel binding failed, so autopilot arrival-snap is disabled. ETA-sync " +
                "and every other VGEcho feature are unaffected, and no direct TravelManager hook is " +
                "installed as a fallback: " + failure.GetType().Name + ": " + failure.Message);
            return;
        }

        if (_arrivalSnap == null) Log.LogWarning(ArrivalSnapBinding.Explain(admission));
        else Log.LogInfo("Autopilot arrival-snap bound to VGModAPI " + apiVersion + " travel events.");
    }

    /// <summary>Version of the installed VGModAPI plugin, or null when it is not
    /// loaded. Reading <see cref="Chainloader"/> touches no API type, so the
    /// absent case never JITs anything that could fail to resolve.</summary>
    private static Version? InstalledApiVersion()
    {
        try
        {
            return Chainloader.PluginInfos != null
                   && Chainloader.PluginInfos.TryGetValue(ArrivalSnapBinding.ApiPluginId, out var api)
                ? api.Metadata.Version
                : null;
        }
        catch
        {
            // Chainloader is not initialized outside a BepInEx process.
            return null;
        }
    }

    private void OnDestroy()
    {
        try
        {
            _arrivalSnap?.Dispose();
        }
        catch (Exception failure)
        {
            Log?.LogError("Disposing the VGModAPI travel subscription failed: "
                + failure.GetType().Name + ": " + failure.Message);
        }
        _arrivalSnap = null;
        _harmony?.UnpatchSelf();
    }
}

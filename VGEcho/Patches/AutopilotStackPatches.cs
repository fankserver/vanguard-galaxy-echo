using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Behaviour.Crew;
using Behaviour.Gameplay;
using Behaviour.Item;
using Behaviour.UI;
using Behaviour.UI.Tooltip;
using HarmonyLib;
using Source.Galaxy;
using Source.Galaxy.POI;
using Source.Item;
using Source.Player;
using Source.Util;
using UnityEngine;

namespace VGEcho.Patches;

/// <summary>
/// How the per-tick amount of <see cref="AutopilotStackPatches"/> scales with
/// the autopilot (Prompt Engineering) mastery level. See the description on
/// <see cref="Plugin.CfgAutopilotStackDepositMode"/>.
/// </summary>
public enum StackDepositMode
{
    Off,
    Tiered,
    Always,
}

/// <summary>
/// Stack-aware autopilot deposits with a mastery-driven progression curve.
/// Vanilla's autopilot deposit (<c>IdleManager.DropFoundItem</c>) transfers a
/// single unit per cycle for general cargo, with hardcoded batches for ammo
/// (max(20, magSize)) and currency (20). At 400/cargoCapacity seconds per cycle,
/// a 200-unit hold takes ~6.6 minutes to drain regardless of cargo diversity.
///
/// We transpile the deposit-amount argument of the single
/// <c>cargo.Remove(InventoryItemType, int, bool)</c> call inside <c>DropFoundItem</c>
/// so each tick moves more of the selected non-ammo / non-currency item type
/// at once. How much depends on <see cref="StackDepositMode"/> and the player's
/// autopilot mastery level — vanilla's autopilot tree already accumulates
/// mastery on every <c>FindActivity</c> tick, so the curve participates in an
/// existing progression hook instead of bypassing it.
///
/// Tier table (mode = Tiered):
/// <code>
///   Mastery  0–9   → 1 unit/tick (vanilla)
///   Mastery 10–19  → 25% of stack/tick (min 5)
///   Mastery 20–29  → 50% of stack/tick (min 10)
///   Mastery 30–39  → 75% of stack/tick (min 25)
///   Mastery 40+    → full stack/tick
/// </code>
/// The tier breakpoints mirror vanilla's <c>milestonesMastery</c> cadence
/// (every 10 levels). <c>Off</c> defers to vanilla unconditionally;
/// <c>Always</c> jumps to full-stack at level 0 (pure QoL override).
///
/// Per-tick amount is always capped by the destination's free m³.
///
/// What this <i>does not</i> change:
///   • Cycle cadence (still 400/cargoCapacity, still skill-tree gated by
///     <see cref="SkilltreeNode.promptEngineeringEnhancedActivityDelay"/> etc).
///   • Selection logic (vanilla picks which item to deposit; we just deposit
///     more of it per pick).
///   • Ammo fetch (already atomic per ammo type via <c>FetchItem</c>).
///   • Auto-sell / material-storage-full path (vanilla's <c>SellItemFromMaterials</c>
///     already moves a single computed amount per call).
///
/// We also patch <see cref="MasteryBadge.AddTooltipCustomContent"/> so the
/// autopilot tree's mastery badge tooltip surfaces the current per-tick amount
/// and the next-tier threshold while Tiered is active. Without that the
/// progression curve would be invisible in-game.
/// </summary>
[HarmonyPatch]
internal static class AutopilotStackPatches
{
    // Resolved once at type init so we can fail loudly at startup rather than
    // silently no-op the transpiler if the removal overload ever gets renamed
    // or reshaped. Game 0.8.2.3 declares
    // Remove(InventoryItemType, int, bool skipFavourited = false); DropFoundItem
    // omits the optional argument, so the compiled callsite still pushes the
    // default `false` and the transpiler has to match the three-argument form.
    private static readonly MethodInfo InventoryRemoveByType =
        NativeInventoryRemoveBinding.ResolveRemove(typeof(Inventory), typeof(InventoryItemType));

    // Cached open-instance delegate over the same MethodInfo. The committed
    // publicized stub still declares the old two-argument Remove, so calling it
    // directly from here would emit a member reference the live game no longer
    // has (MissingMethodException on every deposit). Every removal below —
    // vanilla-passthrough branches included — goes through this delegate and
    // forwards the native skipFavourited flag unchanged.
    // The item parameter is nullable because one branch deliberately hands a
    // null item straight to vanilla rather than deciding what null should mean.
    private static readonly Func<Inventory, InventoryItemType?, int, bool, int> NativeRemove =
        NativeInventoryRemoveBinding.CreateOpenRemoveDelegate<Inventory, InventoryItemType?>(InventoryRemoveByType);

    private static readonly MethodInfo StackAwareRemoveImpl =
        AccessTools.Method(typeof(AutopilotStackPatches), nameof(StackAwareRemove))
        ?? throw new InvalidOperationException("[autopilot-stack] StackAwareRemove helper not found");

    // Lazy reference to the autopilot tree. Skilltree.Get(name) is a registry
    // lookup that may not be populated at our static-init time; resolve on
    // first call. The tree name comes from
    // SkillTreeData.GetSpecializationTreeName(CommanderSpecialization.Engineering).
    private const string AutopilotTreeName = "PromptEngineering";
    private static Skilltree? _autopilotTree;

    private static Skilltree? AutopilotTree
    {
        get
        {
            if (_autopilotTree == null)
            {
                _autopilotTree = Skilltree.Get(AutopilotTreeName);
            }
            return _autopilotTree;
        }
    }

    private static int GetAutopilotMasteryLevel()
    {
        var tree = AutopilotTree;
        return tree != null ? tree.GetMasteryLevel() : 0;
    }

    /// <summary>
    /// Tier breakpoints for <see cref="StackDepositMode.Tiered"/>. Each tier
    /// is (minMastery, percent-of-stack, minimum-units). The first tier whose
    /// minMastery threshold the player meets wins; tier 0 (vanilla) is the
    /// implicit fallback when no entry matches.
    /// </summary>
    private static readonly (int Mastery, int Percent, int MinUnits)[] Tiers =
    {
        (40, 100, int.MaxValue), // full stack
        (30,  75, 25),
        (20,  50, 10),
        (10,  25,  5),
    };

    /// <summary>
    /// Compute the per-tick deposit amount for a given stack size, mastery
    /// level, and mode. Vanilla amount is the floor — we never deposit fewer
    /// units than vanilla would have. Result is uncapped by destination m³;
    /// the caller is responsible for that cap.
    /// </summary>
    internal static int ComputeTickAmount(int stackCount, int vanillaAmount, int masteryLevel, StackDepositMode mode)
    {
        if (stackCount <= vanillaAmount) return vanillaAmount;

        switch (mode)
        {
            case StackDepositMode.Off:
                return vanillaAmount;

            case StackDepositMode.Always:
                return stackCount;

            case StackDepositMode.Tiered:
                for (int i = 0; i < Tiers.Length; i++)
                {
                    int threshold = Tiers[i].Mastery;
                    int percent = Tiers[i].Percent;
                    int minUnits = Tiers[i].MinUnits;
                    if (masteryLevel >= threshold)
                    {
                        if (percent >= 100) return stackCount;
                        int byPercent = Math.Max(stackCount * percent / 100, 1);
                        int withMin = Math.Min(stackCount, Math.Max(byPercent, minUnits));
                        return Math.Max(withMin, vanillaAmount);
                    }
                }
                return vanillaAmount;

            default:
                return vanillaAmount;
        }
    }

    /// <summary>
    /// Human-readable description of the active tier for tooltip display.
    /// Returned tuple: (currentLabel, nextLabel, nextLevel). nextLevel is 0
    /// when there is no further tier to unlock (already at cap or in a mode
    /// without progression).
    /// </summary>
    private static (string Current, string? Next, int NextLevel) DescribeTier(int masteryLevel, StackDepositMode mode)
    {
        switch (mode)
        {
            case StackDepositMode.Off:
                return ("disabled (vanilla 1 unit/tick)", null, 0);

            case StackDepositMode.Always:
                return ("full stack/tick (override)", null, 0);

            case StackDepositMode.Tiered:
                // Pick the highest tier the player has reached.
                int currentTierIdx = -1;
                for (int i = 0; i < Tiers.Length; i++)
                {
                    if (masteryLevel >= Tiers[i].Mastery)
                    {
                        currentTierIdx = i;
                        break;
                    }
                }

                string current = currentTierIdx < 0
                    ? "1 unit/tick (vanilla)"
                    : DescribeTierEntry(Tiers[currentTierIdx].Percent, Tiers[currentTierIdx].MinUnits);

                // Next tier is the entry immediately above the current one in
                // the table. Tiers is sorted by descending Mastery, so the
                // "next" tier is at index currentTierIdx - 1; or, if not yet
                // in any tier, the last entry (lowest Mastery threshold).
                if (currentTierIdx == 0)
                {
                    return (current, null, 0); // already at cap
                }

                int nextTierIdx = currentTierIdx < 0 ? Tiers.Length - 1 : currentTierIdx - 1;
                int nextPercent = Tiers[nextTierIdx].Percent;
                int nextMinUnits = Tiers[nextTierIdx].MinUnits;
                int nextMastery = Tiers[nextTierIdx].Mastery;
                return (current, DescribeTierEntry(nextPercent, nextMinUnits), nextMastery);

            default:
                return ("unknown", null, 0);
        }
    }

    private static string DescribeTierEntry(int percent, int minUnits)
    {
        if (percent >= 100) return "full stack/tick";
        return $"{percent}% of stack/tick (min {minUnits})";
    }

    /// <summary>
    /// Drop-in replacement for the <c>cargo.Remove(item, vanillaAmount, false)</c>
    /// call inside <c>IdleManager.DropFoundItem</c>. Consumes the same operands
    /// the native callsite already pushes ([cargo, item, amount, skipFavourited])
    /// and returns the same type (int actually removed), so the transpiler can
    /// swap a single instruction without touching the stack shape.
    ///
    /// <paramref name="skipFavourited"/> is the native flag read straight off the
    /// stack; it is forwarded unchanged to the real <c>Remove</c> on every branch
    /// so favourite-stack protection keeps behaving exactly as vanilla decided.
    ///
    /// For ammo and currency, defers to vanilla — those categories have
    /// intentional per-cycle batch sizes (20/mag-size, 20). For everything
    /// else, deposit the tier-driven amount capped by destination free space.
    /// </summary>
    internal static int StackAwareRemove(Inventory cargo, InventoryItemType item, int vanillaAmount, bool skipFavourited)
    {
        var mode = Plugin.Instance.CfgAutopilotStackDepositMode.Value;
        if (mode == StackDepositMode.Off)
        {
            return NativeRemove(cargo, item, vanillaAmount, skipFavourited);
        }

        // Only act on the autopilot path. DropFoundItem runs inside the
        // FindActivity deposit cycle (driven by IdleManager.Update, gated on
        // autoPlay) — but belt-and-braces in case a future game patch routes a
        // manual deposit through the same callsite.
        var player = GamePlayer.current;
        if (player == null || !player.autoPlay)
        {
            return NativeRemove(cargo, item, vanillaAmount, skipFavourited);
        }

        if (item == null)
        {
            return NativeRemove(cargo, item, vanillaAmount, skipFavourited);
        }

        // Preserve vanilla cadence for ammo and currency. Their explicit
        // 20/mag-size and 20 batches already give them a fast-feeling drain
        // (a full 100-round ammo stack clears in 5 cycles, currency in 1).
        // Stack-aware override would only help marginally and risks breaking
        // assumptions baked into FindActivityForEquipment / GetAmmoTypesRequired.
        if (item.itemCategory == ItemCategory.Ammo || item.itemCategory == ItemCategory.Currency)
        {
            return NativeRemove(cargo, item, vanillaAmount, skipFavourited);
        }

        int stackCount = cargo.GetCount(item);
        int masteryLevel = mode == StackDepositMode.Always ? int.MaxValue : GetAutopilotMasteryLevel();
        int tickAmount = ComputeTickAmount(stackCount, vanillaAmount, masteryLevel, mode);

        if (tickAmount <= vanillaAmount)
        {
            return NativeRemove(cargo, item, vanillaAmount, skipFavourited);
        }

        // Determine destination the same way vanilla's FindActivity does. We
        // can't read the local from IL cheaply, but the rule is mechanical:
        // CanGoInMaterials → station's materialStorage; CanGoInArmory →
        // player's globalInventory. Vanilla's IsFull(item.m3) guard at the
        // selection step has already verified at least one unit fits, so
        // we're guaranteed a positive cap below.
        Inventory dest;
        if (item.CanGoInMaterials() && MapPointOfInterest.current is SpaceStation ss)
        {
            dest = ss.materialStorage;
        }
        else if (item.CanGoInArmory())
        {
            dest = GamePlayer.current.globalInventory;
        }
        else
        {
            return NativeRemove(cargo, item, vanillaAmount, skipFavourited);
        }

        if (dest == null)
        {
            return NativeRemove(cargo, item, vanillaAmount, skipFavourited);
        }

        // Cap by free m³ in the destination. item.m3 is per-unit volume;
        // floor division gives us the largest count that won't overflow.
        int safeByM3;
        if (item.m3 > 0f)
        {
            safeByM3 = Mathf.FloorToInt(dest.GetSpaceAvailable() / item.m3);
        }
        else
        {
            safeByM3 = tickAmount;
        }

        // Vanilla's IsFull guard means safeByM3 ≥ 1 here, but defensive max
        // covers the corner case where m3 was zero or rounding bit us.
        int amount = Math.Min(tickAmount, Math.Max(safeByM3, 1));

        Plugin.Log.LogDebug(
            $"[autopilot-stack] depositing {amount}× {item.displayName} " +
            $"(stack={stackCount}, m3-cap={safeByM3}, mastery={masteryLevel}, mode={mode})");

        return NativeRemove(cargo, item, amount, skipFavourited);
    }

    /// <summary>
    /// Replaces the single <c>callvirt Inventory.Remove(InventoryItemType, int, bool)</c>
    /// inside <c>IdleManager.DropFoundItem</c> with a call to
    /// <see cref="StackAwareRemove"/>. The helper takes the same four operands
    /// from the stack ([cargo, item, amount, skipFavourited]) and returns the
    /// same type (int), so it's a one-instruction swap with no stack juggling.
    ///
    /// We expect exactly one match. If a future game patch adds another
    /// <c>cargo.Remove(InventoryItemType, int, bool)</c> callsite into DropFoundItem,
    /// we log a warning and skip rather than blindly rewriting both — the user
    /// can disable <c>StackDeposit</c> in config until the mod is updated.
    /// </summary>
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(IdleManager), "DropFoundItem")]
    private static IEnumerable<CodeInstruction> DropFoundItem_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var list = new List<CodeInstruction>(instructions);
        int matches = 0;
        int firstMatchIndex = -1;

        for (int i = 0; i < list.Count; i++)
        {
            var ins = list[i];
            if ((ins.opcode == OpCodes.Callvirt || ins.opcode == OpCodes.Call)
                && ins.operand is MethodInfo mi
                && mi == InventoryRemoveByType)
            {
                matches++;
                if (firstMatchIndex < 0) firstMatchIndex = i;
            }
        }

        if (matches == 0)
        {
            Plugin.Log.LogWarning(
                "[autopilot-stack] DropFoundItem transpiler: no cargo.Remove(InventoryItemType, int, bool) " +
                "callsite found — stack-deposit will be inactive. Game version may have changed.");
            return list;
        }

        if (matches > 1)
        {
            Plugin.Log.LogWarning(
                $"[autopilot-stack] DropFoundItem transpiler: expected 1 cargo.Remove callsite, " +
                $"found {matches}. Skipping rewrite to avoid corrupting unrelated calls — " +
                "stack-deposit will be inactive.");
            return list;
        }

        list[firstMatchIndex] = new CodeInstruction(OpCodes.Call, StackAwareRemoveImpl);
        Plugin.Log.LogInfo("[autopilot-stack] DropFoundItem transpiler applied (1 callsite rewritten)");
        return list;
    }

    /// <summary>
    /// Postfix on <see cref="MasteryBadge.AddTooltipCustomContent"/>. Vanilla
    /// builds the mastery tooltip per-tree (XP progress, current level, the
    /// passive-bonus list, and a tree-specific extra line for Industrial /
    /// Engineering / Economy). When the badge belongs to the autopilot
    /// (Prompt Engineering) tree we append one line under the existing
    /// "Bonus" header describing the current per-tick deposit amount, in
    /// the same greenish style vanilla uses for its own bonus lines.
    ///
    /// We don't add a separator or our own header because the tooltip is
    /// space-constrained and vanilla's "Bonus" heading already frames the
    /// list this line belongs to. The pixel font used in tooltips also
    /// lacks the em-dash glyph, so plain ASCII characters only.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(MasteryBadge), nameof(MasteryBadge.AddTooltipCustomContent))]
    private static void MasteryBadge_AddTooltipCustomContent_Postfix(MasteryBadge __instance, UITooltip tooltip)
    {
        try
        {
            var tree = __instance.skillTree;
            if (tree == null) return;
            var autopilot = AutopilotTree;
            if (autopilot == null || tree != autopilot) return;

            var mode = Plugin.Instance.CfgAutopilotStackDepositMode.Value;
            int masteryLevel = tree.GetMasteryLevel();
            var (current, _, _) = DescribeTier(masteryLevel, mode);

            tooltip.AddTextLine("Stack deposit: " + current).Text.color = ColorHelper.greenish;
        }
        catch (Exception e)
        {
            // The mastery tooltip is shown frequently; a throwing patch would
            // spam errors and visibly break the vanilla tooltip. Swallow and
            // log once per tooltip open.
            Plugin.Log.LogError($"[autopilot-stack] mastery tooltip postfix failed: {e}");
        }
    }
}

using System.Collections.Generic;
using System.Reflection;
using Behaviour.Ability;
using Behaviour.Ability.Payload;
using Behaviour.Gameplay;
using Behaviour.Salvage;
using Behaviour.UI.HUD;
using Behaviour.Weapons;
using HarmonyLib;
using Source.Player;
using UnityEngine;

namespace VGEcho.Patches;

/// <summary>
/// Auto-fires the <b>LB-RTR Bot</b> salvage ability (<see cref="SalvageCargoLootboxBot"/>)
/// at the closest in-range wreck while ECHO autopilot is engaged. Postfix on
/// <see cref="Behaviour.Managers.IdleManager"/>'s <c>Update</c>, with the actual scan
/// throttled to twice per second and skipped entirely whenever the ability isn't ready
/// or the player is mid-manual-target. Gated behind the opt-in
/// <c>[Autopilot] AutoLbrtr</c> config flag — beyond the default "fix UI/timing" scope
/// of VGEcho, but still ECHO-themed because it only fires while autopilot is running.
/// </summary>
[HarmonyPatch]
internal static class AutopilotLbrtrPatches
{
    private const float ScanInterval = 0.5f;
    private static float _nextScanTime;

    // AbilityHud.availableAbilities is public in the publicized stub VGEcho compiles
    // against, but the shipping game DLL keeps it non-public — so a direct field access
    // throws FieldAccessException at runtime. Read it through reflection instead.
    private static readonly AccessTools.FieldRef<AbilityHud, HashSet<ActivatedAbility>> AvailableAbilitiesRef =
        AccessTools.FieldRefAccess<AbilityHud, HashSet<ActivatedAbility>>("availableAbilities");

    // Same publicized-stub vs. shipping-DLL story as availableAbilities — call through
    // reflection so Mono's runtime visibility check is satisfied.
    private static readonly MethodInfo HasSalvageMethod =
        AccessTools.Method(typeof(SalvageContainer), nameof(SalvageContainer.HasSalvage), new[] { typeof(TargetLayer) });
    private static readonly object[] HasSalvageBothArgs = { TargetLayer.Both };

    [HarmonyPostfix]
    [HarmonyPatch(typeof(IdleManager), "Update")]
    private static void IdleManager_Update_Postfix()
    {
        if (!Plugin.Instance.CfgAutopilotAutoLbrtr.Value) return;

        var player = GamePlayer.current;
        if (player == null || !player.autoPlay) return;

        // Skip while the user is in the middle of a manual targeting cast — they're
        // about to fire something themselves, don't race them.
        if (ActivatedAbility.targetingActive) return;

        float now = Time.time;
        // Self-healing throttle: if _nextScanTime survived a scene reload (Time.time
        // reset but the static field didn't), reset it so scanning resumes immediately
        // instead of waiting for real-time to catch up.
        if (_nextScanTime > now + ScanInterval) _nextScanTime = 0f;
        if (now < _nextScanTime) return;
        _nextScanTime = now + ScanInterval;

        var hud = AbilityHud.instance;
        if (hud == null) return;
        var available = AvailableAbilitiesRef(hud);
        if (available == null) return;

        // Locate the LB-RTR ability without relying on display-name strings: it's the
        // ActivatedAbility whose payload prefab carries a SalvageCargoLootboxBot
        // component. Bail if not equipped, or still on cooldown.
        ActivatedAbility lbrtr = null!;
        foreach (var a in available)
        {
            if (a == null || a.payload == null) continue;
            if (a.payload.GetComponent<SalvageCargoLootboxBot>() == null) continue;
            lbrtr = a;
            break;
        }
        if (lbrtr == null || !lbrtr.isReady) return;

        var gm = GameplayManager.Instance;
        var ship = gm != null ? gm.spaceShip : null;
        if (ship == null) return;

        Vector2 origin = ship.transform.position;
        float maxRange = Plugin.Instance.CfgAutopilotAutoLbrtrRange.Value;
        float maxRangeSqr = maxRange * maxRange;

        SalvageContainer closest = null!;
        float closestSqr = float.PositiveInfinity;
        var wrecks = Object.FindObjectsByType<SalvageContainer>(FindObjectsSortMode.None);
        foreach (var wreck in wrecks)
        {
            if (wreck == null || !wreck.isActiveAndEnabled) continue;
            // Both surface and core eligible — LB-RTR pulls cargo regardless of which
            // health pool still has loot pixels attached.
            if (!(bool)HasSalvageMethod.Invoke(wreck, HasSalvageBothArgs)) continue;

            Vector2 pos = wreck.transform.position;
            float dSqr = ((Vector2)(pos - origin)).sqrMagnitude;
            if (dSqr > maxRangeSqr) continue;
            if (dSqr >= closestSqr) continue;
            closest = wreck;
            closestSqr = dSqr;
        }

        if (closest == null) return;

        Plugin.Log.LogDebug(
            $"[autopilot-lbrtr] firing LB-RTR at {closest.displayName} " +
            $"(distance={Mathf.Sqrt(closestSqr):F1}u)");
        lbrtr.TriggerPayload(closest.gameObject);
    }
}

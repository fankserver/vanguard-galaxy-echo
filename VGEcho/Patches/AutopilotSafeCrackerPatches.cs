using System.Collections.Generic;
using Behaviour.Ability;
using Behaviour.Ability.Payload;
using Behaviour.Gameplay;
using Behaviour.Mining;
using Behaviour.UI.HUD;
using HarmonyLib;
using Source.Player;
using UnityEngine;

namespace VGEcho.Patches;

/// <summary>
/// Auto-fires the <b>Crackshot Drone</b> mining ability (<see cref="CrackshotDrone"/>)
/// at the closest in-range asteroid that still has surface ore while ECHO autopilot is
/// engaged. Postfix on <see cref="Behaviour.Managers.IdleManager"/>'s <c>Update</c>,
/// with the actual scan throttled to twice per second and skipped entirely whenever the
/// ability isn't ready or the player is mid-manual-target. Gated behind the opt-in
/// <c>[Autopilot] AutoSafeCracker</c> config flag — beyond the default "fix UI/timing"
/// scope of VGEcho, but follows the same ECHO-themed pattern as the LB-RTR auto-target.
/// </summary>
[HarmonyPatch]
internal static class AutopilotSafeCrackerPatches
{
    private const float ScanInterval = 0.5f;
    private static float _nextScanTime;

    // AbilityHud.availableAbilities is public in the publicized stub VGEcho compiles
    // against, but the shipping game DLL keeps it non-public — so a direct field access
    // throws FieldAccessException at runtime. Read it through reflection instead.
    private static readonly AccessTools.FieldRef<AbilityHud, HashSet<ActivatedAbility>> AvailableAbilitiesRef =
        AccessTools.FieldRefAccess<AbilityHud, HashSet<ActivatedAbility>>("availableAbilities");

    [HarmonyPostfix]
    [HarmonyPatch(typeof(IdleManager), "Update")]
    private static void IdleManager_Update_Postfix()
    {
        if (!Plugin.Instance.CfgAutopilotAutoSafeCracker.Value) return;

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

        // Locate the Crackshot Drone ability without relying on display-name strings:
        // it's the ActivatedAbility whose payload prefab carries a CrackshotDrone
        // component. Bail if not equipped, or still on cooldown.
        ActivatedAbility crackshot = null!;
        foreach (var a in available)
        {
            if (a == null || a.payload == null) continue;
            if (a.payload.GetComponent<CrackshotDrone>() == null) continue;
            crackshot = a;
            break;
        }
        if (crackshot == null || !crackshot.isReady) return;

        var gm = GameplayManager.Instance;
        var ship = gm != null ? gm.spaceShip : null;
        if (ship == null) return;

        Vector2 origin = ship.transform.position;
        float maxRange = Plugin.Instance.CfgAutopilotAutoSafeCrackerRange.Value;
        float maxRangeSqr = maxRange * maxRange;

        Asteroid closest = null!;
        float closestSqr = float.PositiveInfinity;
        var asteroids = Object.FindObjectsByType<Asteroid>(FindObjectsSortMode.None);
        foreach (var asteroid in asteroids)
        {
            if (asteroid == null || !asteroid.isActiveAndEnabled) continue;
            // Only target asteroids that still have surface ore to mine — the
            // Crackshot Drone latches onto surface and applies damage over time.
            if (!asteroid.hasSurfaceOre) continue;

            Vector2 pos = asteroid.transform.position;
            float dSqr = ((Vector2)(pos - origin)).sqrMagnitude;
            if (dSqr > maxRangeSqr) continue;
            if (dSqr >= closestSqr) continue;
            closest = asteroid;
            closestSqr = dSqr;
        }

        if (closest == null) return;

        Plugin.Log.LogDebug(
            $"[autopilot-crackshot] firing Crackshot Drone at {closest.targetName} " +
            $"(distance={Mathf.Sqrt(closestSqr):F1}u)");
        crackshot.TriggerPayload(closest.gameObject);
    }
}

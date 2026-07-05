# Vanguard Galaxy Echo (VGEcho)

A BepInEx plugin for [Vanguard Galaxy](https://store.steampowered.com/app/3471800/) that enhances ECHO — the in-game autopilot AI. Surgical changes to where vanilla's per-tick autopilot loop creates avoidable friction, without bulldozing the Prompt Engineering skill tree's intentional cadence costs.

- **ETA-sync** — while the ship is warping, the Autopilot side-tab's green progress circle tracks live distance-based travel progress instead of the vanilla 12 s loop. Completes exactly on drop-out.
- **Arrival-snap** — on reaching the final waypoint, the next autonomous action fires on the following frame instead of after a 0–12 s residual wait.
- **Stack deposit** — each deposit cycle moves the full stack of an item type instead of one unit. Cycle cadence is unchanged (still `400/cargoCapacity` seconds, still gated by ship-progression and the Prompt Engineering skill tree), so a diverse 200-unit hold drains in *one tick per item type* instead of one per unit. Ammo and currency keep their vanilla per-cycle batches.
- **Refinery routing** *(opt-in)* — when the autopilot would fly home with ore in cargo and home has no refinery, divert to the nearest friendly station with one. Saves the round-trip when mining far from base.
- **Auto-refine on arrival** *(opt-in)* — when the autopilot docks at a station with a refinery, flip that refinery's Auto-Refine toggle on so pending ore refines passively while you're there.
- **Auto LB-RTR** *(opt-in)* — while autopilot is engaged, auto-fire the LB-RTR Bot salvage ability at the closest in-range wreck whenever it's off cooldown. Same cooldown, same payload, no manual targeting — effectively turns the activated ability into a triggered one for the duration of autopilot.
- **Auto Crackshot Drone** *(opt-in)* — while autopilot is engaged, auto-fire the Crackshot Drone mining ability at the closest in-range asteroid with surface ore. Same cooldown, same payload, no manual targeting — effectively turns the mining activated ability into a triggered one for the duration of autopilot.

ETA-sync, Arrival-snap, and Stack-deposit don't change *what* ECHO decides — they fix UI lies, residual waits, and a per-tick architecture artifact respectively. The opt-in toggles change routing, station behavior, or ability casting; they default off so existing installs stay on vanilla decisions.

## Install

1. **Install BepInEx 5.x** — grab `BepInEx_win_x64_5.4.x.zip` from the [BepInEx releases](https://github.com/BepInEx/BepInEx/releases) and unzip it into your Vanguard Galaxy install folder (next to `VanguardGalaxy.exe`).
2. **Launch the game once** so BepInEx creates its `BepInEx/plugins/` and `BepInEx/config/` subfolders, then close the game.
3. **Download the VGEcho release** zip from [Releases](https://github.com/fank/vanguard-galaxy-echo/releases) (or Nexus Mods, once published).
4. **Unzip** into `BepInEx/plugins/`. The zip contains a single `VGEcho/` folder that drops in cleanly:
   ```
   VanguardGalaxy/BepInEx/plugins/
     VGEcho/
       VGEcho.dll
       README.md
   ```
5. **Launch the game.** Open the BepInEx console — you should see a load line ending with the number of Harmony patches applied, e.g.:
   ```
   [Info :Vanguard Galaxy Echo] Vanguard Galaxy Echo v0.4.0 loaded (7 patches)
   ```

## Uninstall

Delete the `BepInEx/plugins/VGEcho/` folder. Optionally also delete `BepInEx/config/vgecho.cfg` to reset saved settings.

## Config

BepInEx writes the config to `BepInEx/config/vgecho.cfg` on first launch. All toggles live under `[Autopilot]`:

| Key                | Default | Purpose                                                                                                                                                                                       |
| ------------------ | ------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `TimingEnabled`    | `true`  | Master toggle for `EtaSync` and `ArrivalSnap`. When `false`, both are skipped and the vanilla 12 s cycle runs unchanged. Does not affect `StackDeposit`.                                      |
| `EtaSync`          | `true`  | While warping, drive the cycle from distance-based travel progress. Fill circle completes exactly on drop-out.                                                                                |
| `ArrivalSnap`      | `true`  | On final-waypoint arrival, zero the cycle so the next autonomous action fires immediately.                                                                                                    |
| `StackDepositMode` | `Tiered`  | Controls how each autopilot deposit cycle moves cargo. `Off` = vanilla 1 unit/tick. `Tiered` = mastery-driven progression curve mirroring vanilla's `milestonesMastery` cadence (see table below). `Always` = full stack from level 0 (pure QoL override). Cycle cadence is unchanged in all modes. Independent of `TimingEnabled`. |

`Tiered` progression table:

| Autopilot mastery | Per-tick deposit                    |
| ----------------- | ----------------------------------- |
| 0–9               | 1 unit (vanilla)                    |
| 10–19             | 25% of stack/tick (min 5 units)     |
| 20–29             | 50% of stack/tick (min 10 units)    |
| 30–39             | 75% of stack/tick (min 25 units)    |
| 40+               | full stack/tick                     |

Mastery accrues automatically — every autopilot tick grants 10 XP to the autopilot tree, no skill points required. The current tier and next-threshold info is shown in the Engineering tree's mastery badge tooltip while `Tiered` is active.
| `RefineryRoute`    | `false` | When cargo contains ore and the autopilot would fly back to your home station (and home has no refinery), divert to the nearest station that does.                                            |
| `RefineryMaxHops`  | `2`     | Maximum jump-gate hops to search for a refinery station. Matches the vanilla mission-station search range. Accepts `1`–`10`.                                                                  |
| `AutoRefine`       | `false` | On autopilot arrival at a station with a refinery, enable that refinery's Auto-Refine toggle. Setting sticks per-station.                                                                     |
| `AutoLbrtr`        | `false` | While autopilot is engaged, auto-fire the equipped LB-RTR Bot salvage ability at the closest in-range wreck with salvage whenever it comes off cooldown. Skipped while you're mid-manual-cast. Independent of `TimingEnabled`. |
| `AutoLbrtrRange`   | `80`    | Max distance (world units) from the player ship to scan for wrecks when `AutoLbrtr` is enabled. Accepts `20`–`500`. Tune up if the drone reliably reaches farther wrecks before its duration expires; down if it keeps falling short. |
| `AutoSafeCracker`  | `false` | While autopilot is engaged, auto-fire the equipped Crackshot Drone mining ability at the closest in-range asteroid with surface ore whenever it comes off cooldown. Skipped while you're mid-manual-cast. Independent of `TimingEnabled`. |
| `AutoSafeCrackerRange` | `80` | Max distance (world units) from the player ship to scan for asteroids when `AutoSafeCracker` is enabled. Accepts `20`–`500`. Tune up if the drone reliably reaches farther asteroids before its duration expires; down if it keeps falling short. |

Disable any feature independently — no rebuild needed, just relaunch the game. The three default-on toggles fix UI/architecture issues without bypassing skill-tree progression; the opt-in toggles change what ECHO decides or casts on your behalf, not just how it executes.

## Troubleshooting

**No load line in the BepInEx console**
- Check that `BepInEx/plugins/VGEcho/VGEcho.dll` exists.
- Enable the console: `BepInEx/config/BepInEx.cfg` → `[Logging.Console]` → `Enabled = true`.

**Plugin loads but nothing happens during warp**
- Autopilot has to be engaged (default hotkey `T`). Every patch is gated on `GamePlayer.current.autoPlay`; when autopilot is off, the vanilla cycle runs unchanged.
- Confirm `TimingEnabled = true` and the individual feature toggle is `true` in `vgecho.cfg`.

**`TypeInitializationException` on load**
- Likely a game update renamed an internal field or method that VGEcho hooks. See "Known limitations" below. A rebuild against the new game version is needed.

## Known limitations

- **Game version drift** — VGEcho hooks private method names (`IdleManager.DropFoundItem`, `IdleManager.Update`, `IdleManager.IdleTravelToSpaceStation`, `TravelManager.TravelToNextWaypoint`), a private field literal (`IdleManager.idleTravelTarget`), compiler-generated backing-field literals (`<updateTimer>k__BackingField`, `<updateTimerBase>k__BackingField`), an IL-level method reference to `Inventory.Remove(InventoryItemType, int)`, and the autopilot tree name `"PromptEngineering"` for mastery lookups. A patch that renames any of these breaks the corresponding feature at load time (the stack-deposit transpiler self-disables with a console warning if the callsite count changes; mastery lookups fall through to "level 0" if the tree name changes, leaving stack-deposit gated as if mastery were never earned). File an issue with the BepInEx console output and wait for a new VGEcho build.
- **Booster cadence stays vanilla** — stack-deposit reduces drain to one tick per item *type*, but each tick still waits the vanilla `400/cargoCapacity` seconds between item types. That's intentional: ship-progression (cargo capacity) and the Prompt Engineering skill tree are vanilla's progression hooks for autopilot speed, and bypassing them was the predecessor `FastDeposit` / `FastFetch` features' main flaw — they're now removed.

## Building from source

Requires .NET SDK 8+. The Makefile targets WSL + a Steam install at `/mnt/c/Program Files (x86)/Steam/steamapps/common/Vanguard Galaxy`; override `GAME_DIR` if your install is elsewhere.

```bash
make deploy
# or with a custom install:
make deploy GAME_DIR="/mnt/d/SteamLibrary/steamapps/common/Vanguard Galaxy"
```

This symlinks the game's `Assembly-CSharp.dll` into `VGEcho/lib/` for compile-time references, builds the plugin, and copies the DLL into `<game>/BepInEx/plugins/`.

## Releasing (for maintainers)

Creating a GitHub Release auto-builds and uploads the zip via `.github/workflows/release.yml`. CI compiles `VGEcho.dll` against **publicized stubs** committed at `VGEcho/lib/`:

- `Assembly-CSharp.dll` — the game's code
- `UnityEngine.UI.dll` — uGUI widgets (Toggle, Button) needed by the UI patches
- `Unity.TextMeshPro.dll` — TMP_Text, used for UI labels

Each stub is method signatures only — no IL bodies. The real runtime assemblies take over in-game via Mono's late binding.

```bash
# One-time per game update — regenerate the publicized stubs from your
# current install and commit them. BepInEx-standard tool, MIT-licensed.
# --strip is REQUIRED: it rewrites every method body to `throw null;` so the
# committed file carries only type metadata. Running without --strip ships
# the full proprietary game IL, which is a copyright problem.
dotnet tool install -g BepInEx.AssemblyPublicizer.Cli
for dll in Assembly-CSharp.dll UnityEngine.UI.dll Unity.TextMeshPro.dll; do
  assembly-publicizer --strip "$GAME_DIR/VanguardGalaxy_Data/Managed/$dll" -o "VGEcho/lib/$dll"
done
git add VGEcho/lib/*.dll && git commit -m "chore: refresh publicized stubs for game vX.Y"

# Tag + release
gh release create v0.1.0 --title "v0.1.0" --notes "Initial release."
```

To re-run on a failed release without recreating it: `gh workflow run release.yml -f tag=v0.1.0`.

## Credits

- **Vanguard Galaxy** by [Bat Roost Games](https://store.steampowered.com/developer/BatRoostGames/) — the game being modded
- **BepInEx 5** — mod loader
- **HarmonyX** — runtime method patching

## License

MIT. See [LICENSE](LICENSE).

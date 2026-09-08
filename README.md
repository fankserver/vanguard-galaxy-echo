# Echo (VGEcho)

A BepInEx plugin for [Vanguard Galaxy](https://store.steampowered.com/app/3471800/) that enhances ECHO — the in-game autopilot AI. Surgical changes to where vanilla's per-tick autopilot loop creates avoidable friction, without bulldozing the Prompt Engineering skill tree's intentional cadence costs.

- **ETA-sync** — while the ship is warping, the Autopilot side-tab's green progress circle tracks live distance-based travel progress instead of the vanilla 12 s loop. Completes exactly on drop-out.
- **Arrival-snap** *(needs [VGModAPI](https://github.com/fankserver/vanguard-galaxy-api) 0.1.9+)* — on completing the final route, the next autonomous action fires on the following frame instead of after a 0–12 s residual wait. Driven by VGModAPI's verified `RouteCompleted` travel observation, not by a hook of our own. Without the API this one feature stays off and everything else works unchanged.
- **Stack deposit** — each deposit cycle moves the full stack of an item type instead of one unit. Cycle cadence is unchanged (still `400/cargoCapacity` seconds, still gated by ship-progression and the Prompt Engineering skill tree), so a diverse 200-unit hold drains in *one tick per item type* instead of one per unit. Ammo and currency keep their vanilla per-cycle batches.
- **Refinery routing** *(opt-in)* — when the autopilot would fly home with ore in cargo and home has no refinery, divert to the nearest friendly station with one. Saves the round-trip when mining far from base.
- **Auto-refine on arrival** *(opt-in)* — when the autopilot docks at a station with a refinery, flip that refinery's Auto-Refine toggle on so pending ore refines passively while you're there.
- **Auto LB-RTR** *(opt-in)* — while autopilot is engaged, auto-fire the LB-RTR Bot salvage ability at the closest in-range wreck whenever it's off cooldown. Same cooldown, same payload, no manual targeting — effectively turns the activated ability into a triggered one for the duration of autopilot.
- **Auto Crackshot Drone** *(opt-in)* — while autopilot is engaged, auto-fire the Crackshot Drone mining ability at the closest in-range asteroid with surface ore. Same cooldown, same payload, no manual targeting — effectively turns the mining activated ability into a triggered one for the duration of autopilot.

ETA-sync, Arrival-snap, and Stack-deposit don't change *what* ECHO decides — they fix UI lies, residual waits, and a per-tick architecture artifact respectively. The opt-in toggles change routing, station behavior, or ability casting; they default off so existing installs stay on vanilla decisions.

## Compatibility

Built and verified against **Vanguard Galaxy 0.8.2.3**. Game 0.8.2 reshaped `Inventory.Remove(InventoryItemType, int)` into `Inventory.Remove(InventoryItemType, int, bool skipFavourited = false)`; VGEcho 0.6.0 and earlier bound the old two-argument form, so on 0.8.2+ the stack-deposit patch threw at load and Harmony reported `Failed to patch ... IdleManager::DropFoundItem` in the BepInEx console. VGEcho 0.6.1 binds the current overload and forwards the native `skipFavourited` flag unchanged, leaving deposit tiers, destination caps and favourite-stack protection exactly as vanilla decides them; every later release carries that fix, 0.7.0 included. Upgrade if you are on 0.8.2 or newer.

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
   [Info :Echo] Echo v0.4.0 loaded (7 patches)
   ```

## Uninstall

Delete the `BepInEx/plugins/VGEcho/` folder. Optionally also delete `BepInEx/config/vgecho.cfg` to reset saved settings.

## Config

BepInEx writes the config to `BepInEx/config/vgecho.cfg` on first launch. All toggles live under `[Autopilot]`:

| Key                | Default | Purpose                                                                                                                                                                                       |
| ------------------ | ------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `TimingEnabled`    | `true`  | Master toggle for `EtaSync` and `ArrivalSnap`. When `false`, both are skipped and the vanilla 12 s cycle runs unchanged. Does not affect `StackDeposit`.                                      |
| `EtaSync`          | `true`  | While warping, drive the cycle from distance-based travel progress. Fill circle completes exactly on drop-out.                                                                                |
| `ArrivalSnap`      | `true`  | On genuine final-route completion, zero the cycle so the next autonomous action fires immediately. Additionally requires VGModAPI 0.1.9–0.1.x with `[Travel] Enabled = true`; without it the toggle does nothing and no game hook is installed in its place. |
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

**Arrival-snap does nothing but the rest of VGEcho works**
- That is the designed degraded state. VGEcho logs the exact reason at startup: VGModAPI not installed (logged at Info — an optional dependency being absent is not a problem), or, at Warning, its version outside `0.1.9`–`0.1.x`, no travel service (`[Travel] Enabled = false` in `vgmodapi.cfg`), or the API reporting its `native-travel` capability as unavailable.
- There is deliberately no fallback: VGEcho will not hook `TravelManager` itself when the API is unavailable. See [docs/api-travel-arrival.md](docs/api-travel-arrival.md).

**`TypeInitializationException` on load**
- Likely a game update renamed an internal field or method that VGEcho hooks. See "Known limitations" below. A rebuild against the new game version is needed.

## Known limitations

- **Game version drift** — VGEcho hooks private method names (`IdleManager.DropFoundItem`, `IdleManager.Update`, `IdleManager.IdleTravelToSpaceStation`, `TravelManager.TravelToNextWaypoint` for the opt-in auto-refine), a private field literal (`IdleManager.idleTravelTarget`), compiler-generated backing-field literals (`<updateTimer>k__BackingField`, `<updateTimerBase>k__BackingField`), a reflectively resolved method reference to `Inventory.Remove(InventoryItemType, int, bool)`, and the autopilot tree name `"PromptEngineering"` for mastery lookups. A patch that renames any of these breaks the corresponding feature at load time (the stack-deposit transpiler self-disables with a console warning if the callsite count changes; mastery lookups fall through to "level 0" if the tree name changes, leaving stack-deposit gated as if mastery were never earned). File an issue with the BepInEx console output and wait for a new VGEcho build.
- **Arrival-snap depends on VGModAPI's travel observation** — it owns no game hook of its own, so it inherits that API's coverage and its runtime-qualification status. VGModAPI's native travel group is still marked experimental. The timer write does re-read the game's own waypoint list and `TravelActive()` first, so another API subscriber starting a new route in the same dispatch cannot make the autopilot decide mid-route.
- **Booster cadence stays vanilla** — stack-deposit reduces drain to one tick per item *type*, but each tick still waits the vanilla `400/cargoCapacity` seconds between item types. That's intentional: ship-progression (cargo capacity) and the Prompt Engineering skill tree are vanilla's progression hooks for autopilot speed, and bypassing them was the predecessor `FastDeposit` / `FastFetch` features' main flaw — they're now removed.

## Building from source

Requires .NET SDK 8+. The Makefile targets WSL + a Steam install at `/mnt/c/Program Files (x86)/Steam/steamapps/common/Vanguard Galaxy`; override `GAME_DIR` if your install is elsewhere.

One extra compile-time reference is needed: **`VGModAPI.Abstractions.dll`**, the arrival-snap contract. It is never committed here and never shipped in the VGEcho package — the API plugin owns the one installed copy. `make link-api` symlinks it into `VGEcho/lib/`, defaulting to a sibling API checkout:

```
../vanguard-galaxy-api/VGModAPI.Abstractions/bin/Release/netstandard2.1/VGModAPI.Abstractions.dll
```

Build that first (`make build CONFIGURATION=Release` inside the API checkout), or point `VGAPI_DLL` anywhere else:

```bash
make build
make build VGAPI_DLL=/path/to/VGModAPI.Abstractions.dll
make test                   # asset-free: pure decisions + Cecil metadata over the built DLL
make check-bindings         # travel/timing metadata checks; needs the local install
make compat-test            # game-compatibility regression suite (no game install needed)
make compat-check-bindings  # same suite's Category=InstalledGame checks, against your local install
make deploy
# or with a custom install:
make deploy GAME_DIR="/mnt/d/SteamLibrary/steamapps/common/Vanguard Galaxy"
```

`build` symlinks the game's `Assembly-CSharp.dll` and the API contract into `VGEcho/lib/` for compile-time references; `deploy` copies `VGEcho.dll` into `<game>/BepInEx/plugins/`. Every `test` target fails when its `--filter` matches nothing, so a renamed category can't pass as a green run.

`make compat-test` is what CI runs (`.github/workflows/compat-checks.yml`). It exercises the native-`Remove` binding helper against synthetic inventories and reads the freshly built `VGEcho.dll` with Mono.Cecil to assert the plugin carries no reference to the removed two-argument overload and forwards `skipFavourited` on every deposit branch. `make compat-check-bindings` additionally reads your installed `Assembly-CSharp.dll` as metadata (never loaded, never executed, never copied) to confirm the live `DropFoundItem` still has exactly one `Inventory.Remove` callsite with the signature the transpiler binds.

## Releasing (for maintainers)

Creating a GitHub Release auto-builds and uploads the zip via `.github/workflows/release.yml`. Every push and PR additionally runs `.github/workflows/checks.yml` (build + travel/timing tests) and `.github/workflows/compat-checks.yml` (build + game-compatibility tests), both Debug and Release. All three workflows check out the public [VGModAPI](https://github.com/fankserver/vanguard-galaxy-api) repo at a pinned commit and build `VGModAPI.Abstractions` themselves, because that reference is compile-only and not committed here — since 0.7.0 the plugin does not compile without it. That build runs from inside the API checkout so its `global.json` applies, which is why the workflows install SDK `10.0.111` exactly alongside `8.0.x`.

CI compiles `VGEcho.dll` against **publicized stubs** committed at `VGEcho/lib/`:

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

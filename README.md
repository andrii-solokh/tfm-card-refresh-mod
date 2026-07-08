# Terraforming Mars — QoL mod (macOS)

A [BepInEx](https://github.com/BepInEx/BepInEx) plugin that adds quality-of-life
fixes and keyboard shortcuts to the Asmodee **Terraforming Mars** digital client
on macOS.

Everything it does is **display / UI only**. The game is server-authoritative and
re-validates every real action, so the mod cannot make an illegal move, reveal
hidden information, or give any mechanical advantage — it only makes your own
client honest and quicker to drive.

> Personal project. Not affiliated with Asmodee / Lucky Hammers. Modifying an
> online client is against the game's ToS and is used here at your own risk.

---

## Requirements

- macOS (Apple Silicon or Intel).
- Terraforming Mars installed via **Steam** (app id `800270`).
- For the mod to load, the game runs under **Rosetta (x86_64)** — the installer
  handles this automatically.
- Only to **rebuild** the plugin: the [.NET SDK](https://dotnet.microsoft.com/download)
  (`dotnet`).

## Install

```sh
git clone <this-repo-url> tfm-card-refresh-mod
cd tfm-card-refresh-mod
./setup.sh
```

`setup.sh` downloads BepInEx, applies the macOS fixes, drops `steam_appid.txt`,
and installs the plugin. Then launch modded one of two ways (Steam must be
running):

- **Steam launch option** — Library → Terraforming Mars → Properties → General →
  Launch Options:
  ```
  "/Users/<you>/Library/Application Support/Steam/steamapps/common/Terraforming Mars/run_bepinex.sh" %command%
  ```
- **Or** double-click **`Launch TFM (modded).command`**.

Launch **normally from Steam** (no launch option) any time you want the stock
game — the mod files sit inert unless launched through `run_bepinex.sh`.

### Confirm it loaded

`BepInEx/LogOutput.log` in the game folder should end with:

```
Loading [TFM Card Playability Refresh 1.0.0]
TFM Card Playability Refresh 1.0.0 loaded.
Harmony patches applied ...
Chainloader startup complete
```

## Features

1. **Card view re-checks playability** on state change (no close/reopen).
2. **Read your hand during opponent turns.**
3. **Projects open on your turn**, and the hand reopens after you play a card.
4. **No turn/phase announcements** (also removes their turn-start delay).
5. **Requirement-locked cards greyed** when viewing your hand off-turn.
6. **Action availability shown off-turn** (unused actions active, used ones dim).

## Hotkeys

`Space` confirms the "SELECT ONE" popup, else an open dialog's Yes/OK, else
Buy/Use/Select the focused card, and with nothing open it passes / skips / ends
your turn (the board's end-turn button). `Up`/`Down` move the highlighted option
in a SELECT ONE popup or resource list. Letters/numbers toggle panels (work
off-turn; suppressed only while you're actively typing in chat):

| Key | Action |
|-----|--------|
| P | Projects (hand) |
| A | Actions |
| R | Resources |
| V | Victory points |
| E | Effects |
| M | Milestones tab |
| K | Standard Projects tab |
| W | Awards tab |
| G | Convert plants → greenery |
| T | Convert heat → temperature |
| S | Sell cards (opens Sell Patents; Space confirms) |
| B | View state (inspect board) / Return |
| Space | Confirm the default button of the open dialog / focused card |
| ↑ / ↓ | Navigate a choice list |
| 1–4 | Focus a player's board (1 = you, then the others) |
| H | Toggle the on-screen shortcut overlay |

Conversions (`G`/`T`) only fire when you can actually convert (enough resources,
your turn). Tags no longer has a key (`T` is now temperature).

Hold **Cmd/Ctrl** to reveal the keys on screen: number badges appear on the
selectable cards/actions, and the Milestones / Standard Projects / Awards tabs
show their letter. Release the modifier and press the bare key to use it.

## Config (toggle features / rebind keys)

Every feature and key is configurable. Launch the game once with the mod, then
edit:

```
BepInEx/config/com.experiment.tfm.cardrefresh.cfg
```

- `[Keys]` — rebind any shortcut. Values are Unity `KeyCode` names (e.g. `P`,
  `Space`, `UpArrow`, `Keypad1`). Change and relaunch.
- `[Features]` — turn any feature `true`/`false`: `Hotkeys` (master switch),
  `SuppressAnnouncements`, `KeepHandOpenOffTurn`, `AutoOpenHandAfterPlay`,
  `DimUnplayableInHandView`, `ShowActionAvailabilityOffTurn`,
  `SortUsableActionsFirst`, `HandReadableOffTurn`, `CardRefresh`,
  `PlayTurnStartSound` (ping when your turn begins, even with announcements
  hidden). `TurnStartSound` picks the sound id (e.g. `SFX_OTHER_PLAYER_TURN`,
  `SFX_MENU_CONFIRM`, `SFX_POPUP_OPEN`).

Config changes take effect on the next launch. In-game, press **H** for a quick
overlay of the current key bindings.

## Rebuild after a game update

A game patch can rename the classes the plugin hooks. Rebuild against the updated
game DLLs:

```sh
./build.sh
```

It recompiles and reinstalls to `BepInEx/plugins/`, and errors on anything that
moved so you know what to fix. Patches are wrapped in try/catch and fail safe, so
a stale patch goes silent rather than crashing the game.

## Uninstall

Delete from the game folder: `BepInEx/`, `libdoorstop.dylib`, `run_bepinex.sh`,
`.doorstop_version`, `steam_appid.txt`. Clear the Steam launch option if set.

## Why it's set up this way

- **BepInEx 5.4.23.5** (universal macOS). The 6.x bleeding-edge did not complete
  its chainloader on this Unity 6 build.
- **Forced x86_64 / Rosetta** — doorstop's Mono hook does not fire on this game's
  Unity 6.0.62 **arm64** runtime, but works under x86_64.
- **Absolute doorstop path** — `arch -e` strips `DYLD_LIBRARY_PATH`, so a bare
  `libdoorstop.dylib` name aborts the process at launch.
- **`steam_appid.txt` + `SteamAppId`** — stop `SteamAPI_RestartAppIfNecessary`
  from relaunching the game through Steam, which would spawn a fresh process
  without the mod.

## Files

| File | Purpose |
|------|---------|
| `Plugin.cs` | Plugin source (all features + hotkeys) |
| `TfmCardRefresh5.csproj` | Build project (references the local game + BepInEx DLLs) |
| `TfmCardRefresh.dll` | Prebuilt plugin |
| `setup.sh` | One-shot installer |
| `build.sh` | Rebuild + reinstall the plugin |
| `run_bepinex.sh.working` | BepInEx launcher with the macOS fixes |
| `Launch TFM (modded).command` | Double-click launcher |

#!/bin/bash
# Double-click to launch Terraforming Mars with the card-fix mod.
# Steam must already be running and you must be logged in.
#
# This replicates the exact working recipe:
#   - SteamAppId env + steam_appid.txt  -> stops Steam from relaunching the game
#     (which would drop the mod injection)
#   - run_bepinex.sh forces the x86_64 (Rosetta) slice, where doorstop's mono
#     hooking works on this Unity 6 build.

GAME="$HOME/Library/Application Support/Steam/steamapps/common/Terraforming Mars"
SCRIPT="$GAME/run_bepinex.sh"

if [ ! -x "$SCRIPT" ]; then
  echo "run_bepinex.sh not found at: $SCRIPT"
  echo "Is the mod still installed?"
  read -r -p "Press Return to close..."
  exit 1
fi

echo 800270 > "$GAME/steam_appid.txt"
export SteamAppId=800270
export SteamGameId=800270

echo "Launching Terraforming Mars (modded)..."
cd "$GAME" || exit 1
exec "$SCRIPT"

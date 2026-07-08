#!/bin/sh
# Rebuild the plugin after a game update. Requires dotnet + Steam-installed game.
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
GAME="$HOME/Library/Application Support/Steam/steamapps/common/Terraforming Mars"
GAME_MANAGED="$GAME/TerraformingMars.app/Contents/Resources/Data/Managed"
BEPINEX_CORE="$GAME/BepInEx/core"
export DOTNET_ROLL_FORWARD=Major
dotnet build "$DIR/TfmCardRefresh5.csproj" -c Release \
  -p:GameManaged="$GAME_MANAGED" -p:BepInExCore="$BEPINEX_CORE"
cp "$DIR/bin/Release/TfmCardRefresh.dll" "$GAME/BepInEx/plugins/"
echo "Rebuilt and reinstalled into BepInEx/plugins/. Relaunch the game to load it."

#!/bin/bash
# One-shot installer: downloads BepInEx, applies the macOS fixes, installs the
# plugin into your Steam copy of Terraforming Mars. Steam must own the game.
#
#   ./setup.sh
#
set -e

GAME="$HOME/Library/Application Support/Steam/steamapps/common/Terraforming Mars"
REPO="$(cd "$(dirname "$0")" && pwd)"
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_macos_universal_5.4.23.5.zip"

if [ ! -d "$GAME/TerraformingMars.app" ]; then
  echo "Could not find the game at:"
  echo "  $GAME"
  echo "Install Terraforming Mars via Steam first, then re-run."
  exit 1
fi

# 1. Install BepInEx 5.4.23.5 (universal macOS) if not already present.
if [ ! -d "$GAME/BepInEx/core" ]; then
  echo "Downloading BepInEx 5.4.23.5..."
  TMP="$(mktemp -d)"
  curl -fsSL "$BEPINEX_URL" -o "$TMP/bepinex.zip"
  (cd "$GAME" && unzip -oq "$TMP/bepinex.zip")
  rm -rf "$TMP"
else
  echo "BepInEx already installed."
fi

# 2. Install the pre-configured launcher (absolute doorstop path + forced x86_64),
#    pointing executable_name at this machine's game path.
cp "$REPO/run_bepinex.sh.working" "$GAME/run_bepinex.sh"
/usr/bin/sed -i '' "s|^executable_name=.*|executable_name=\"$GAME/TerraformingMars.app\"|" "$GAME/run_bepinex.sh"
chmod +x "$GAME/run_bepinex.sh"

# 3. steam_appid.txt stops Steam from relaunching the game (which drops injection).
echo 800270 > "$GAME/steam_appid.txt"

# 4. Install the plugin.
mkdir -p "$GAME/BepInEx/plugins"
cp "$REPO/TfmCardRefresh.dll" "$GAME/BepInEx/plugins/"

echo
echo "Installed. Two ways to launch modded (Steam must be running):"
echo "  A) Steam launch option (Properties > General > Launch Options):"
echo "     \"$GAME/run_bepinex.sh\" %command%"
echo "  B) Double-click 'Launch TFM (modded).command' in this folder."
echo
echo "Launch normally from Steam (no launch option) for the stock game."

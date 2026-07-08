#!/bin/bash
# Double-click this file to install the mod.
#
# First time only: if double-clicking shows a warning, right-click this file
# and choose "Open", then click "Open" again. macOS blocks double-clicking
# downloaded scripts until you allow it once.

DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$DIR" || exit 1

clear
echo "==================================================="
echo "  Terraforming Mars mod - installer"
echo "==================================================="
echo

if bash "$DIR/setup.sh"; then
  echo
  echo "==================================================="
  echo "  All done!"
  echo
  echo "  To play with the mod, double-click:"
  echo "      Launch TFM (modded).command"
  echo "  (in this same folder). Make sure Steam is open."
  echo "==================================================="
else
  echo
  echo "Something went wrong. Make sure Terraforming Mars is"
  echo "installed through Steam, then try again."
fi

echo
read -r -p "Press Return to close this window..."

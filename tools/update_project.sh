#!/bin/zsh
# Brings an already set up Unity project up to date with the port in this repo. setup and build both run it,
# so after a git pull ./build (or ./setup) picks up fixes without ripping the game again.
# If it changes anything the game's assets depend on, it deletes work/.done/build, so the next ./setup or
# ./build rebuilds them.
# Usage: update_project.sh <repo dir> <work dir> <project dir>
set -euo pipefail

HERE=${1:A}
WORK=${2:A}
PROJECT=${3:A}
changed=0

# The game ships the Input System as a DLL compiled for Windows, which can't see controllers on iPhone.
# The package from Unity has the iOS controller support.
mkdir -p $WORK/removed-plugins
for n in Unity.InputSystem Unity.InputSystem.ForUI; do
  if [[ -f $PROJECT/Assets/Plugins/$n.dll ]]; then
    mv $PROJECT/Assets/Plugins/$n.dll{,.meta} $WORK/removed-plugins/
    changed=1
  fi
done
python3 -I - $PROJECT/Packages/manifest.json <<'EOF' || changed=1
import json, sys
p = sys.argv[1]
m = json.load(open(p))
want = {"com.unity.addressables": "2.5.0", "com.unity.inputsystem": "1.14.2"}
if all(m["dependencies"].get(k) == v for k, v in want.items()):
    sys.exit(0)
m["dependencies"].update(want)
m["dependencies"] = dict(sorted(m["dependencies"].items()))
json.dump(m, open(p, "w"), indent=2)
sys.exit(1)
EOF

# Edits to the game's scripts, then the port's own code and art. Game and port scripts are compiled by every
# player build and native plugins go straight into the Xcode project, so changes to them don't need the asset
# rebuild; editor scripts and art do.
out=$(python3 -I $HERE/tools/patch_game.py $PROJECT/Assets) || { echo "$out"; exit 1; }
if [[ -n $(rsync -a --itemize-changes $HERE/port/Assets/ $PROJECT/Assets/ | grep '^>f' | grep -v -E ' (Scripts/.*\.cs|Plugins/iOS/.*)$') ]]; then changed=1; fi

# Shader replacements.
out=$(python3 -I $HERE/tools/gen_shaders.py $PROJECT/Assets $PROJECT/Assets $WORK/tmp_shaders)
if [[ $out != *"changed: 0 "* ]]; then changed=1; fi

if (( changed )); then
  rm -f $WORK/.done/build
  echo "  updated the Unity project"
fi

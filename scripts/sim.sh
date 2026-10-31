#!/bin/zsh
# sim.sh XCODE_DIR [BUNDLE_ID]: compile Unity's Xcode project for the iOS Simulator, install it and launch it.
# The game's output goes to <XCODE_DIR>-console.log. Set SIM_UDID to pick a simulator (default: the newest
# available iPhone), SIM_SAVES to a folder of user*.dat files to copy in, SIM_AUTOLOAD to a save slot to load
# straight away, and SIM_TOUCHSCRIPT to a fake-touch script (see port/Assets/Scripts/Assembly-CSharp/IOSPort/TouchScript.cs).
set -euo pipefail
export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
XCODE=${1:A}
BUNDLE=${2:-com.personal.silksong}
DERIVED=$XCODE-DerivedData
UDID=${SIM_UDID:-$(xcrun simctl list devices available -j | python3 -I -c '
import json, sys
devs = [d for rt, ds in json.load(sys.stdin)["devices"].items() if "iOS" in rt for d in ds if d["name"].startswith("iPhone")]
print(devs[-1]["udid"] if devs else "")')}
[[ -n $UDID ]] || { echo "No iPhone simulator found. Add one in Xcode > Window > Devices and Simulators."; exit 1; }

xcodebuild -project $XCODE/Unity-iPhone.xcodeproj -scheme Unity-iPhone -configuration Release \
  -sdk iphonesimulator -destination "id=$UDID" -derivedDataPath $DERIVED build \
  | grep -E "error:|\*\* BUILD" || true
APP=$(find $DERIVED/Build/Products -maxdepth 2 -name "*.app" -type d | head -1)
[[ -d $APP ]] || { echo "No simulator app produced"; exit 1; }

xcrun simctl boot $UDID 2>/dev/null || true
open -a Simulator
xcrun simctl terminate $UDID $BUNDLE 2>/dev/null || true
xcrun simctl install $UDID $APP
DATA=$(xcrun simctl get_app_container $UDID $BUNDLE data)
mkdir -p $DATA/Documents
[[ -n ${SIM_SAVES:-} ]] && cp $SIM_SAVES/*.dat $DATA/Documents/
[[ -n ${SIM_AUTOLOAD:-} ]] && echo $SIM_AUTOLOAD > $DATA/Documents/iosport-autoload.txt
[[ -n ${SIM_TOUCHSCRIPT:-} ]] && cp $SIM_TOUCHSCRIPT $DATA/Documents/iosport-touchscript.txt
xcrun simctl launch --console-pty --terminate-running-process $UDID $BUNDLE > $XCODE-console.log 2>&1 &
echo "Launched $BUNDLE on $UDID (console: $XCODE-console.log)"
echo "App data: $DATA"

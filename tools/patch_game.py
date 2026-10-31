"""Applies the iOS port's edits to the game scripts AssetRipper exported.

Usage: patch_game.py <project Assets dir>

Each edit finds a short piece of the original code and changes it. Running it twice is fine:
edits that are already in place are skipped. If the game updates and an anchor can't be found,
it stops and says which file needs looking at.
"""
import os
import re
import sys

SCRIPTS = os.path.join(sys.argv[1], "Scripts", "Assembly-CSharp")
failed = []


def patch(rel, pairs):
    path = os.path.join(SCRIPTS, rel)
    if not os.path.exists(path):
        failed.append(f"{rel}: file not found")
        return
    s = open(path, encoding="utf-8").read()
    changed = False
    for old, new in pairs:
        if new in s:
            continue
        if s.count(old) != 1:
            failed.append(f"{rel}: couldn't find the code to change ({old.strip().splitlines()[0][:80]})")
            continue
        s = s.replace(old, new)
        changed = True
    if changed:
        open(path, "w", encoding="utf-8").write(s)
        print("  patched", rel)


# Steam/GOG/Xbox online features need Windows-only libraries, and saves go straight into
# Documents so they show up in the Files app.
patch("DesktopPlatform.cs", [
    ("\tprivate void CreateOnlineSubsystem()\n\t{\n\t\tList<CreateOnlineSubsystemDelegate> list = new List<CreateOnlineSubsystemDelegate>();",
     "\tprivate void CreateOnlineSubsystem()\n\t{\n"
     "\t\t// iOS port: Steam/GOG/GameCore need Windows-only native libraries.\n"
     "\t\tif (Application.platform == RuntimePlatform.IPhonePlayer)\n\t\t{\n"
     "\t\t\tDebug.LogFormat(this, \"iOS: online subsystems disabled.\");\n\t\t\treturn;\n\t\t}\n"
     "\t\tList<CreateOnlineSubsystemDelegate> list = new List<CreateOnlineSubsystemDelegate>();"),
    ("\t\tsaveDirPath = Path.Combine(saveDirPath, path);\n",
     "\t\t// iOS port: keep saves at the top of Documents so they show directly in the Files app.\n"
     "\t\tif (Application.platform != RuntimePlatform.IPhonePlayer)\n\t\t{\n"
     "\t\t\tsaveDirPath = Path.Combine(saveDirPath, path);\n\t\t}\n"),
])

# InControl's native input library only ships for Windows, so iOS uses Unity's controller input.
patch("InControl/NativeInputDeviceManager.cs", [
    ("\t\t\tif (Application.platform != RuntimePlatform.OSXPlayer && Application.platform != RuntimePlatform.OSXEditor && Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor && Application.platform != RuntimePlatform.IPhonePlayer && Application.platform != RuntimePlatform.tvOS)",
     "\t\t\t// iOS port: InControlNative only ships for Windows, so iOS uses Unity's built-in controller input.\n"
     "\t\t\tif (Application.platform != RuntimePlatform.OSXPlayer && Application.platform != RuntimePlatform.OSXEditor && Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor)"),
])

# Detect controller types (for button prompts) on iPhone too, and skip the Windows-only XInput lookup.
patch("InputHandler.cs", [
    ("Application.platform == RuntimePlatform.LinuxPlayer || Application.platform == RuntimePlatform.LinuxEditor)\n\t\t{\n\t\t\tswitch (inputDevice.DeviceStyle)",
     "Application.platform == RuntimePlatform.LinuxPlayer || Application.platform == RuntimePlatform.LinuxEditor || Application.platform == RuntimePlatform.IPhonePlayer)\n\t\t{\n\t\t\tswitch (inputDevice.DeviceStyle)"),
    ("if (InputManager.EnableXInput || InputManager.NativeInputEnableXInput)",
     "if ((InputManager.EnableXInput || InputManager.NativeInputEnableXInput) && Application.platform != RuntimePlatform.IPhonePlayer)"),
])

# Hide menu options that do nothing on iPhone (PerformanceSettings decides which).
patch("MenuButtonList.cs", [
    ("\t\t\tSelectable selectable = entry.Selectable;\n\t\t\tMenuButtonListCondition condition = entry.Condition;\n",
     "\t\t\tSelectable selectable = entry.Selectable;\n"
     "\t\t\tif (IOSPort.PerformanceSettings.HidesMenuOption(selectable))\n\t\t\t{\n"
     "\t\t\t\tselectable.gameObject.SetActive(value: false);\n\t\t\t\tselectable.interactable = false;\n\t\t\t\tcontinue;\n\t\t\t}\n"
     "\t\t\tMenuButtonListCondition condition = entry.Condition;\n"),
])

# "Reset to default" would force 1920x1080 on the phone.
patch("UnityEngine/UI/MenuResolutionSetting.cs", [
    ("\t\tpublic void ResetToDefaultResolution()\n\t\t{\n\t\t\tScreen.SetResolution(1920, 1080, Screen.fullScreen);\n",
     "\t\tpublic void ResetToDefaultResolution()\n\t\t{\n\t\t\tif (!Application.isMobilePlatform)\n\t\t\t{\n"
     "\t\t\t\tScreen.SetResolution(1920, 1080, Screen.fullScreen);\n\t\t\t}\n"),
])

# Always start on save slot 1 instead of the last slot played.
patch("UIManager.cs", [
    ("\t\tSaveSlotButton itemToHighlight = ((!DemoHelper.IsDemoMode) ? Platform.Current.LocalSharedData.GetInt(\"lastProfileIndex\", 0) : 0) switch",
     "\t\t// iOS port: always start on slot 1 instead of the last slot played.\n"
     "\t\tSaveSlotButton itemToHighlight = ((!DemoHelper.IsDemoMode && !Application.isMobilePlatform) ? Platform.Current.LocalSharedData.GetInt(\"lastProfileIndex\", 0) : 0) switch"),
])

# The SCREEN setting in the port's menu can limit the view to 16:9.
patch("ForceCameraAspect.cs", [
    ("\tpublic static float CurrentViewportAspect { get; private set; }",
     "\t// iOS port: the SCREEN setting (IOSPort.PortSettings.FullWidth) can limit the view to 16:9.\n"
     "\tprivate static int iosPortVersion;\n\n\tprivate int iosPortAppliedVersion;\n\n"
     "\tpublic static void IOSPortReapply()\n\t{\n\t\tiosPortVersion++;\n\t}\n\n"
     "\tpublic static float CurrentViewportAspect { get; private set; }"),
    ("\t\tif (lastX != Screen.width || lastY != Screen.height)\n\t\t{",
     "\t\tif (lastX != Screen.width || lastY != Screen.height || iosPortAppliedVersion != iosPortVersion)\n\t\t{\n"
     "\t\t\tiosPortAppliedVersion = iosPortVersion;"),
    ("\t\tfloat clampedBetween = new MinMaxFloat(1.6f, 2.3916667f).GetClampedBetween(num);",
     "\t\tfloat clampedBetween = new MinMaxFloat(1.6f, (Application.isMobilePlatform && !IOSPort.PortSettings.FullWidth) ? 1.7777778f : 2.3916667f).GetClampedBetween(num);"),
])


# Controllers: the game pauses on Start, and Unity's iOS controller input has no fixed button number for the
# second menu button (Xbox View, PlayStation Share/Create, MFi Options), so the unused ones all press Back,
# which opens the inventory. Stick clicks get mapped too.
def patch_mfi():
    rel = "InControl/UnityDeviceProfiles/AppleMFiUnityProfile.cs"
    path = os.path.join(SCRIPTS, rel)
    if not os.path.exists(path):
        failed.append(f"{rel}: file not found")
        return
    s = open(path, encoding="utf-8").read()
    if "IOSPort:" in s:
        return
    old_pause = '\t\t\t\t\tName = "Pause",\n\t\t\t\t\tTarget = InputControlType.Pause,'
    m = re.search(r"ButtonMappings = new InputControlMapping\[(\d+)\]\n\t\t\t\{\n", s)
    if s.count(old_pause) != 1 or not m:
        failed.append(f"{rel}: couldn't find the button mappings")
        return
    s = s.replace(old_pause, '\t\t\t\t\t// IOSPort: the game pauses on Start (it never binds InputControlType.Pause).\n'
                             '\t\t\t\t\tName = "Pause",\n\t\t\t\t\tTarget = InputControlType.Start,')
    extra = ("\t\t\t\t// IOSPort: Unity's iOS controller input has no fixed button number for the second menu button (Xbox View,\n"
             "\t\t\t\t// PlayStation Share/Create, MFi Options), so the unused ones all press Back, which opens the inventory.\n")
    mapping = "\t\t\t\tnew InputControlMapping\n\t\t\t\t{\n\t\t\t\t\tName = \"%s\",\n\t\t\t\t\tTarget = InputControlType.%s,\n\t\t\t\t\tSource = InputDeviceProfile.Button(%d)\n\t\t\t\t},\n"
    for i in (1, 2, 3, 18, 19):
        extra += mapping % ("Options %d" % i, "Back", i)
    extra += mapping % ("Left Stick Button", "LeftStickButton", 16)
    extra += mapping % ("Right Stick Button", "RightStickButton", 17)
    s = s.replace(m.group(0), "ButtonMappings = new InputControlMapping[%d]\n\t\t\t{\n" % (int(m.group(1)) + 7) + extra, 1)
    open(path, "w", encoding="utf-8").write(s)
    print("  patched", rel)


patch_mfi()

if failed:
    print("Some game scripts couldn't be patched. This usually means the game version is different:")
    for f in failed:
        print("  -", f)
    sys.exit(1)

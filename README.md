# Silksong iOS port

This turns your own PC copy of Hollow Knight: Silksong into an iPhone/iPad app (an IPA) that you sideload. It can also build BepInEx mods into the app.

There's no game in this repo. You need to own Silksong on Steam or GOG. The scripts rip your copy into a Unity project, apply the port, and build the IPA on your Mac.

Short version:

```
./setup "/path/to/Hollow Knight Silksong"    # once, takes a few hours
./build                                       # whenever you change mods, about 25 minutes
```

The rest of this file goes through everything step by step. If you've never done this kind of thing before, read it top to bottom once before starting.

---

## What you need

**A Mac.** This can't be done on Windows or Linux, because building an iPhone app needs Xcode and Xcode only runs on macOS. Apple Silicon (M1 or newer) is best. Intel Macs should work but are a lot slower and haven't been tested. You need a macOS version that runs Xcode 15 or newer (macOS 13.5 or later).

**About 65 GB of free disk space** while setting up (75 to be comfortable). Roughly:

| What | Size |
|---|---|
| Xcode | 15-20 GB |
| Unity 6000.0.50f1 with iOS support | about 10 GB |
| The PC game files (setup deletes its copy once it no longer needs it) | about 8 GB |
| The ripped Unity project (plus Unity's cache) | about 40 GB |
| Build output and the IPA | 5-10 GB |

Once setup has finished you can get some back (see [Freeing up space](#freeing-up-space)). The project itself has to stay if you want to build with mods later. You can put the work folder on an external SSD with `--work` if your Mac is short on space. A slow USB hard drive will make everything take much longer.

**Silksong for PC (Windows version)**, on Steam or GOG. Even if you play on a Mac, the port is made from the Windows files. Getting them is covered below.

**An iPhone or iPad** with iOS 15 or newer, a cable, and an Apple ID to sign the app with (a free one works).

**Accounts:** a free Unity account (Unity needs a free Personal license to run), and your Steam or GOG account.

**Software** (all free):

- [Xcode](https://apps.apple.com/app/xcode/id497799835) from the Mac App Store
- [Unity Hub](https://unity.com/download), plus Unity **6000.0.50f1** with **iOS Build Support**. It has to be this exact version.
- [Sideloadly](https://sideloadly.io) to put the IPA on your phone
- Python 3 (comes with Xcode's command line tools)
- [Homebrew](https://brew.sh) and `innoextract`, only if you use the GOG installer

setup downloads the rest by itself: AssetRipper 2.0.0, the .NET 8 SDK (into `~/.dotnet`, no password needed), SteamCMD if you use Steam, and a couple of small helper tools.

---

## Step 1: Install Xcode

1. Install Xcode from the App Store. It's big, so give it a while.
2. Open it once and let it install its extra components. Accept the license.
3. In Terminal, run `xcode-select --install` to get the command line tools (this is where `python3` comes from). If it says they're already installed, that's fine.

You don't need an Apple developer account for Xcode. setup builds the app unsigned, and Sideloadly signs it later.

## Step 2: Install Unity

1. Install Unity Hub and sign in with your Unity account.
2. Go to **Preferences > Licenses > Add** and pick **Get a free personal license**. Without this, Unity won't run in the background and setup will stop.
3. Go to **Installs > Install Editor > Archive > download archive**. Find **6000.0.50f1** on the Unity website and open it with Unity Hub.
4. When it asks for modules, tick **iOS Build Support**. You don't need anything else.

If you skip step 3, setup will offer to download and install 6000.0.50f1 for you. You still have to do steps 1 and 2 for the license.

## Step 3: Get the game files

Pick whichever matches where you own the game.

### GOG

1. Log in at gog.com and go to your library. Open Silksong.
2. Under the download options, switch the system to **Windows** and download the **offline backup installer**. That's a `setup_hollow_knight_silksong_....exe` file, possibly with some `.bin` files next to it. Keep them all in the same folder.
3. Install innoextract: `brew install innoextract`
4. You'll pass the `.exe` to setup in the next step. It unpacks it itself.

If you already have the Windows game folder from GOG Galaxy on a PC, you can copy that over instead. See "A game folder you already have" below.

### Steam

setup can download the Windows version with SteamCMD (Valve's command line Steam client):

```
./setup --steam your_steam_username
```

It asks for your Steam password and Steam Guard code in the terminal. Your login goes straight to Steam and isn't stored by these scripts.

You can also copy the folder from a Windows PC. On the PC, find it with Steam: right-click Silksong > Manage > Browse local files.

### A game folder you already have

Any Windows copy of the game folder works. It's the folder that has `Hollow Knight Silksong.exe` and `Hollow Knight Silksong_Data` in it. Copy it to the Mac (a USB stick or AirDrop is fine) and pass that folder to setup.

If that copy has mods installed (BepInEx), that's okay. setup removes BepInEx from its own copy before ripping and leaves your folder alone.

**Game version:** the port was made with GOG version 1.0.29315. Newer versions will usually work. If a game update changed one of the scripts the port edits, setup stops at "Patching the game scripts" and names the file.

## Step 4: Run setup

Download this repo (green **Code** button > **Download ZIP**, or `git clone`), open Terminal, and `cd` into it. Then run one of these:

```
./setup "/Users/you/Downloads/Hollow Knight Silksong"           # a game folder
./setup ~/Downloads/setup_hollow_knight_silksong_1.0.29315.exe  # GOG installer
./setup --steam your_steam_username                             # Steam
```

Tip: type `./setup ` with a space at the end, then drag the folder or .exe from Finder into the Terminal window to paste its path.

What it does, in order:

| Step | Time on an M1 |
|---|---|
| Checks Xcode, Unity, disk space; installs .NET if needed | a minute |
| Gets the game files | a few minutes (longer for Steam downloads) |
| Rips the game into a Unity project with AssetRipper | 15-30 min |
| Applies the port: iPhone code, touch controls, game script edits, asset layout, shaders | a few minutes |
| Opens the project in Unity for the first time | 20-40 min |
| Builds the iOS version (textures, asset bundles, Xcode project) | 45-90 min |
| Compiles the IPA with Xcode | 5-10 min |

So budget around 2-3 hours. Most of it is waiting, and you can use the Mac in the meantime, but it'll be slow. Plug in your charger and don't let the Mac go to sleep (`caffeinate -i ./setup ...` keeps it awake).

When it's done you get `output/Silksong.ipa`.

**If it stops partway:** read the red message, fix the problem, and run the exact same command again. Steps that already finished get skipped, so you won't redo the 30-minute ones. Logs are in `work/logs/`.

To start over from nothing, delete the `work` folder.

## Step 5: Install it on your phone

1. Open Sideloadly and plug in your iPhone. Trust the computer on the phone if it asks.
2. Drag `output/Silksong.ipa` into Sideloadly, enter your Apple ID, and press Start.
3. On the phone, go to **Settings > General > VPN & Device Management**, tap your Apple ID, and trust it.
4. On iOS 16 or newer, also turn on **Settings > Privacy & Security > Developer Mode** (the phone restarts).

With a free Apple ID the app stops opening after 7 days. Sideload it again with Sideloadly to refresh it. Your saves aren't touched. A paid Apple developer account makes it last a year.

### Moving your PC save over

Saves are `user1.dat` to `user4.dat` (one per slot). On PC they're usually in:

- Windows: `%USERPROFILE%\AppData\LocalLow\Team Cherry\Hollow Knight Silksong\<number>\`
- Mac: `~/Library/Application Support/unity.Team-Cherry.Silksong/<number>/`

Open the Files app on the phone (or Finder with the phone plugged in > Files tab), go to **On My iPhone > Silksong**, and copy the `.dat` files in while the game is closed. GOG saves are known to work. Steam saves use the same files and should too.

---

## Adding mods

Do setup first. After that, adding or changing mods means running `./build`, which takes about 25 minutes. It doesn't repeat the long setup steps.

1. **Download the PC version of the mod**, the normal BepInEx 5 one, from Thunderstore or Nexus. There's no special iOS version of any mod, so don't look for one.
2. **Check what it depends on.** On Thunderstore it's listed under "Dependencies". Download those too (but not BepInEx itself, that's already included). For example, QoL needs ModMenu and FsmUtil. If something's missing, build says so and leaves that mod out.
3. **Unzip each one into `BepInEx/plugins/`** in this repo, the same way you would on PC. Each mod usually gets its own folder with its DLLs and files.
   - If a mod comes with a `patchers` folder, those files go in `BepInEx/patchers/`.
   - If you want a mod to start with certain settings, put its `.cfg` file in `BepInEx/config/`.
4. **Close Unity** if you have the project open.
5. Run:
   ```
   ./build
   ```
6. Read the **Heads up** list at the end of "Reading mods". It tells you about anything in your mods that can't work on iPhone (more on that below). The rest of the mod still gets built in.
7. Install `output/Silksong-Modded.ipa` with Sideloadly like before. Installing over the unmodded version keeps your saves.

Other options:

```
./build --name MyMods          # writes output/MyMods.ipa
./build --sim                  # runs it in the iOS Simulator on your Mac instead, for testing
./build --bundle-id com.you.silksong-modded    # install it next to the unmodded app instead of replacing it
```

To remove a mod, delete its folder from `BepInEx/plugins` and run `./build` again.

### On the phone

In the Files app under **On My iPhone > Silksong**:

- `BepInEx/config/` has the mod settings. You can edit them there, and your changes stay when you install a new build.
- `BepInEx/plugins/` has the mods' files (skins, textures, text). It gets replaced every time you install a new build.
- `BepInEx/LogOutput.log` is the mod log, same as on PC. Look here first if a mod isn't working.
- `BepInEx/iosport/missing-hooks.txt` lists game functions a mod tried to patch that this build didn't expect. Copy it into this repo's `BepInEx/` folder and run `./build` again, and they'll be included.

### What mods can't do on iPhone

On PC, BepInEx loads mod DLLs while the game is running, and Harmony rewrites game functions in memory. iOS doesn't allow apps to load or change code at runtime, so `./build` does that work ahead of time and compiles everything into the app. Most mods work fine this way, but some things can't be done ahead of time:

- **Harmony transpilers and IL manipulators** (they rewrite code). They're skipped, and the mod's other patches still work.
- **MonoMod hooks** (`On.` / `IL.`), `Reflection.Emit`, and loading DLLs at runtime.
- **Asset bundles made for PC.** Unity bundles only work on the platform they were built for, so custom models and similar won't load. PNG, WAV and text files from mods are fine.
- **BepInEx 6 mods.** Only BepInEx 5 is supported.
- **Native DLLs** (C/C++).
- Patches on constructors and generic methods aren't supported yet.

If a mod mostly works but one feature doesn't, it's usually one of these.

---

## Port features

- Touch controls with a joystick, d-pad or arrow keys (pick in the settings panel). They hide while you use a controller.
- Controllers (Xbox, PlayStation, MFi) work, including the inventory and pause buttons.
- A settings panel (the gear on the title screen) for touch controls, screen width (full width or 16:9 like PC), render scale and other performance options.
- Automatic save backups in `SaveBackups/`, and an **Export saves** button that zips your saves to share or keep in iCloud.
- Saves and mod files visible in the Files app.
- Runs at the screen's refresh rate instead of iOS's default 30 fps cap.

## Freeing up space

After setup has finished and the IPA works, you can delete:

- `work/tools/AssetRipper`, `work/tools/steamcmd`.
- `work/build/DerivedData` (about 6 GB). Xcode recreates it, the next build just takes a few minutes longer.
- The Unity installer leftovers in `work/tools`, if any.

Don't delete `work/ripped`. That's the project `./build` uses.

## Updating to a new game version

Get the new game files and run setup again with a fresh work folder:

```
mv work work-old
./setup "/path/to/new/game"
```

Delete `work-old` once the new one builds. Mods may need updating for a new game version too.

## Troubleshooting

**"Unity has no license"** - Open Unity Hub, sign in, and add a free Personal license under Preferences > Licenses.

**"these aren't the Windows game files"** - You gave it the Mac version of the game. Get the Windows version (see Step 3).

**"Patching the game scripts failed"** - Probably a game version the port doesn't know yet. The message says which file. Post it in an issue.

**AssetRipper "can't be opened because Apple cannot check it"** - setup clears that flag itself, but if macOS still blocks it, go to System Settings > Privacy & Security and click "Open Anyway", then run setup again.

**Sideloadly errors** - Usually the Apple ID. Free accounts can only have 3 sideloaded apps at a time. Remove an old one and try again.

**The app opens and closes straight away** - With a free Apple ID, it's been more than 7 days. Sideload it again.

**A mod doesn't work** - Check the Heads up list from `./build` and `BepInEx/LogOutput.log` on the phone. Make sure you have all of its dependencies.

**Running out of space during the build** - The Unity build needs room for its cache. Free up space or move the work folder to a bigger drive (`--work`), then run setup again.

## What's in this repo

| Path | What it is |
|---|---|
| `setup` | One-time setup: game files to Unity project to first IPA |
| `build` | Builds an IPA with the mods in `BepInEx/` |
| `port/` | The port itself: iPhone code, touch controls, editor build scripts, iOS plugins, art. Copied into the Unity project by setup. |
| `tools/patch_game.py` | The small edits made to the game's own scripts (online features off, controller fixes, save location, screen width) |
| `tools/make_addressables_map.py` | Rebuilds the game's asset bundle layout for iOS from the PC game's catalog |
| `tools/gen_shaders.py` | Replaces the empty shaders AssetRipper exports with working ones |
| `tools/catdump`, `tools/bundledump` | Read the PC game's Addressables catalog and bundle contents |
| `tools/make_ipa.sh` | Compiles the Xcode project into an unsigned IPA |
| `modding/` | The mod tool: `Weaver` runs at build time (BepInEx patchers, Harmony hook points), `ModRuntime` runs on the phone (plugin loader, Harmony replacement) |
| `scripts/sim.sh` | Runs a build in the iOS Simulator |
| `BepInEx/` | Put your mods here |

Everything setup and build make goes in `work/` and `output/`, which git ignores.

## Credits and notes

- Hollow Knight: Silksong is made by Team Cherry. Buy it. This repo doesn't contain the game, and IPAs built with it contain your copy of the game, so don't share them.
- [AssetRipper](https://github.com/AssetRipper/AssetRipper), [AddressablesTools](https://github.com/nesrak1/AddressablesTools) and [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET) do the heavy lifting for reading the game.
- `modding/vendor/core` contains [BepInEx](https://github.com/BepInEx/BepInEx) 5.4.23.2 (LGPL-2.1), [HarmonyX](https://github.com/BepInEx/HarmonyX) (MIT), [Mono.Cecil](https://github.com/jbevain/cecil) (MIT) and [MonoMod](https://github.com/MonoMod/MonoMod) (MIT), unchanged.
- The free version of AssetRipper doesn't export shaders, so the ones here are approximations. A few effects (water, blur, heat haze) look simpler than on PC.

Port by FaarisPorts.

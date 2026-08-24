# microblock's QoL Utils

[简体中文](README.md)

A Windows-first QoL utility mod for Celeste + Everest. The portable font, icon,
and UI rasterizer can also be built for Linux and macOS. MiaoNet, CollabUtils2,
and SpeedrunTool are optional runtime integrations rather than hard dependencies.

## Features

### UI and system helpers

- Material You-style HUD cards, settings pages, chapter select, and Everest mod
  options. HUD surfaces, acrylic blur, and the global mod-options replacement
  can be toggled independently.
- A custom QOL settings page with category navigation, setting search, mouse,
  keyboard, and controller input. Everest's binding pages and custom setting
  entries keep their original behavior.
- An opt-in replacement chapter browser with recent chapters, level-set
  navigation, search, author/description/tag metadata, and keyboard, controller,
  mouse, and wheel input. It enters chapters through the normal OuiChapterPanel
  flow and honors the official unlock limits.
- Reflection-only CollabUtils2 support. Collab maps and gyms are hidden by
  default, can be exposed by an advanced option, and are grouped into collapsible
  lobby sections when possible.
- Embedded Material Symbols and a portable font renderer. Text is rasterized at
  its physical output size and cached; the default font is Microsoft YaHei UI,
  with a chooser for installed font families.
- Windows HiDPI support and optional input-language switching: text fields use a
  Chinese keyboard layout while normal gameplay uses an English layout.
- Optional removal of room transitions and death animation. Collision boxes have
  hidden, overlaid, and collision-only modes.
- A Microblock QOL Tools entry is added to the pause menu.

### HUD and minimap

- Rolling FPS, CPU frame time, and—when Motion Smoothing is available—separate
  physics and render FPS.
- Optional frame-spike notices and a lightweight frame-profiler HUD.
- A circular or square minimap rendered from the live solid-tile grid:
  - configurable size, zoom, and keyboard/controller zoom bindings;
  - room bounds, room backgrounds, opacity, and adaptive colors;
  - cached shortest routes and remaining-room count to the map's inferred goal;
  - strawberry, golden strawberry, moonberry, heart, cassette, key, and gem markers;
  - persistent collection state from the current save, with optional edge markers
    for strawberries in nearby rooms.
- Optional MiaoNet players, avatars, off-screen players, and names on the minimap.
  Names can be hidden, limited to watched players, or shown for everyone.
- Optional suppression of MiaoNet's native off-screen name labels.

### MiaoNet watching and notifications

When MiaoNet is present, the mod reads same-map player positions, rooms, names,
and avatars through reflection. If MiaoNet is absent, the mod still loads normally.

Everest console commands:

~~~text
qol_watch <player>
qol_unwatch <player>
qol_watch_list
~~~

The MiaoNet chat box also registers /qol (alias /mu):

~~~text
/qol watch <player>
/qol unwatch <player>
/qol list
~~~

On Windows, a system notification is shown when a watched player changes rooms
while Celeste is not the foreground application.

### Recording and death replays

Recording requires the Windows native backend. It captures the game window through
WGC/scap, does not launch ffmpeg.exe, and does not use a managed frame buffer or
subprocess.

- Automatic recording can cover every room or only runs carrying a golden berry.
- Manual recording can be started, saved, and discarded from the settings page or
  through console commands.
- Full recordings and death replays use independent capture sessions. Death
  replays retain the latest 30 seconds by default, configurable from 10 to 60
  seconds, save after death, and resume automatically after respawn.
- Continuous capture keeps only successful segments. Deaths, room transitions,
  pauses, SpeedrunTool loads, and custom respawn-point changes affect the final
  edit list without putting failed gameplay into the final video.
- On area completion, the native finalizer reads only retained ranges and produces
  a gapless MP4. The in-game recording library shows finalization progress and
  supports separate full/death views, opening, and deletion.
- The default output root is
  %USERPROFILE%\Videos\Celeste\microblocks-qol-recordings. It can be changed in
  settings. Completed files are stored under full/<area> and deaths/<area>;
  each MP4 also has a .timeline.json sidecar.
- Video uses H.264 and audio uses AAC. Frame rate, bitrate, encoder preference,
  UI SFX capture, and retention limits are configurable.
- FMOD DSP taps capture gameplay_sfx, music, and optional ui_sfx. Chunks are
  streamed to an .sfxchunks sidecar and mixed during finalization against the
  same video timeline instead of buffering an entire run in memory.
- BGM can use the captured game mix or SfxOnlyWithPostMix. The latter splits
  music at event, loop, seek, and other timeline discontinuities; a clean mapped
  file replaces only its matching event segment.

BgmEventMapFile is an optional JSON object. Relative paths are resolved from the
directory containing the JSON file:

~~~json
{
  "event:/music/lvl1/main": "D:/Celeste-BGM/first_steps.flac"
}
~~~

Without a mapping, captured music is still cut with the same death, respawn-point,
and SpeedrunTool edit list.

### Profiler

The Profiler page can start a 10-second in-process EventPipe stack sample:

- update and render are reported separately;
- entries show exclusive CPU time, percentage, owning mod assembly, and MonoMod
  hook target;
- a Mod-only simple view and a full professional view are available;
- CSV and .nettrace reports are written to
  %LOCALAPPDATA%\MicroblocksQolUtils\profiles;
- the lightweight frame-time HUD remains available without a full sample.

## Console commands

In addition to the watching commands above, recording and native capture expose:

~~~text
qol_capture_probe_start
qol_capture_probe_stats
qol_capture_probe_stop

qol_record_start
qol_record_save
qol_record_discard
qol_record_status
~~~

The capture probe is for development diagnostics: it reports WGC/scap capture,
queue depth, dropped frames, and media time without enabling normal recording.

## Build and install

The default Celeste path is:

~~~text
C:\SteamLibrary\steamapps\common\Celeste
~~~

The build entry point requires Node.js, the .NET 8 SDK, Rust, and the native
toolchain for the selected target. A complete Windows recording build additionally
requires:

- Visual Studio C++ Build Tools;
- LLVM/Clang, including libclang.dll and clang-cl.exe;
- MSYS2 with GNU make, Perl, and NASM;
- tar.

The repository script builds the managed mod, native rasterizer, Windows
capture/recording backend, and writes Build plus MicroblocksQolUtils.zip:

~~~powershell
node scripts/build-qol-mod.mjs
~~~

Build and install to the default Celeste installation:

~~~powershell
node scripts/build-qol-mod.mjs --install
~~~

Set CELESTE_ROOT when Celeste is installed elsewhere. Close the game first; the
script refuses to replace DLLs loaded by a running Celeste process:

~~~powershell
$env:CELESTE_ROOT = "D:\Games\Celeste"
node scripts/build-qol-mod.mjs --install
~~~

Targets used by CI can be selected explicitly:

~~~powershell
node scripts/build-qol-mod.mjs --target x86_64-pc-windows-msvc
node scripts/build-qol-mod.mjs --target x86_64-unknown-linux-musl --skip-parity
node scripts/build-qol-mod.mjs --target x86_64-apple-darwin --skip-parity
~~~

Linux and macOS packages include the portable font/icon rasterizer, while
Windows-only capture, recording, and Windows notifications report themselves as
unavailable. The Windows build downloads and verifies the FFmpeg 8.1 source, then
builds a minimal LGPL shared runtime containing only the codecs and formats used
by this project; it never packages or invokes ffmpeg.exe.

## Development checks

SkiaSharp is used only by the development parity harness. It is not referenced by
the managed mod and is not copied into the final package:

~~~powershell
node scripts/verify-skia-parity.mjs
node scripts/verify-skia-gpu-parity.mjs
~~~

GitHub Actions runs Rust formatting/tests and builds Windows x64, Linux x64 musl,
and macOS x64 packages. Every commit pushed to master updates the nightly
pre-release and its three platform archives; tags matching v* publish the same
archives as a versioned release.

## Dependency notes

- MiaoNet, CollabUtils2, and SpeedrunTool are optional runtime bridges.
- Material Symbols are embedded in the repository.
- third_party/scap is pinned and locally patched.
- SkiaSharp is a development-only dependency and is not part of the mod runtime.

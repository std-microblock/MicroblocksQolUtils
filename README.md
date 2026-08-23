# microblock's QoL Utils

Windows-first Everest utility mod. Optional integrations are detected at
runtime; MiaoNet+ and SpeedrunTool are not hard dependencies.

Implemented:

- HUD entity and settings model.
- Direct Windows TTF/OTF glyph rasterization with a bounded, lazy GPU cache.
- Material You surfaces for the HUD and chapter browser, using the selected
  chapter's accent color and the same direct system-font renderer.
- Toggleable GPU acrylic rendering for the custom chapter browser. The
  Overworld is rendered into bounded full-screen targets, blurred with
  Celeste's own Gaussian blur shader, and composited behind translucent cards.
- An opt-in replacement chapter browser with keyboard, controller, mouse,
  wheel, and level-set navigation. It honors vanilla Celeste unlock limits and
  routes selections through the normal `OuiChapterPanel` launch flow.
- Optional CollabUtils2 `LobbyHelper` interop. Lobby entries stay visible while
  hidden Collab maps and gyms are omitted by default; an advanced setting can
  expose them for direct selection without making CollabUtils2 a dependency.
  Multi-lobby collabs are split into collapsible lobby sections, with the lobby
  card kept visible while its maps are folded.
- Rolling FPS display.
- Persistent watched-player list and Everest console commands.
- Circular/square current-room minimap rendered from the live solid-tile grid,
  with collectible markers, persistent strawberry collection status, optional
  edge markers for strawberries in adjacent rooms, and highlighted shortest-route rooms.
- Cached room-graph shortest distance and route to the heart/end room.
- Optional reflection-only MiaoNet player positions, avatars, names and map count.
- `/qol watch`, `/qol unwatch` and `/qol list` inside MiaoNet's own chat box
  (plus `qol_watch`, `qol_unwatch`, `qol_watch_list` in the Everest console).
- Background Windows balloon notifications when a watched player changes rooms.
- Optional suppression of MiaoNet's off-screen name labels.
- Near-instant room transitions (camera/player/light interpolation removed).
- Optional instant respawns that skip the death animation and death screen wipe.
- Opt-in frame-spike sampling grouped by owning assembly for entity Update and
  Render, with an on-screen top offender and CSV logs under LocalAppData.
- A Rust `cdylib` capture backend built directly on `scap`/WGC. Captured BGRA
  frames use an intentional CPU copy, stay outside managed memory, and pass
  through a fixed-capacity latest-frame queue; a slow encoder cannot grow
  memory without bound.
- Streaming H.264 encoding through FFmpeg shared libraries, with automatic
  NVENC, QSV, AMF, Media Foundation, then OpenH264 fallback. No `ffmpeg.exe`,
  `gdigrab`, managed frame buffer, or subprocess is used.
- One WGC/encoder session remains alive for the whole room. Deaths,
  SpeedrunTool loads, and respawn changes only move logical start/end markers;
  they never restart capture or grow an in-memory recording buffer.
- A top-right recording badge can independently show a blinking red capture
  dot and the elapsed recording time.
- Native background finalization decodes only the retained ranges from the
  continuous room file and re-encodes them into a gapless MP4. This permits
  exact non-keyframe cuts while failed attempts and load freezes are omitted.
- Completed recordings are pruned oldest-first at startup and after finalization;
  the recording settings can change the retention count, disable the limit,
  or run cleanup immediately.
- Pass-through FMOD DSP taps capture `bus:/gameplay_sfx` and optionally
  `bus:/ui_sfx`, while deliberately excluding `bus:/music`. Mixer callbacks
  feed a fixed pool of 32 native PCM chunks with non-blocking `try_lock`
  semantics, and a writer thread streams them to a timestamped `.sfxchunks`
  sidecar instead of buffering room audio in managed or native memory. Exact
  zero-filled idle blocks are represented as timestamp gaps rather than stored.
- Timeline cuts for SpeedrunTool save/load and respawn-point triggers. A saved
  prefix is trimmed at its exact timestamp, so deaths and load freezes are not
  included in the final video.
- The finalizer applies those same retained ranges to both SFX buses, mixes
  overlapping gameplay/UI chunks at their timestamps, fills sparse gaps with
  silence, streams the result through FFmpeg's native AAC encoder, then remuxes
  H.264 + AAC into the completed MP4 without launching an executable.
- In `SfxOnlyWithPostMix` mode, each retained segment also stores its FMOD
  music event and timeline position. Event changes, loops, seeks, and other
  timeline discontinuities split only the logical edit list; the finalizer
  decodes the mapped clean BGM file, seeks to each saved position, resamples it
  to the captured SFX format, and mixes it before AAC encoding.

Planned/in progress:

- Broader built-in BGM event-map presets; custom maps already work.

## Recorder setup

The native capture bridge selects the window configured by
`RecordingWindowTitle` (default `Celeste`). During development, the Everest
commands `qol_capture_probe_start`, `qol_capture_probe_stats`, and
`qol_capture_probe_stop` exercise scap/WGC without enabling automatic
recording.

`scripts/build-qol-mod.mjs` downloads the current FFmpeg 8.1 LGPL shared build
from BtbN, verifies GitHub's SHA-256 digest, links the Rust encoder against its
import libraries, and packages only the required DLLs and license beside the
mod's native DLL. The FFmpeg executable in the development archive is not
packaged or invoked.

For automatic BGM reconstruction, `BgmEventMapFile` points to a JSON object
that resolves FMOD event paths to clean music files. Relative paths are
resolved against the JSON file's directory, for example:

```json
{
  "event:/music/lvl1/main": "D:/Celeste-BGM/first_steps.flac"
}
```

The music bus itself is never captured, so deaths cannot bake an interrupted or
restarted BGM track into the continuous room recording.

## Build and install

The standalone repository now contains the complete native crate, its pinned
and patched `scap` dependency, and the packaging scripts that previously lived
in `celeste-next-gym`.

Install Rust (MSVC toolchain), the .NET 8 SDK, Node.js, and LLVM/Clang, then run:

```powershell
node scripts/build-qol-mod.mjs
```

The packaged Everest mod is written to `Build`. Managed references default to
`C:\SteamLibrary\steamapps\common\Celeste`; set `CELESTE_ROOT` if the game is
elsewhere. Build and install in one step with:

```powershell
node scripts/build-qol-mod.mjs --install
```

Close Celeste before using `--install`; the script refuses to replace loaded
DLLs while the game is running.

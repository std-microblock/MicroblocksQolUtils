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
  Multi-lobby collabs are split into collapsible lobby sections; folding a
  section hides all of its chapter cards.
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
- Optional Windows input-language control: text fields use an installed Chinese
  keyboard layout, while normal gameplay stays locked to an English layout.
- Near-instant room transitions (camera/player/light interpolation removed).
- Optional instant respawns that skip the death animation and death screen wipe.
- Three-state collision-box rendering: hidden, overlaid on gameplay, or collision boxes only.
- A self-drawn Profiler settings tab can launch a 10-second in-process EventPipe
  stack sample, split hot methods between Engine Update and Render, identify
  owning mod assemblies and MonoMod hook targets, and retain CSV plus `.nettrace`
  reports under LocalAppData. Reports use exclusive/self time, provide a simple
  Mod-only view plus a full professional view, and scroll through the extended
  result list. The lightweight frame-spike HUD remains available.
- A Rust `cdylib` capture backend built directly on `scap`/WGC. Captured BGRA
  frames use an intentional CPU copy, stay outside managed memory, and pass
  through a fixed-capacity latest-frame queue; a slow encoder cannot grow
  memory without bound.
- Streaming H.264 encoding through FFmpeg shared libraries, with automatic
  NVENC, QSV, AMF, Media Foundation, then OpenH264 fallback. No `ffmpeg.exe`,
  `gdigrab`, managed frame buffer, or subprocess is used.
- Full-run/manual recording and death replay use independent WGC/encoder
  sessions, so a background death-replay buffer never occupies the manual
  recording controls. Full-run recording stays alive for the complete area,
  including every room transition. Deaths, SpeedrunTool loads, and respawn
  changes only move its logical start/end markers and never grow an in-memory
  frame buffer. Death-replay capture rotates after a death so the replay can be
  finalized immediately, then resumes automatically after respawn.
- A top-right recording badge can independently show a blinking red capture
  dot and the elapsed recording time.
- Native background finalization decodes only the retained ranges from the
  continuous run file and re-encodes them into one gapless MP4 at area
  completion. The recording library displays progress directly on the output
  file row and disables playback/deletion until generation finishes. This permits
  exact non-keyframe cuts while failed attempts and load freezes are omitted.
- Completed recordings are pruned oldest-first at startup and after finalization;
  the recording settings can change the retention count, disable the limit,
  or run cleanup immediately.
- Optional death replays keep their own recording session active during gameplay
  and retain only the most recent rolling window (30 seconds by default,
  configurable from 10 to 60 seconds). The death capture is stopped and queued
  for saving after the game update finishes, then automatically resumes after
  respawn. Death replays have their own retention limit (30 files by default).
- The recording library can switch between recent deaths and complete videos.
  New files are separated under `deaths/<area>` and `full/<area>` inside the
  configured recording directory; recordings created by older versions remain
  visible as complete videos.
- Pass-through FMOD DSP taps capture `bus:/gameplay_sfx`, `bus:/music`, and
  optionally `bus:/ui_sfx`. Mixer callbacks feed a fixed pool of 32 native PCM
  chunks with non-blocking `try_lock`
  semantics, and a writer thread streams them to a timestamped `.sfxchunks`
  sidecar instead of buffering run audio in managed or native memory. Exact
  zero-filled idle blocks are represented as timestamp gaps rather than stored.
- Timeline cuts for SpeedrunTool save/load and respawn-point triggers. Each
  room transition becomes the next death-reset anchor only after its animation
  has fully finished; custom respawn-point changes and SpeedrunTool snapshots
  preserve the exact successful prefix, so deaths and load freezes are not
  included in the final video.
- The finalizer applies those same retained ranges to gameplay, UI, and music
  buses, mixes overlapping chunks at their timestamps, fills sparse gaps with
  silence, streams the result through FFmpeg's native AAC encoder, then remuxes
  H.264 + AAC into the completed MP4 without launching an executable.
- Every retained segment stores its FMOD music event and timeline position.
  The captured music bus is automatically cut with the same successful-run
  timeline, so the completed video always keeps its BGM. In
  `SfxOnlyWithPostMix` mode, event changes, loops, seeks, and other timeline
  discontinuities additionally split the logical edit list; when an event has
  a configured clean BGM mapping, the finalizer replaces that segment's
  captured music with the mapped file at the saved timeline position before
  AAC encoding.

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

Normal recording does not require a BGM mapping file: the captured music bus is
automatically cut and joined with the successful-run timeline. As an optional
advanced override, `BgmEventMapFile` can point to a JSON object that replaces
specific FMOD events with clean music files. Relative paths are resolved
against the JSON file's directory, for example:

```json
{
  "event:/music/lvl1/main": "D:/Celeste-BGM/first_steps.flac"
}
```

When no clean mapping exists, the captured music bus is trimmed by the same
death, respawn-trigger, and SpeedrunTool edit list as video and SFX. A mapped
event replaces only its own captured segment, avoiding doubled music.

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

# Native capture backend

This crate builds `microblocks_qol_native.dll`, the Windows capture, FFmpeg
encoding, audio-sidecar, and recording-finalization backend used by the managed
Everest mod.

Use the repository build entry point rather than invoking Cargo directly:

```powershell
node scripts/build-qol-mod.mjs
```

Pass `--install` to replace the copy under the configured Celeste `Mods`
directory. Set `CELESTE_ROOT` when Celeste is not installed at the repository's
default path. Close Celeste first so its loaded DLLs can be replaced safely.

The build script downloads and verifies the official FFmpeg 8.1 source archive,
builds a minimal LGPL shared runtime with the Media Foundation H.264 encoder,
configures `ffmpeg-next`, builds this crate with the `ffmpeg` feature, and places
the native DLL and its five required FFmpeg DLLs in `Build/Code`.

Requirements:

- Rust with the MSVC Windows target
- .NET 8 SDK
- Visual Studio C++ Build Tools
- LLVM/Clang (`libclang.dll` and `clang-cl.exe`), either on `PATH` or through
  `LIBCLANG_PATH`
- MSYS2 with GNU make and `mingw-w64-x86_64-nasm`
- `tar`, used to unpack the verified FFmpeg archive

`third_party/scap` is the pinned, locally patched capture dependency. See its
`PATCHES.md` before updating it.

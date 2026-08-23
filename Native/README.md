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

The build script downloads and verifies the FFmpeg 8.1 LGPL shared development
archive, configures `ffmpeg-next`, builds this crate with the `ffmpeg` feature,
and places the native DLL and its required FFmpeg DLLs in `Build/Code`.

Requirements:

- Rust with the MSVC Windows target
- .NET 8 SDK
- LLVM/Clang (`libclang.dll`), either on `PATH` or through `LIBCLANG_PATH`
- `tar`, used to unpack the verified FFmpeg archive

`third_party/scap` is the pinned, locally patched capture dependency. See its
`PATCHES.md` before updating it.

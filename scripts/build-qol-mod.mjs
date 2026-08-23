import { cpSync, existsSync, mkdirSync, rmSync } from "node:fs";
import { basename, dirname, resolve } from "node:path";
import { spawnSync } from "node:child_process";
import { ensureQolFfmpeg, findLibclangDirectory } from "./qol-ffmpeg.mjs";

const root = resolve(import.meta.dirname, "..");
const output = resolve(root, "Build");
const managedOutput = resolve(root, "Source/bin/Release/net8.0");
const dll = resolve(managedOutput, "MicroblocksQolUtils.dll");
const celesteRoot = resolve(process.env.CELESTE_ROOT ?? "C:/SteamLibrary/steamapps/common/Celeste");
const nativeName = process.platform === "win32"
  ? "microblocks_qol_native.dll"
  : process.platform === "darwin"
    ? "libmicroblocks_qol_native.dylib"
    : "libmicroblocks_qol_native.so";

const run = (command, args, env = process.env) => {
  const result = spawnSync(command, args, {
    cwd: root,
    stdio: "inherit",
    shell: false,
    env,
  });
  if (result.status !== 0) process.exit(result.status ?? 1);
};

const ffmpeg = await ensureQolFfmpeg(root);
const nativeEnv = ffmpeg
  ? {
      ...process.env,
      FFMPEG_DIR: ffmpeg.root,
      LIBCLANG_PATH: findLibclangDirectory(),
      PATH: `${ffmpeg.bin};${process.env.PATH ?? ""}`,
    }
  : process.env;
run("dotnet", [
  "run",
  "--project",
  resolve(root, "Tools/SkiaParity/SkiaParity.csproj"),
  "-c",
  "Release",
  "--",
  resolve(root, ".work/skia-parity"),
]);
run(
  "cargo",
  ["build", "-q", "-p", "microblocks-qol-native", "--release", "--features", "ffmpeg"],
  nativeEnv,
);
run("dotnet", ["build", resolve(root, "Source/MicroblocksQolUtils.csproj"), "-c", "Release"]);
rmSync(output, { recursive: true, force: true });
mkdirSync(resolve(output, "Code"), { recursive: true });
cpSync(dll, resolve(output, "Code/MicroblocksQolUtils.dll"));
for (const dependency of [
  "Dia2Lib.dll",
  "Microsoft.Diagnostics.FastSerialization.dll",
  "Microsoft.Diagnostics.NETCore.Client.dll",
  "Microsoft.Diagnostics.Tracing.TraceEvent.dll",
  "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
  "Microsoft.Extensions.Logging.Abstractions.dll",
  "SkiaSharp.dll",
  "System.Collections.Immutable.dll",
  "System.IO.Pipelines.dll",
  "System.Reflection.Metadata.dll",
  "System.Text.Encodings.Web.dll",
  "System.Text.Json.dll",
  "TraceReloggerLib.dll",
]) {
  const source = resolve(managedOutput, dependency);
  if (existsSync(source)) cpSync(source, resolve(output, "Code", dependency));
}
const skiaNative = resolve(managedOutput, "runtimes/win-x64/native/libSkiaSharp.dll");
if (!existsSync(skiaNative)) throw new Error(`Skia native runtime was not found at ${skiaNative}`);
cpSync(skiaNative, resolve(output, "Code/libSkiaSharp.dll"));
cpSync(resolve(root, "third_party/skiasharp/LICENSE.txt"),
  resolve(output, "Code/SkiaSharp-LICENSE.txt"));
cpSync(resolve(root, "target", "release", nativeName), resolve(output, "Code", nativeName));
if (ffmpeg) {
  for (const dependency of ffmpeg.dlls) {
    cpSync(dependency, resolve(output, "Code", dependency.split(/[\\/]/u).at(-1)));
  }
  cpSync(ffmpeg.license, resolve(output, "Code", "FFmpeg-LICENSE.txt"));
}
for (const path of ["everest.yaml", "Dialog", "Graphics", "Native/README.md"]) {
  const source = resolve(root, path);
  if (!existsSync(source)) continue;
  const target = resolve(output, path);
  mkdirSync(dirname(target), { recursive: true });
  cpSync(source, target, { recursive: true });
}
console.log(`Built ${output}`);

if (process.argv.includes("--install")) {
  if (!existsSync(resolve(celesteRoot, "Celeste.dll"))) {
    throw new Error(`Celeste.dll was not found under ${celesteRoot}`);
  }
  if (process.platform === "win32") {
    const running = spawnSync(
      "tasklist.exe",
      ["/FI", "IMAGENAME eq Celeste.exe", "/FO", "CSV", "/NH"],
      { encoding: "utf8", windowsHide: true },
    );
    if (running.status === 0 && running.stdout.toLowerCase().includes('"celeste.exe"')) {
      throw new Error("Celeste is running; close it before replacing the installed mod");
    }
  }
  const modsRoot = resolve(celesteRoot, "Mods");
  const installed = resolve(modsRoot, "MicroblocksQolUtils");
  if (dirname(installed) !== modsRoot || basename(installed) !== "MicroblocksQolUtils") {
    throw new Error(`Refusing to replace unexpected install target ${installed}`);
  }
  mkdirSync(modsRoot, { recursive: true });
  rmSync(installed, { recursive: true, force: true });
  cpSync(output, installed, { recursive: true });
  console.log(`Installed ${installed}`);
}

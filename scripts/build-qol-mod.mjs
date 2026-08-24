import { cpSync, existsSync, mkdirSync, rmSync, statSync } from "node:fs";
import { basename, dirname, resolve } from "node:path";
import { spawnSync } from "node:child_process";
import { ensureQolFfmpeg, findLibclangDirectory } from "./qol-ffmpeg.mjs";

const root = resolve(import.meta.dirname, "..");
const output = resolve(root, "Build");
const argumentValue = (name) => {
  const index = process.argv.indexOf(name);
  if (index < 0) return null;
  const value = process.argv[index + 1];
  if (!value || value.startsWith("--")) throw new Error(`${name} requires a value`);
  return value;
};
const target = argumentValue("--target");
const archive = resolve(root, argumentValue("--archive") ?? "MicroblocksQolUtils.zip");
const skipParity = process.argv.includes("--skip-parity");
const disableFfmpeg = process.argv.includes("--no-ffmpeg");
const maximumArchiveBytes = 10 * 1024 * 1024;
const managedOutput = resolve(root, "Source/bin/Release/net8.0");
const dll = resolve(managedOutput, "MicroblocksQolUtils.dll");
const celesteRoot = resolve(process.env.CELESTE_ROOT ?? "C:/SteamLibrary/steamapps/common/Celeste");
const targetPlatform = target?.includes("windows")
  ? "win32"
  : target?.includes("apple-darwin")
    ? "darwin"
    : target?.includes("linux")
      ? "linux"
      : process.platform;
const nativeName = targetPlatform === "win32"
  ? "microblocks_qol_native.dll"
  : targetPlatform === "darwin"
    ? "libmicroblocks_qol_native.dylib"
    : "libmicroblocks_qol_native.so";
const cargoTargetRoot = resolve(root, process.env.CARGO_TARGET_DIR ?? "target");
const nativeDirectory = resolve(cargoTargetRoot, ...(target ? [target] : []), "release");
const nativeOutput = resolve(nativeDirectory, nativeName);

const run = (command, args, env = process.env) => {
  const result = spawnSync(command, args, {
    cwd: root,
    stdio: "inherit",
    shell: false,
    env,
  });
  if (result.status !== 0) process.exit(result.status ?? 1);
};

const wantsFfmpeg = targetPlatform === "win32" && !disableFfmpeg;
if (wantsFfmpeg && process.platform !== "win32") {
  throw new Error("The FFmpeg-enabled Windows native library must be built on Windows");
}
const ffmpeg = wantsFfmpeg ? await ensureQolFfmpeg(root) : null;
const nativeEnv = ffmpeg
  ? {
      ...process.env,
      FFMPEG_DIR: ffmpeg.root,
      LIBCLANG_PATH: findLibclangDirectory(),
      PATH: `${ffmpeg.bin};${process.env.PATH ?? ""}`,
    }
  : process.env;
const cargoArguments = ["build", "-q", "-p", "microblocks-qol-native", "--release", "--locked"];
if (target) cargoArguments.push("--target", target);
if (ffmpeg) cargoArguments.push("--features", "ffmpeg");
run(
  "cargo",
  cargoArguments,
  nativeEnv,
);
if (target?.endsWith("-unknown-linux-musl") && !existsSync(nativeOutput)) {
  const staticLibrary = resolve(nativeDirectory, "libmicroblocks_qol_native.a");
  if (!existsSync(staticLibrary)) {
    throw new Error(`The musl static library was not produced at ${staticLibrary}`);
  }
  run(process.env.MUSL_CC ?? "musl-gcc", [
    "-shared",
    "-Wl,--gc-sections",
    "-Wl,-soname,libmicroblocks_qol_native.so",
    "-o",
    nativeOutput,
    "-Wl,--whole-archive",
    staticLibrary,
    "-Wl,--no-whole-archive",
    "-ldl",
    "-lpthread",
    "-lm",
  ], nativeEnv);
}
if (!existsSync(nativeOutput)) {
  throw new Error(`The native library was not produced at ${nativeOutput}`);
}
if (!skipParity && process.platform === "win32" && targetPlatform === "win32") {
  run("dotnet", [
    "run",
    "--project",
    resolve(root, "Tools/SkiaParity/SkiaParity.csproj"),
    "-c",
    "Release",
    "--",
    resolve(root, ".work/skia-parity"),
  ], { ...nativeEnv, MQOL_NATIVE_LIBRARY: nativeOutput });
} else {
  console.log("Skipped the Windows-only Skia parity harness");
}
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
cpSync(nativeOutput, resolve(output, "Code", nativeName));
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
rmSync(archive, { force: true });
const packaged = targetPlatform === "win32"
  ? spawnSync("tar", ["-a", "-cf", archive, "-C", output, "."], {
      cwd: root,
      stdio: "inherit",
      shell: false,
    })
  : spawnSync("zip", ["-q", "-r", archive, "."], {
      cwd: output,
      stdio: "inherit",
      shell: false,
    });
if (packaged.error) throw packaged.error;
if (packaged.status !== 0) throw new Error("Cannot create the Everest mod archive");
const archiveBytes = statSync(archive).size;
if (archiveBytes >= maximumArchiveBytes) {
  throw new Error(
    `Packaged mod is ${(archiveBytes / 1024 / 1024).toFixed(2)} MiB; expected less than 10 MiB`,
  );
}
console.log(`Packaged ${archive} (${(archiveBytes / 1024 / 1024).toFixed(2)} MiB)`);

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

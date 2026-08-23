import { resolve } from "node:path";
import { spawnSync } from "node:child_process";

const root = resolve(import.meta.dirname, "..");
const celeste = resolve(process.env.CELESTE_ROOT ?? "C:/SteamLibrary/steamapps/common/Celeste");
const native = resolve(celeste, "lib64-win-x64");
const output = resolve(root, ".work", "skia-parity");

const run = (command, args, options = {}) => {
  const result = spawnSync(command, args, { stdio: "inherit", shell: false, ...options });
  if (result.status !== 0) process.exit(result.status ?? 1);
};

run("node", [resolve(root, "scripts/verify-skia-parity.mjs")], { cwd: root });
run("dotnet", [
  "run",
  "--project",
  resolve(root, "Tools/SkiaGpuParity/SkiaGpuParity.csproj"),
  "-c",
  "Release",
  "--",
  output,
], {
  cwd: celeste,
  env: { ...process.env, PATH: `${native};${celeste};${process.env.PATH ?? ""}` },
  windowsHide: true,
});

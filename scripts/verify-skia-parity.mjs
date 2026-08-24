import { resolve } from "node:path";
import { spawnSync } from "node:child_process";

const root = resolve(import.meta.dirname, "..");
const output = resolve(root, ".work", "skia-parity");
const native = spawnSync(
  "cargo",
  ["build", "-q", "-p", "microblocks-qol-native", "--release"],
  { cwd: root, stdio: "inherit", shell: false },
);
if (native.status !== 0) process.exit(native.status ?? 1);
const result = spawnSync(
  "dotnet",
  ["run", "--project", resolve(root, "Tools/SkiaParity/SkiaParity.csproj"), "-c", "Release", "--", output],
  { cwd: root, stdio: "inherit", shell: false },
);
process.exit(result.status ?? 1);

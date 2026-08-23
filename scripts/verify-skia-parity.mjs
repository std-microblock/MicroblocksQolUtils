import { resolve } from "node:path";
import { spawnSync } from "node:child_process";

const root = resolve(import.meta.dirname, "..");
const output = resolve(root, ".work", "skia-parity");
const result = spawnSync(
  "dotnet",
  ["run", "--project", resolve(root, "Tools/SkiaParity/SkiaParity.csproj"), "-c", "Release", "--", output],
  { cwd: root, stdio: "inherit", shell: false },
);
process.exit(result.status ?? 1);

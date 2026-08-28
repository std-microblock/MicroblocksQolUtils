import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readdirSync,
  readFileSync,
  rmSync,
  statSync,
  writeFileSync,
} from "node:fs";
import { dirname, join, relative, resolve, sep } from "node:path";
import { tmpdir } from "node:os";
import { spawnSync } from "node:child_process";

const [outputArgument, ...packageArguments] = process.argv.slice(2);
if (!outputArgument || packageArguments.length === 0) {
  console.error("Usage: node scripts/merge-qol-packages.mjs OUTPUT.zip PLATFORM-PACKAGE.zip...");
  process.exit(2);
}

const output = resolve(outputArgument);
const packages = packageArguments.map((path) => resolve(path));
if (packages.includes(output)) throw new Error("The output archive cannot also be an input archive");
for (const packagePath of packages) {
  if (!existsSync(packagePath) || !statSync(packagePath).isFile()) {
    throw new Error(`Input archive does not exist: ${packagePath}`);
  }
}

const run = (command, args, options = {}) => {
  const result = spawnSync(command, args, { stdio: "inherit", ...options });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${command} failed with status ${result.status}`);
};

const files = new Map();
const canonicalBaseFiles = new Set(["Code/MicroblocksQolUtils.dll"]);
const work = mkdtempSync(join(tmpdir(), "microblocks-qol-merge-"));

const walkFiles = (root) => {
  const result = [];
  const visit = (directory) => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name);
      if (entry.isDirectory()) visit(path);
      else if (entry.isFile()) result.push(path);
      else throw new Error(`Unsupported archive entry type after extraction: ${path}`);
    }
  };
  visit(root);
  return result;
};

try {
  for (const [index, packagePath] of packages.entries()) {
    const extracted = join(work, `package-${index}`);
    mkdirSync(extracted);
    run("unzip", ["-q", packagePath, "-d", extracted]);
    for (const source of walkFiles(extracted)) {
      const name = relative(extracted, source).split(sep).join("/");
      if (!name || name.startsWith("/") || name.split("/").includes("..")) {
        throw new Error(`Unsafe archive entry after extraction: ${name}`);
      }
      const contents = readFileSync(source);
      const previous = files.get(name);
      if (previous && !previous.contents.equals(contents)) {
        if (canonicalBaseFiles.has(name)) continue;
        throw new Error(`Platform packages contain different shared file contents: ${name}`);
      }
      files.set(name, { contents });
    }
  }

  for (const required of [
    "Code/lib-win-x64/microblocks_qol_native.dll",
    "Code/lib-linux/libmicroblocks_qol_native.so",
    "Code/lib-osx/libmicroblocks_qol_native.dylib",
  ]) {
    if (!files.has(required)) throw new Error(`Merged package is missing ${required}`);
  }

  const merged = join(work, "merged");
  for (const [name, { contents }] of [...files.entries()].sort(([a], [b]) => a.localeCompare(b))) {
    const destination = join(merged, ...name.split("/"));
    mkdirSync(dirname(destination), { recursive: true });
    writeFileSync(destination, contents);
  }

  mkdirSync(dirname(output), { recursive: true });
  rmSync(output, { force: true });
  const entries = readdirSync(merged).sort();
  run("zip", ["-q", "-r", output, ...entries], { cwd: merged });
  console.log(`Merged ${packages.length} platform packages into ${output}`);
} finally {
  rmSync(work, { recursive: true, force: true });
}

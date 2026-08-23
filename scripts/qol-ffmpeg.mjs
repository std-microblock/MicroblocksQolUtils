import { createHash } from "node:crypto";
import { spawnSync } from "node:child_process";
import {
  createWriteStream,
  existsSync,
  mkdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { rename } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { Readable } from "node:stream";
import { pipeline } from "node:stream/promises";

const assetName = "ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip";
const extractedName = assetName.slice(0, -4);
const requiredDlls = [
  "avcodec-62.dll",
  "avformat-62.dll",
  "avutil-60.dll",
  "swresample-6.dll",
  "swscale-9.dll",
];

const githubHeaders = {
  Accept: "application/vnd.github+json",
  "User-Agent": "microblocks-qol-build",
  "X-GitHub-Api-Version": "2022-11-28",
};

export async function ensureQolFfmpeg(root) {
  if (process.platform !== "win32") return null;
  const cache = resolve(root, ".cache", "ffmpeg");
  const archive = resolve(cache, assetName);
  const extracted = resolve(cache, extractedName);
  const manifestPath = resolve(cache, "manifest.json");
  mkdirSync(cache, { recursive: true });

  let cached;
  try {
    cached = JSON.parse(readFileSync(manifestPath, "utf8"));
  } catch {
    cached = null;
  }
  const complete = requiredDlls.every((name) => existsSync(resolve(extracted, "bin", name)))
    && existsSync(resolve(extracted, "include", "libavcodec", "avcodec.h"))
    && existsSync(resolve(extracted, "lib", "avcodec.lib"));
  if (complete && cached?.assetId && cached?.digest?.startsWith("sha256:")) {
    return ffmpegLayout(extracted, cached);
  }

  const release = await fetch("https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest", {
    headers: githubHeaders,
  });
  if (!release.ok) throw new Error(`Cannot query BtbN FFmpeg release: HTTP ${release.status}`);
  const metadata = await release.json();
  const asset = metadata.assets?.find((candidate) => candidate.name === assetName);
  if (!asset?.browser_download_url || !asset?.digest?.startsWith("sha256:")) {
    throw new Error(`BtbN release does not contain ${assetName} with a SHA-256 digest`);
  }
  if (complete && cached?.assetId === asset.id && cached?.digest === asset.digest) {
    return ffmpegLayout(extracted, asset);
  }
  if (complete && existsSync(archive) && sha256(archive) === asset.digest) {
    writeManifest(manifestPath, metadata, asset);
    return ffmpegLayout(extracted, asset);
  }

  const response = await fetch(asset.browser_download_url, { redirect: "follow" });
  if (!response.ok || !response.body) {
    throw new Error(`Cannot download ${assetName}: HTTP ${response.status}`);
  }
  const temporary = `${archive}.download`;
  rmSync(temporary, { force: true });
  await pipeline(Readable.fromWeb(response.body), createWriteStream(temporary));
  const digest = sha256(temporary);
  if (digest !== asset.digest) {
    rmSync(temporary, { force: true });
    throw new Error(`FFmpeg archive digest mismatch: expected ${asset.digest}, got ${digest}`);
  }
  rmSync(archive, { force: true });
  rmSync(extracted, { recursive: true, force: true });
  await rename(temporary, archive);
  const unpack = spawnSync("tar", ["-xf", archive, "-C", cache], { stdio: "inherit" });
  if (unpack.status !== 0) throw new Error(`Cannot extract ${assetName}`);
  writeManifest(manifestPath, metadata, asset);
  return ffmpegLayout(extracted, asset);
}

export function findLibclangDirectory() {
  if (process.env.LIBCLANG_PATH) return process.env.LIBCLANG_PATH;
  for (const executable of ["libclang.dll", "clang.exe"]) {
    const where = spawnSync("where.exe", [executable], { encoding: "utf8" });
    if (where.status !== 0) continue;
    const first = where.stdout.split(/\r?\n/u).find(Boolean);
    if (first) return dirname(first.trim());
  }
  throw new Error(
    "libclang.dll is required to build the FFmpeg Rust bindings; install LLVM or set LIBCLANG_PATH",
  );
}

function ffmpegLayout(root, asset) {
  return {
    root,
    bin: resolve(root, "bin"),
    license: resolve(root, "LICENSE.txt"),
    dlls: requiredDlls.map((name) => resolve(root, "bin", name)),
    digest: asset.digest,
  };
}

function sha256(path) {
  return `sha256:${createHash("sha256").update(readFileSync(path)).digest("hex")}`;
}

function writeManifest(path, release, asset) {
  writeFileSync(
    path,
    `${JSON.stringify({ assetId: asset.id, digest: asset.digest, publishedAt: release.published_at }, null, 2)}\n`,
    "utf8",
  );
}

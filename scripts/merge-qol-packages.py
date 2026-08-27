#!/usr/bin/env python3
from __future__ import annotations

import argparse
import os
from pathlib import Path, PurePosixPath
import sys
from zipfile import BadZipFile, ZIP_DEFLATED, ZipFile, ZipInfo


REQUIRED_NATIVE_LIBRARIES = (
    "Code/lib-win-x64/microblocks_qol_native.dll",
    "Code/lib-linux/libmicroblocks_qol_native.so",
    "Code/lib-osx/libmicroblocks_qol_native.dylib",
)
CANONICAL_BASE_FILES = {"Code/MicroblocksQolUtils.dll"}


def archive_path(name: str) -> str:
    normalized = name.replace("\\", "/").lstrip("/")
    path = PurePosixPath(normalized)
    if not normalized or path.is_absolute() or ".." in path.parts:
        raise ValueError(f"unsafe archive entry: {name}")
    return path.as_posix()


def merge_packages(output: Path, packages: list[Path]) -> None:
    files: dict[str, bytes] = {}
    origins: dict[str, Path] = {}

    for package in packages:
        with ZipFile(package, "r") as archive:
            for entry in archive.infolist():
                if entry.is_dir():
                    continue
                name = archive_path(entry.filename)
                contents = archive.read(entry)
                if name in files and files[name] != contents:
                    if name in CANONICAL_BASE_FILES:
                        continue
                    raise ValueError(
                        f"platform packages contain different shared file contents: {name} "
                        f"({origins[name].name} and {package.name})"
                    )
                files[name] = contents
                origins.setdefault(name, package)

    missing = [name for name in REQUIRED_NATIVE_LIBRARIES if name not in files]
    if missing:
        raise ValueError(f"merged package is missing: {', '.join(missing)}")

    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_name(output.name + ".tmp")
    temporary.unlink(missing_ok=True)
    try:
        with ZipFile(temporary, "w", compression=ZIP_DEFLATED, compresslevel=9) as archive:
            for name in sorted(files):
                entry = ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
                entry.compress_type = ZIP_DEFLATED
                entry.external_attr = 0o100644 << 16
                archive.writestr(entry, files[name], compresslevel=9)
        os.replace(temporary, output)
    finally:
        temporary.unlink(missing_ok=True)

    print(f"Merged {len(packages)} platform packages into {output}")


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Merge platform-specific MicroblocksQolUtils packages into one archive."
    )
    parser.add_argument("output", type=Path)
    parser.add_argument("packages", nargs="+", type=Path)
    arguments = parser.parse_args()

    output = arguments.output.resolve()
    packages = [package.resolve() for package in arguments.packages]
    if output in packages:
        parser.error("the output archive cannot also be an input archive")
    for package in packages:
        if not package.is_file():
            parser.error(f"input archive does not exist: {package}")

    try:
        merge_packages(output, packages)
    except (BadZipFile, OSError, ValueError) as error:
        print(error, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

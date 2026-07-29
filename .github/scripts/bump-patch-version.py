#!/usr/bin/env python3
"""Increment BOCCHI's patch version and keep repo.json in sync."""

from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[2]
PROJECT_PATH = ROOT / "BOCCHI" / "BOCCHI.csproj"
REPOSITORY_PATH = ROOT / "repo.json"


def replace_once(content: bytes, pattern: bytes, replacement: bytes, label: str) -> bytes:
    updated, count = re.subn(pattern, replacement, content)
    if count != 1:
        raise RuntimeError(f"Expected exactly one {label}, found {count}")
    return updated


project = PROJECT_PATH.read_bytes()
match = re.search(rb"<Version>(\d+)\.(\d+)\.(\d+)</Version>", project)
if match is None:
    raise RuntimeError(f"Could not find a three-part Version in {PROJECT_PATH}")

major, minor, patch = (int(part) for part in match.groups())
current_version = f"{major}.{minor}.{patch}"
next_version = f"{major}.{minor}.{patch + 1}"

project = replace_once(
    project,
    rb"<Version>" + re.escape(current_version.encode()) + rb"</Version>",
    f"<Version>{next_version}</Version>".encode(),
    "project Version",
)

repository = REPOSITORY_PATH.read_bytes()
for field in ("AssemblyVersion", "TestingAssemblyVersion"):
    repository = replace_once(
        repository,
        rb'("' + field.encode() + rb'"\s*:\s*")'
        + re.escape(f"{current_version}.0".encode())
        + rb'(")',
        rb"\g<1>" + f"{next_version}.0".encode() + rb"\g<2>",
        f"repo.json {field}",
    )

PROJECT_PATH.write_bytes(project)
REPOSITORY_PATH.write_bytes(repository)
print(next_version)

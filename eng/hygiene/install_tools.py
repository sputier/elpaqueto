#!/usr/bin/env python3
"""Download the pinned third-party tools used by the hygiene checks and the
Docker workflow (image vulnerability scan).

Each archive is downloaded from the tool's GitHub release, verified against
the SHA-256 recorded below (taken from the release's checksum file), then
extracted to ``eng/hygiene/.tools/``. A mismatch aborts without extracting.

Supported platforms: Linux x64 and Windows x64.
To update a tool, change its version and both checksums in the same commit.
"""

from __future__ import annotations

import hashlib
import io
import platform
import stat
import sys
import tarfile
import time
import urllib.request
import zipfile
from pathlib import Path

TOOLS_DIR = Path(__file__).resolve().parent / ".tools"

TOOLS = {
    "actionlint": {
        "version": "1.7.12",
        "url": "https://github.com/rhysd/actionlint/releases/download/v{version}/actionlint_{version}_{asset}",
        "assets": {
            "linux-x64": ("linux_amd64.tar.gz", "8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8"),
            "windows-x64": ("windows_amd64.zip", "6e7241b51e6817ea6a047693d8e6fed13b31819c9a0dd6c5a726e1592d22f6e9"),
        },
    },
    "gitleaks": {
        "version": "8.30.1",
        "url": "https://github.com/gitleaks/gitleaks/releases/download/v{version}/gitleaks_{version}_{asset}",
        "assets": {
            "linux-x64": ("linux_x64.tar.gz", "551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb"),
            "windows-x64": ("windows_x64.zip", "d29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e"),
        },
    },
    "trivy": {
        "version": "0.76.1",
        "url": "https://github.com/aquasecurity/trivy/releases/download/v{version}/trivy_{version}_{asset}",
        "assets": {
            "linux-x64": ("Linux-64bit.tar.gz", "c7cbe3137fb8a48a17574bede3dd6beb16742d1e9772929940fc90082c39d9c0"),
            "windows-x64": ("windows-64bit.zip", "1051f49cb22d5284f43425f72aad9cda63ca12ceb92e7caa06903490c82dc9bf"),
        },
    },
}


def current_platform() -> str:
    system = platform.system().lower()
    machine = platform.machine().lower()
    if machine not in ("x86_64", "amd64"):
        raise SystemExit(f"unsupported architecture: {machine}")
    if system == "linux":
        return "linux-x64"
    if system == "windows" or system.startswith(("mingw", "msys", "cygwin")):
        return "windows-x64"
    raise SystemExit(f"unsupported platform: {system}")


def executable(name: str) -> Path:
    suffix = ".exe" if current_platform() == "windows-x64" else ""
    return TOOLS_DIR / name / TOOLS[name]["version"] / f"{name}{suffix}"


def download(url: str, attempts: int = 3) -> bytes:
    """Download ``url``, retrying transient network errors (the checksum is verified by the caller)."""
    for attempt in range(1, attempts + 1):
        try:
            with urllib.request.urlopen(url, timeout=120) as response:  # noqa: S310 (fixed https URL)
                return response.read()
        except OSError as error:  # URLError and HTTPError are OSError subclasses
            if attempt == attempts:
                raise
            print(f"download failed ({error}), retrying", file=sys.stderr)
            time.sleep(10 * attempt)
    raise AssertionError("unreachable")


def install(name: str) -> Path:
    target = executable(name)
    if target.exists():
        return target
    spec = TOOLS[name]
    asset, expected = spec["assets"][current_platform()]
    url = spec["url"].format(version=spec["version"], asset=asset)
    print(f"downloading {url}")
    data = download(url)
    actual = hashlib.sha256(data).hexdigest()
    if actual != expected:
        raise SystemExit(f"checksum mismatch for {url}: expected {expected}, got {actual}")
    binary = target.name
    if asset.endswith(".zip"):
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            content = archive.read(binary)
    else:
        with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as archive:
            member = archive.extractfile(binary)
            if member is None:
                raise SystemExit(f"{binary} not found in {url}")
            content = member.read()
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(content)
    target.chmod(target.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
    return target


if __name__ == "__main__":
    for tool in sys.argv[1:] or TOOLS:
        print(install(tool))

#!/usr/bin/env python3
"""Regenerates the ElPaqueto logo derivatives from the master image.

Usage (from the repository root, needs Pillow: ``python -m pip install Pillow``):

    python eng/branding/make_derivatives.py

Reads ``assets/branding/elpaqueto-logo.png`` and writes:

- ``assets/branding/elpaqueto-logo-480.png``: README banner (displayed at 240 px);
- ``assets/branding/elpaqueto-mascot-512.png``: square crop of the mascot, the source of the icons;
- ``assets/branding/elpaqueto-social-preview.png``: 1280x640 GitHub social preview;
- ``src/ElPaqueto.Web/wwwroot/favicon.ico``: 16, 32 and 48 px icons;
- ``src/ElPaqueto.Web/wwwroot/images/elpaqueto-icon-32.png`` and ``-180.png``: PNG favicon and touch icon;
- ``src/ElPaqueto.Web/wwwroot/images/elpaqueto-header-48.png`` and ``-96.png``: web UI header (1x, 2x).

The output is deterministic for a given Pillow version. The script fails if a file exceeds the size
budget of the branding assets (master 2 MB, every derivative 200 KB).
"""

from __future__ import annotations

import sys
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
BRANDING = ROOT / "assets" / "branding"
WWWROOT = ROOT / "src" / "ElPaqueto.Web" / "wwwroot"
MASTER = BRANDING / "elpaqueto-logo.png"

# Square around the mascot's hat and face in the 1254x1254 master.
MASCOT_BOX = (225, 95, 905, 775)

MASTER_BUDGET = 2 * 1024 * 1024
DERIVATIVE_BUDGET = 200 * 1024


def resized(image: Image.Image, size: tuple[int, int]) -> Image.Image:
    return image.resize(size, Image.Resampling.LANCZOS)


def flatten_background(image: Image.Image, background: tuple[int, int, int], tolerance: int = 10) -> Image.Image:
    """Replaces the near-uniform off-white background by one exact color, so that it quantizes evenly."""
    pixels = image.load()
    width, height = image.size
    for y in range(height):
        for x in range(width):
            pixel = pixels[x, y]
            if all(abs(channel - reference) <= tolerance for channel, reference in zip(pixel, background)):
                pixels[x, y] = background
    return image


def save_png(image: Image.Image, path: Path, colors: int | None = None) -> Path:
    """Saves an optimized PNG, quantized to a palette when ``colors`` is given."""
    if colors is not None:
        image = image.quantize(colors=colors, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
    path.parent.mkdir(parents=True, exist_ok=True)
    image.save(path, format="PNG", optimize=True)
    return path


def main() -> int:
    master = Image.open(MASTER).convert("RGB")
    if master.size != (1254, 1254):
        print(f"unexpected master size {master.size}; adjust MASCOT_BOX", file=sys.stderr)
        return 1

    background = master.getpixel((0, 0))
    banner = flatten_background(resized(master, (480, 480)), background)
    mascot = master.crop(MASCOT_BOX)

    outputs = [
        save_png(banner, BRANDING / "elpaqueto-logo-480.png", colors=256),
        save_png(resized(mascot, (512, 512)), BRANDING / "elpaqueto-mascot-512.png", colors=256),
        save_png(resized(mascot, (32, 32)), WWWROOT / "images" / "elpaqueto-icon-32.png"),
        save_png(resized(mascot, (180, 180)), WWWROOT / "images" / "elpaqueto-icon-180.png", colors=256),
        save_png(resized(mascot, (48, 48)), WWWROOT / "images" / "elpaqueto-header-48.png"),
        save_png(resized(mascot, (96, 96)), WWWROOT / "images" / "elpaqueto-header-96.png", colors=256),
    ]

    social = Image.new("RGB", (1280, 640), background)
    social.paste(resized(master, (600, 600)), (340, 20))
    social = flatten_background(social, background)
    outputs.append(save_png(social, BRANDING / "elpaqueto-social-preview.png", colors=128))

    favicon = WWWROOT / "favicon.ico"
    resized(mascot, (48, 48)).save(favicon, format="ICO", sizes=[(16, 16), (32, 32), (48, 48)])
    outputs.append(favicon)

    failures = 0
    for path in [MASTER, *outputs]:
        size = path.stat().st_size
        budget = MASTER_BUDGET if path == MASTER else DERIVATIVE_BUDGET
        status = "ok" if size <= budget else "OVER BUDGET"
        failures += status != "ok"
        print(f"{status:11} {size:>9} bytes  {path.relative_to(ROOT).as_posix()}")

    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())

# ElPaqueto branding

This folder holds the ElPaqueto logo and the images derived from it.

## Licence status: to be confirmed

**The licence and usage terms of the logo are not decided yet.** The MIT License in [`LICENSE`](../../LICENSE) covers the source code; it is not stated to cover these images. Until the maintainer publishes the terms here:

- the provenance of the image and the right to redistribute it are to be confirmed by the maintainer;
- do not assume that the logo may be reused, modified or redistributed outside this repository;
- forks and derived works should not use the logo to present themselves as the official ElPaqueto project.

This section will be replaced by the provenance and the usage terms once they are decided.

## Files

| File | Size (px) | Use |
| --- | --- | --- |
| `elpaqueto-logo.png` | 1254 × 1254 | Master image: the mascot, the name and the tagline "The package dealer you can trust." Opaque off-white background. |
| `elpaqueto-logo-480.png` | 480 × 480 | README banner, displayed at 240 px. |
| `elpaqueto-mascot-512.png` | 512 × 512 | Square crop of the mascot (hat and face), the source of the icons. |
| `elpaqueto-social-preview.png` | 1280 × 640 | GitHub social preview (repository settings, "Social preview"). |

Derived files used by the web UI, in `src/ElPaqueto.Web/wwwroot/`:

| File | Size (px) | Use |
| --- | --- | --- |
| `favicon.ico` | 16, 32, 48 | Browser favicon. |
| `images/elpaqueto-icon-32.png` | 32 × 32 | PNG favicon. |
| `images/elpaqueto-icon-180.png` | 180 × 180 | Touch icon (`apple-touch-icon`). |
| `images/elpaqueto-header-48.png`, `images/elpaqueto-header-96.png` | 48 × 48, 96 × 96 | Logo of the page header, displayed at 40 px (1x and 2x). |

Only the opaque version exists: the master has no transparency, and no transparent or vector variant has been made. On dark backgrounds the icons are shown as a rounded off-white tile.

## Regenerating the derivatives

Every derivative is generated from the master by [`eng/branding/make_derivatives.py`](../../eng/branding/make_derivatives.py), with Python 3 and [Pillow](https://python-pillow.org/):

```bash
python -m pip install Pillow
python eng/branding/make_derivatives.py
```

The script crops, resizes (Lanczos), reduces the colour palette of the larger PNG files and writes the files listed above. It fails if a file exceeds the size budget: 2 MB for the master, 200 KB for every derivative. When the master changes, run it and commit the master and every derivative together.

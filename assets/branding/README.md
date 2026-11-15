# ElPaqueto branding

This folder holds the ElPaqueto logo and the images derived from it.

## Licence and usage terms

The logo, the mascot and every image in this folder and in the derivative list below are **not** covered by the MIT License of the source code ([`LICENSE`](../../LICENSE)). All rights in them are reserved by the ElPaqueto maintainer, except for the nominative use described here. The project name "ElPaqueto" is treated the same way.

**Allowed without asking**, for the unmodified images:

- showing the logo to refer to the ElPaqueto project: in articles, talks, documentation, package-feed listings and integrations that name ElPaqueto;
- keeping the files as they are in a fork of this repository that exists to contribute back, or in a clone for local use.

**Not allowed without written permission:**

- modifying the logo or the mascot, or building on them;
- using them as the logo or the identity of another product, of a rebranded fork, or of a redistributed derived project: such a project must replace them and its name;
- presenting a project or a service as the official ElPaqueto project, or as endorsed by it;
- merchandise and any other commercial use of the images themselves.

**Provenance.** The image was generated with an AI image tool and selected by the maintainer. For that reason the copyright status of the image, and the absence of third-party rights in it, cannot be guaranteed: these terms are the maintainer's usage policy for the project, offered without warranty. To ask for permission, open an issue on this repository.

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

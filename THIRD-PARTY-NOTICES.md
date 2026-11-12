# Third-party notices

ElPaqueto is licensed under the [MIT License](LICENSE). It includes software written by third parties. Each component below keeps its own copyright and licence. This file lists them for attribution; it does not change their terms.

## Upstream projects

### BaGetter

ElPaqueto is a fork of BaGetter. Most of the source code in this repository comes from it.

- Repository: <https://github.com/bagetter/BaGetter>
- Base commit: [`02b6706`](https://github.com/bagetter/BaGetter/commit/02b6706e404f42da6ef693fa6c15049622e07279) ("Mirrored package identification (#219)")
- Licence: MIT
- Copyright notice: `Copyright 2023 BaGetter`

The BaGetter copyright line and the MIT permission notice are kept unchanged in [`LICENSE`](LICENSE).

### BaGet

BaGetter is itself a fork of BaGet. The history inherited from upstream shows the licence file added to BaGet in commit `10216cd` (2018) and its copyright line later replaced by the BaGetter line in commit `3c2f700` (2023). The permission notice text is identical in both versions.

- Repository: <https://github.com/loic-sharma/BaGet>
- Licence: MIT
- Copyright notice: `Copyright 2018 Loic Sharma`

## Client-side libraries

The web UI redistributes the following libraries under `src/ElPaqueto.Web/wwwroot/lib/`. Versions come from `src/ElPaqueto.Web/libman.json` and the headers of the vendored files. Each directory keeps the library's own licence file.

| Directory | Library | Version | Licence | Copyright notice | Licence file |
| --- | --- | --- | --- | --- | --- |
| `alpinejs` | [Alpine.js](https://github.com/alpinejs/alpine) | 2.8.1 | MIT | Copyright © 2019-2020 Caleb Porzio and contributors | [`LICENSE.md`](src/ElPaqueto.Web/wwwroot/lib/alpinejs/LICENSE.md) |
| `bootstrap` | [Bootstrap](https://github.com/twbs/bootstrap) | 3.4.1 | MIT | Copyright (c) 2011-2019 Twitter, Inc. | [`LICENSE`](src/ElPaqueto.Web/wwwroot/lib/bootstrap/LICENSE) |
| `jquery` | [jQuery](https://github.com/jquery/jquery) | 3.5.1 | MIT | Copyright JS Foundation and other contributors | [`LICENSE.txt`](src/ElPaqueto.Web/wwwroot/lib/jquery/LICENSE.txt) |
| `office-ui-fabric-core` | [Office UI Fabric Core](https://github.com/OfficeDev/office-ui-fabric-core) | 11.0.0 | MIT | Copyright (c) Microsoft Corporation | [`LICENSE`](src/ElPaqueto.Web/wwwroot/lib/office-ui-fabric-core/LICENSE) |

### Notes on embedded code and assets

- **jQuery** embeds the Sizzle CSS selector engine 2.3.5. Its header in `dist/jquery.js` carries the same notice: Copyright JS Foundation and other contributors, released under the MIT license.
- **Bootstrap** ships the Glyphicons Halflings fonts in `dist/fonts/` as part of the Bootstrap 3.4.1 package. The vendored `LICENSE` file does not mention them separately.
- **Office UI Fabric Core**: the vendored `LICENSE` states that use of the assets referenced in Office UI Fabric files is subject to the terms at <https://aka.ms/fabric-assets-license>. The fonts referenced by `fabric.css` are not redistributed by ElPaqueto: the browser loads them from a Microsoft CDN.
- **Alpine.js** `dist/alpine-ie11.js` (an Internet Explorer 11 build, not referenced by the UI layout) bundles polyfills whose notices are kept in the file:
  - classList.js 1.1.20170427 by Eli Grey: dedicated to the public domain;
  - a polyfill with the notice "Copyright (c) 2016 The Polymer Project Authors", under the BSD-style licence at <http://polymer.github.io/LICENSE.txt>;
  - core-js 3.8.3, "© 2021 Denis Pushkarev": the file carries the copyright line but does not name the licence;
  - regenerator runtime, "Copyright (c) 2014-present, Facebook, Inc.", under the MIT license.

  Both Alpine.js builds also contain code without any notice in the file, such as a `Proxy` polyfill and a reactivity module marked `version: 0.26.0`. Their licences cannot be determined from the vendored files.

## NuGet dependencies

NuGet packages referenced by the projects in this repository (see `Directory.Packages.props`) are not stored in the repository. They are restored at build time and keep their own licences, available from each package's metadata. Notices for the full dependency closure of the published binaries and the container image are not generated yet.

# Flora

Flora is a customized fork of [FModel](https://github.com/4sval/FModel), the Unreal Engine archive explorer, built on [CUE4Parse](https://github.com/FabianFG/CUE4Parse). On top of FModel it adds an IDA-style dockable workspace, a visual blueprint viewer and a UMG widget designer.

***

## Features

### Dockable workspace
- Panels (archives, folders, packages, search, audio player, image merger, AES/backup managers, settings, ...) live in one window instead of popups.
- Every panel can be docked, floated, pinned **always on top** (sticky-note style) or detached as an independent window.
- Layout is saved to `%AppData%\FModel\DockLayout.xml`; *Window → Reset Layout* restores the default.

### Blueprint graph viewer
- Double-click a Blueprint package to open it in the graph viewer (toggle: *Settings → Open Blueprints in Graph Viewer*).
- Decompiled Kismet is shown as node graphs, with pseudo-code and C++ style output.
- Uses `.usmap` / `.jmap` mappings for unversioned properties.

### UMG widget designer
- Renders the widget tree with layout, slots, brushes, game fonts and localized text.
- Resolves referenced textures, Paper2D sprites and materials automatically, with a shared cache.
- **Edit (Preview)** tab: change widget/slot properties to try things out. Edits are in-memory only, are highlighted, can be reset per property / widget / all, and are **never written back** to the asset.

### Other
- English UI by default; Chinese (zh-CN) localization is included.
- Blueprint export helpers (C++ / UAssetAPI JSON) and patch-pak write-back tooling.

## Project layout

| Path | Content |
| --- | --- |
| `FModel/` | WPF application (UI, view models, docking, blueprint viewer views) |
| `FModel.BlueprintGraph/` | Blueprint decompiler, graph model, widget layout/props |
| `FModel.BlueprintGraph.Tests/` | Unit tests for the above |
| `CUE4Parse/` | Local CUE4Parse copy used by the app |
| `KardsExtract/` | Small headless CUE4Parse CLI used for testing |

## Building

Requires the .NET SDK matching `FModel.csproj` and Windows (WPF).

```bash
git clone https://github.com/CCB-TEAM/Flora.git
cd Flora
dotnet build FModel/FModel.csproj -c Release
```

## Credits and license

Flora is based on [FModel](https://github.com/4sval/FModel) by 4sval and contributors, [CUE4Parse](https://github.com/FabianFG/CUE4Parse) by FabianFG and contributors, and ideas from UEBlueprintGraphViewer.

Licensed under [GPL-3.0](LICENSE). Third-party libraries are listed in [NOTICE](NOTICE).

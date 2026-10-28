# Flora

Flora is a customized fork of [FModel](https://github.com/4sval/FModel), the Unreal Engine archive explorer, built on [CUE4Parse](https://github.com/FabianFG/CUE4Parse). On top of FModel it adds an IDA-style dockable workspace, a visual blueprint viewer with wire editing, a UMG widget designer and a pak packer.

***

## Features

### Dockable workspace
- Panels (archives, folders, packages, search, audio player, image merger, pak packer, blueprint viewers, agent...) are docked in one window. Dialog-type tools (AES keys, backups, settings, mapping wizard, about) open as ordinary popup windows centered over the main window.
- A panel can be floated into its own **real Windows window** (system title bar and frame, Aero Snap): drag a tab out, double-click it, or use *Window → Float Active Panel*. Like any Windows tool window it is owned by the main window: it always stays above it and is minimized and restored with it, so it can't get lost behind the main window.
- Drag the window's title bar over the main window, use the title bar's system menu (right click) or *Window → Dock All Floating Windows* to dock panels again. Per panel, and remembered: *Always on Top* (above every application) and *Independent Window* (own taskbar button; may go behind the main window). Closing Flora asks panels with unsaved work (e.g. blueprint edits) first.
- Layout is saved to `%AppData%\FModel\DockLayout.xml` (settings keep the FModel folder name); *Window → Reset Layout* restores the default.

### Blueprint graph viewer
- Double-click a Blueprint package to open it in the graph viewer (toggle: *Settings → Open Blueprints in Graph Viewer*).
- Decompiled Kismet is shown as node graphs, with pseudo-code and C++ style output.
- Uses `.usmap` / `.jmap` mappings for unversioned properties.

### UMG widget designer
- Renders the widget tree with layout, slots, brushes, game fonts and localized text.
- Resolves referenced textures, Paper2D sprites and materials automatically, with a shared cache.
- **Edit (Preview)** tab: change widget/slot properties to try things out. Edits are in-memory only, are highlighted, can be reset per property / widget / all, and are **never written back** to the asset.

### Blueprint editing with an LLM agent
- The graph viewer is read-only. Blueprints are edited by a language model through Flora's agent tools; the viewer shows the pending edits (highlighted pins and wires, a Changes list with revert, undo/redo) for review.
- **Graph edits**: literal pin values, class defaults, execution wires (skip or cut a chain, move an event's start) and data wires, planned against the compiled code so only edits it can express are accepted.
- **Bytecode objects** (opt-in): Kismet statements as JSON objects with symbolic references (`import:/Script/...`, property paths, `@offset` / `#label` jump targets); Flora adds names and imports and lays all offsets out again.
- Every edit is validated before saving (offsets, sizes, CUE4Parse read-back, re-decompile). Saving to a package or the pak packer needs the user's confirmation.
- **Built-in agent**: *View → Blueprint Agent* talks to Claude (Anthropic API) or any OpenAI-compatible endpoint (DeepSeek, Qwen, Ollama...). Replies and reasoning stream in; tool calls are shown as collapsible steps; tools that write files ask for approval inline; conversations are saved (`%AppData%\FModel\Agent\Sessions`) and can be reopened. The API key is stored encrypted for the Windows user.
- **Context compaction**: when a request reaches the configured size (default 100k tokens) the agent writes a summary of the conversation for itself, Flora appends the current state of the open blueprints and the pak, and the summary replaces the history (also `/compact`).
- **Native signatures**: the agent can query the loaded `.jmap` dumps (classes, structs, function parameters, enums).
- **MCP clients** get the guide with the `read_guide` tool and as `flora://guide/<topic>` resources (the skill itself is only found by Claude Code inside this repository); the server accepts both `localhost` and `127.0.0.1`.
- **MCP server**: the same tools over MCP (Streamable HTTP on localhost, bearer token) for Claude Code and other clients; the panel copies the `claude mcp add` command.
- **Packing and localization**: the agent can add game or local files to the pak packer, change `.locres` strings, replace textures and sounds, and build the pak (the build asks for confirmation).
- The skill that teaches the workflow lives in `.claude/skills/flora-blueprint-bytecode/` and is also the built-in agent's system prompt.

### Pak packer (*View → Pak Packer*)
- Build a pak from files taken from the loaded game, other `.pak` files or folders (drag and drop), plus any local file (mp4, images, ...).
- Edit in the workspace: replace textures with an image (re-encoded into the original format), edit `.locres` strings, play sounds and replace a sound with an audio file (encoded to BINKA).
- Projects are saved as `.florapack`; the pak is written with the bundled repak writer.
- BINKA encoding uses the external `binka_encode.exe` ([binkaenc](https://github.com/595554963github/binkaenc), built by the user and placed in the `tools` folder; not shipped).

### Other
- English UI by default; Chinese (zh-CN) localization is included.
- Blueprint export helpers (C++ / UAssetAPI JSON).

## Project layout

| Path | Content |
| --- | --- |
| `FModel/` | WPF application (UI, view models, docking, blueprint viewer views) |
| `FModel.BlueprintGraph/` | Blueprint decompiler, graph model, widget layout/props |
| `FModel.BlueprintGraph.Tests/` | Unit tests for the above |
| `FModel/AgentUI/` | Agent panel web interface (Vite + React + TypeScript), hosted in WebView2 |
| `.claude/skills/flora-blueprint-bytecode/` | Agent skill (workflow, edit rules, bytecode object format) |
| `CUE4Parse/` | Local CUE4Parse copy used by the app |
| `UAssetAPI/` | Local UAssetAPI copy (code layout, asset resolver, repak writer) |

## Building

Requires the .NET SDK matching `FModel.csproj` and Windows (WPF).

```bash
git clone https://github.com/CCB-TEAM/Flora.git
cd Flora
dotnet build FModel/FModel.csproj -c Release
```

The build also builds the agent panel (`FModel/AgentUI`) with npm, so Node.js must be installed; `-p:SkipAgentUi=true` skips it when `AgentUI/dist` is already built. To work on the panel with hot reload, run `npm run dev` in `FModel/AgentUI` and start Flora with `FLORA_AGENT_UI_URL=http://localhost:5173` (`FLORA_AGENT_UI_DEVTOOLS=1` enables the WebView2 developer tools). The panel needs the Microsoft Edge WebView2 runtime (part of Windows 11 and current Windows 10).

## Credits and license

Flora is based on [FModel](https://github.com/4sval/FModel) by 4sval and contributors, [CUE4Parse](https://github.com/FabianFG/CUE4Parse) by FabianFG and contributors, and ideas from UEBlueprintGraphViewer.

Licensed under [GPL-3.0](LICENSE). Third-party libraries are listed in [NOTICE](NOTICE).

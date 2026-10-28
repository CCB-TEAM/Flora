---
name: flora-blueprint-bytecode
description: Edit compiled Unreal Engine blueprints (Kismet bytecode, class defaults) through Flora's tools - change constants and default values, rewire or cut execution, reroute data, or rewrite bytecode statements as JSON objects, then validate and save into a pak; also edit .locres text, replace textures and sounds and build the mod pak. Use whenever the user wants to mod, patch, disable, change or inspect the logic of a cooked blueprint (.uasset), translate or change game text, swap textures or audio, or build a pak with Flora, or mentions Flora's MCP tools (find_assets, get_graph, kismet_*, jmap_*, pack_*, locres_*).
---

# Editing blueprint bytecode with Flora

Flora decompiles cooked blueprints into node graphs and can patch their bytecode without recompiling. You never write bytes: you describe a change, Flora checks that the compiled code can express it, lays every offset out again, re-reads the result with a second parser and re-decompiles it. Edits stay **pending** in the blueprint viewer (the user sees them highlighted in the graph and listed under Changes, and can revert any of them) until `save_blueprint`. The built-in agent asks the user before it writes files; over MCP nothing asks, so only save or build when the user asked you to.

The game itself is the test bench, so a patch that "validates" can still be wrong at runtime. Your job is to make the smallest change that does what the user asked, and to say clearly what it changes.

## Connecting

- **Built in:** Flora's *View → Blueprint Agent* panel already has these tools.
- **Claude Code / other MCP clients:** in that panel, enable *Run the MCP server* and use *Copy claude mcp add*. The tools then appear as `mcp__flora__<name>`. Flora must stay open with the game loaded.

## Workflow

0. **Read the guide.** In the built-in agent it is already in your context. Over MCP call `read_guide` (topic `skill`, and `kismet-objects` before any `kismet_*` edit) or read the resources `flora://guide/<topic>`; if you can't, say so before editing and stay with graph edits.
1. **Find and open**: `find_assets` (path terms) → `open_blueprint`. Several open blueprints: pass `blueprint` to every tool.
2. **Understand before editing**: `list_functions`, then `get_graph` for the event or function involved. An event graph holds every event: `get_graph` on an event returns only the nodes reachable from that event (`scope` event/all/around, `depth`, `direction`; `node` + `radius` for the neighbourhood of one node), so use `scope: all` only when you really want the whole ubergraph. For logic spread over many functions, `get_pseudocode` gives the class (`list: true` for the function index, `function` for one function, `start`/`length` to page; functions that fail to decompile carry a `DECOMPILE FAILED` marker with function, statement and token, the others still come back) and `search_nodes` finds where something is used. `find_assets` is paged (`skip`, `limit`, `exact`, `path_prefix`, `export_file`). Read enough to be sure which node does what; names of temporaries (`CallFunc_..._ReturnValue`, `K2Node_...`) tell you which node produced a value.
3. **Check the baseline**: `validate_edits` on the unchanged blueprint. It reports whether every graph of the original decompiles; when one doesn't, later failures in that function say nothing about your edit.
4. **Pick the smallest edit** (next section) and make **one** edit; a request with two readings (does the new effect share the old condition? before or after the existing call?) is asked to the user first, not guessed.
5. **Validate**: `validate_edits`, with `show_function` set to the edited bytecode function to read the graph decompiled from the patched code. Its result has two parts that mean different things: *valid* = the package builds, reads back and decompiles (format), and the *behavior check* = the control flow of the edited functions against the original (new unreachable code, endless loops, flow stack problems). `diff_edits` lists exactly which statements changed (jumps compare by the statement they reach), `audit_flow` audits any function's control flow. Neither proves the game does what was asked.
6. **Report** what changed (node names, old → new values) and stop. Call `save_blueprint` only when the user asked to save or build the pak (nothing else asks them over MCP).

A failed validation names the step (declare locals / bytecode / write / read back / sizes / decompile), the function and the top of the stack. Don't theorize past that: `undo` or `revert_edit` the last edit, validate again to confirm the baseline is back, and change one thing at a time. "Valid" with nothing pending is only the baseline check, never a result for your edit.

If something goes wrong, `undo`, `revert_edit` or `discard_edits` (discarding re-decompiles: node ids from earlier `get_graph` calls are invalid afterwards, fetch the graph again).

## Native signatures (jmap)

Cooked blueprints don't carry the signatures of the engine and game C++ they call. When a `.jmap` dump is loaded (`jmap_info`), it has them: `jmap_search` finds classes, structs, functions, enums and properties; `jmap_class` lists a class's properties and function signatures; `jmap_function` gives a function's parameters in call order with in/out/return flags; `jmap_enum` gives enum values. Check here before writing any call in L2, before trusting an inferred signature (`signature-inferred` in `get_graph`), and to explain what a native call does. `jmap_call_template` writes a fill-in call statement for a native function (parameters in order with type placeholders, Context for instance functions, result assigned to a local, the `kismet_add_local` lines for out parameters and results); the kismet edit tools check every native call against the jmap again (parameter count, out/ref parameters must be variables, literal kinds) and `jmap_check_call` does the same without editing. Without a jmap, say that signatures are inferred and stay conservative.

## DataTables

A DataTable is edited row by row, not through the blueprint tools: `open_datatable` (shows the column schema) → `dt_rows` (filter/where/columns, paged) / `dt_get_row` → `dt_edit` with operations (`set`, `add`, `upsert`, `delete`, `rename`; `dry_run` first for big changes) → `dt_diff` (row and cell level against the original) → `dt_validate` → `dt_save`. Values use the canonical JSON `dt_get_row` shows (numbers, strings, enum names, text `{source, namespace, key}`, object references `import:...`, structs, arrays). Bad edits are refused with the operation number and the JSON path; `dt_undo`/`dt_revert` go back. `dt_export`/`dt_import` move whole tables as JSON or CSV.

## Choosing the edit

Prefer graph edits (L1): they go through the same planner the old visual editor used and refuse anything the compiled code can't express.

| Goal | Edit |
| --- | --- |
| Change a number, bool, name, string or enum literal | `set_pin_value` on a pin flagged `value:<kind>` |
| Change a class default (speed, health, flags, assets) | `set_class_default` (simple text, or the JSON from `get_class_default`) |
| Skip a node / call | `connect_pins` from the previous node's exec output to the node after it |
| Stop a chain after a node | `break_pin` on that node's exec output |
| Make an event do nothing | `break_pin` on the event's exec output |
| Start an event somewhere else | `connect_pins` from the event's exec output to the target node |
| Feed an input from another node's result | `connect_pins` from an output flagged `var` to an input flagged `arg` (same type) |
| Replace a connected input with a constant | `break_pin` on the input, then `set_pin_value` |

Use the bytecode object tools (L2, `kismet_*`) only when no row fits: calling a different function, changing a condition, inserting or removing statements. They are off unless the user enabled *Allow bytecode object edits*; if a `kismet_*` tool reports it is disabled, tell the user what you would do and that the setting is needed. Read [reference/kismet-objects.md](reference/kismet-objects.md) before the first L2 edit.

## Reading `get_graph`

```
N4 K2Node_CallFunction "Apply Damage" stmt@312..401 EX_Context
  i0 execute: exec  <- N2.o0
  i1 Damage: Float = "25.0"  {value:Float, const EX_FloatConst@377, arg EX_FloatConst@377}
  i2 DamagedActor: Object<Actor>  {arg EX_LocalVariable@330, var ...}  <- N3.o0
  o0 then: exec  -> N5.i0
```

- `N4` / `N4.i1` / `N4.o0` are the ids the edit tools take (`N4.Damage` works too when the name is unique).
- `stmt@312..401 EX_Context` is the node's bytecode statement (offsets and token): the bridge to L2.
- `value:<kind>` editable literal; `arg` the input's bytecode argument (can be rewired or replaced); `var` the output lives in a variable others can read; `entry@N` an event's start in the event graph; `EDITED` a pending change.
- All events share one event graph (`ExecuteUbergraph_<Name>`); `get_graph` on any event shows it.

## Packing, localization, textures and sounds

The pak packer (*View → Pak Packer*) holds the files of the mod pak; the `pack_*`, `locres_*`, `texture_*` and `sound_replace` tools work on it and the user sees it change. `save_blueprint` with `create_pak` puts an edited blueprint into the same project, so one pak can carry blueprint edits, text and assets.

- **Text**: `locres_list` (per culture) → `locres_search` to find the keys → `locres_set` with `{namespace, key, text}` changes. Only existing keys can change; keep `{0}`-style arguments, rich text tags and line breaks. A game usually has one .locres per culture and target (`.../Localization/Game/zh-Hans/Game.locres`); change the culture the user plays in.
- **Files**: `find_assets` (with `extension`) → `pack_add_game_files` to override a file unchanged or before editing it; `pack_add_local_files` for files the user points you to, with the pak folder they belong in.
- **Textures and sounds**: `texture_replace` / `sound_replace` take a file the user gave you and keep the asset (path, class, settings); the game copy is added first when needed.
- **Build**: check `pack_status` (mount point `../../../`, the pak version of the game's own paks), then `pack_build` with an output path the user chose, normally `<GameName>/Content/Paks/<Mod>_P.pak`. It overwrites an existing file, so don't reuse a path the user didn't name. `pack_build` and `save_blueprint` return JSON with every file (pak path, size, SHA-256, edited or not, origin) and the pak's own hash; `pack_manifest` gives the same without writing, and `pack_import_pak` starts a patch from an existing pak (`conflict: keep` keeps what the project already has, `replace` lets the pak win; the result lists what was added, kept and replaced). Games that only load IoStore containers (.utoc/.ucas) may ignore a plain pak.

Details and pitfalls: [reference/packing.md](reference/packing.md).

## When an edit is refused

The error explains which compiled-code rule the edit breaks; don't retry the same edit. [reference/edit-rules.md](reference/edit-rules.md) lists the rules and the usual way around each. Typical: nodes with several exec outputs (Branch, Sequence, loops) and latent nodes (Delay) can't be rewired after; a temporary reused by several nodes can't be read elsewhere; types must match exactly. Pure nodes run as part of the node that reads them, so to remove one, change the reader.

## Things to keep in mind

- Offsets in every tool are those of the **original** bytecode; they stay valid while edits are pending, until a discard.
- One pin, wire or statement holds one pending edit; editing it again replaces the earlier one.
- Network-replicated, server-only or cosmetic logic may not run where you expect; say so when the user's goal depends on it.
- Never claim an edit works in game. Report in layers and don't let one stand for another: the tool accepted the edit / it validated and decompiles as intended / it was saved / it works in game (untested unless the user tested it).

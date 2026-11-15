# Polaris MCP integration

Flora can call a separately built Polaris CLI to generate C# SDKs and compile annotated C# changes to exported cooked assets. Polaris and the .NET 10 runtime are installed separately; the CLI, game mappings and SDK are not included in Flora.

1. Build Polaris and locate `polaris.dll`.
2. Call `polaris_generate_sdk` with the CLI, a compatible game JMap and a fresh output directory. The default module filter is `kards,Engine,CoreUObject`; an explicit `filter` can select different modules.
3. Export the target package and its `.uexp` from Flora, preserving its path relative to the Content root. Prepare annotated mod source as described by Polaris.
4. Call `polaris_apply` with the CLI, mod source, Content root, compatible USMAP, generated `Polaris.Sdk` source directory and a separate, empty output directory. `dry_run` defaults to true.
5. To write package copies, use a new output directory and `dry_run=false`. Set `pak=true` to additionally generate `mod.pak`. Inspect the results before using `pack_add_local_files` or `pack_import_pak`.

These tools do not stage edits in the open blueprint viewer, automatically install a pak, or reconcile pending Flora edits. A replaced function is replaced in full and may stop calling the original ubergraph. Polaris supports a subset of C# and has limitations around ubergraph editing and new assets. Static success and a readable pak do not prove runtime behavior; check mapping compatibility and validate in the target game.

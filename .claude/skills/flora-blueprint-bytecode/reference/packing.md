# Packing, localization, textures and sounds

All of these tools work on the project open in Flora's pak packer. Nothing is written to disk until `pack_build` (the pak) or `pack_save_project` (the `.florapack` project); both ask the user.

## How a mod pak overrides the game

- A pak mounted later wins over the game's files with the same path. Patch paks are named `<anything>_P.pak` and go into the game's `Content/Paks` folder (or a `~mods` subfolder for some games), so they load after the base paks.
- Paths in the pak are the game's paths relative to the mount point, e.g. `MyGame/Content/UI/T_Logo.uasset` with mount point `../../../`. Keep the game's path for every overridden file; `pack_add_game_files` does this for you.
- Packages are several files (`.uasset` + `.uexp`, sometimes `.ubulk`/`.uptnl`); they always travel together.
- Match the pak version of the game's own paks (`pack_set_options`). Compression is optional.
- Games that load only IoStore containers (`.utoc`/`.ucas`) without a legacy pak fallback ignore a plain pak; say so if the user's game is like that (`pack_status` / the game folder shows it).
- Encrypted games: the pak Flora writes is not encrypted, which most games accept for patch paks.

## Localization (.locres)

- Unreal looks text up by namespace + key. A `.locres` holds the translations of one culture for one localization target: `<Game>/Content/Localization/<Target>/<culture>/<Target>.locres`, e.g. `Game/zh-Hans/Game.locres`. The culture the player uses decides which file counts.
- `locres_search` shows the pak's copy when the file is already in the pak (edited text), otherwise the game's.
- `locres_set` only changes existing entries; it keeps each entry's hashes so the game still matches them. Changing the text of a key the game also has in its source strings is fine; adding keys is not supported.
- Keep placeholders and markup exactly: `{0}`, `{PlayerName}`, `<Highlight>...</>`, `\r\n`. Unreal formats text at runtime and a missing argument shows up literally.
- For large jobs (translating a whole file), page with `skip`/`take` and send changes in batches of a few hundred entries. Report how many entries changed and which were skipped.
- Text that is not in any .locres (hard-coded in blueprints as a string or culture-invariant text) is in the blueprint: find it with `search_nodes` and change it with `set_pin_value` (String / Text pins) or a class default.

## Textures

- `texture_replace` resizes the image to the texture's size and encodes it in the texture's own format with all mips, so the asset stays valid. Use an image with the same aspect ratio; transparency only survives formats with alpha (`texture_inspect` shows the format).
- UI textures with 9-slice margins, atlases and sprite sheets keep their layout: the new image must follow it.

## Sounds

- `sound_replace` with `audio_file` encodes to Bink Audio with `binka_encode.exe` (not shipped with Flora: the user puts it into the `tools` folder next to Flora.exe). Quality 0 is best, 9 smallest.
- With `sound_asset` another sound wave package (from another cook of the same engine version) is copied over the sound.
- Sounds played through Wwise or FMOD banks (`.bnk`, `.bank`) are not sound wave assets; replacing those needs the game's audio middleware tools.

## Files from disk

- `pack_add_local_files` copies files or whole folders under `target_folder`. Only use paths the user gave you, and put them where the game expects them (videos often in `Content/Movies`, configs under `Config`).
- Loose files such as `.ini` overrides only work if the game reads them from the pak; many shipping games ignore pak configs.

# NotEnoughTranslator: tnkz fork

This repository is a fork of [CRC3D3.NotEnoughTranslator.Plugin](https://github.com/MeidoPromotionAssociation/CRC3D3.NotEnoughTranslator.Plugin) by MeidoPromotionAssociation. It adds features for CRC3D3. Each feature is kept on its own branch, so that it can become a separate pull request to upstream later. Upstream owns the design. This fork follows upstream's rules and conventions.

The upstream [README](README.md) and the documents in [Document/](Document/) describe the plugin. This file describes only what the fork changes.

## Branches

| Branch | Content |
| --- | --- |
| `main` | Upstream `main`, unchanged. It only follows upstream. |
| `tnkz/main` | The default branch of the fork: upstream `main`, plus this file, plus the merged feature branches. |
| `tnkz/<topic>` | One feature, based on upstream `main`. Its diff against upstream is exactly one possible pull request. |

`tnkz/main` is never rewritten. Upstream changes reach it by merges.

## Features in `tnkz/main`

### Story text (`tnkz/story-hook`)

NET translates the story text of CRC3D3 scripts: talk lines, speaker names and choices. It uses the normal JSONL entries in `BepInEx/NotEnoughTranslator/Text`. Upstream loads these files and translates the system-message queue, but has no hook for the other story text.

- Hooks: a prefix on `AdvSystem.Utility.ReplaceCharaName(string)` translates each line before the game inserts character names. Script system messages go through upstream's prefix on `CRC.UI.SystemMessagesManager.ShowSystemMessage(string)`. The `4Text/Enabled` setting turns both on and off.
- Context: a JSONL row can have optional `file` and `label` fields. If one Japanese line needs different English in different places, rows with context give each place its own text. `file` is the script name without folder and `.ks`, and case does not matter. Rows without these fields work as before, and upstream NET ignores the fields. Upstream's system-message hook has no context, so all rows of one system message need the same text.
- Dump: with `EnableTextDump`, untranslated story lines go to `Dump/Text` with their `file` and `label`.
- Fix: the build pins Newtonsoft.Json to 13.0.2, the version that the game ships. With the upstream range, every text dump failed in the game.
- Not translated yet: character names that the game inserts into a line, for example `[HF]`. A speaker shown as a bare tag gets the name from the save data.

The details are in the [translation guide](Document/English/TranslationGuide.md), sections 4.10 and 4.11, and in the [module architecture](Document/English/ModuleArchitecture.md). Both documents have a Simplified Chinese version.

Tested with the second CRC3D3 trial (`demo2` in upstream's CI), update of 2026-10-22. Translation data is not included.

### Personality label (`tnkz/scoped-lookups`)

NET can translate the personality label on the heroine status panel (`天真爛漫`, `クール`, `笑顔`, `麗らか`). The game writes the label as a literal without an I2 term, so the UI module cannot reach it.

- Folder: `BepInEx/NotEnoughTranslator/Personality`, with JSONL rows that have `original` and `text`, exact matches only. These rows never enter the `Text` dictionary, so a label such as `クール` does not collide with the same Japanese in other text.
- Switch: `7Personality/Enabled`, off by default, because the hook reads a private game field. A change in ConfigurationManager takes effect without a restart.
- XUAT: NET records each label that it writes, so XUAT does not translate it again.
- Not seen on screen yet: the second trial has no route to the heroine status panel. In the game, the switch, the load and the install checks work.

The details are in the [translation guide](Document/English/TranslationGuide.md), section 9, and in the [module architecture](Document/English/ModuleArchitecture.md).

Not merged yet: `tnkz/random-names` translates the default random names. The random-name buttons read a CSV table that the branch does not cover yet.

## Pull requests to upstream

None yet.

## Requirements

The same as upstream, plus one setting. `BepInEx/config/BepInEx.cfg` must have `HideManagerGameObject = true`. With the setting off, CRC3D3 destroys the BepInEx manager object at startup, and the plugin can unload itself.

## License

BSD 3-Clause, the same as upstream. See [LICENSE](LICENSE).

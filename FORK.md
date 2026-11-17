# NotEnoughTranslator: tnkz fork

This repository is a fork of [CRC3D3.NotEnoughTranslator.Plugin](https://github.com/MeidoPromotionAssociation/CRC3D3.NotEnoughTranslator.Plugin) by MeidoPromotionAssociation. It adds features for CRC3D3. Each feature is kept on its own branch, so that it can become a separate pull request to upstream later. Upstream owns the design. This fork follows upstream's rules and conventions.

The upstream [README](README.md) and the documents in [Document/](Document/) describe the plugin. This file describes only what the fork changes.

## Branches

| Branch | Content |
| --- | --- |
| `main` | Upstream `main`, unchanged. It only follows upstream. |
| `tnkz/main` | The default branch of the fork: upstream `main`, plus this file, plus the merged feature branches. |
| `tnkz/<topic>` | One feature, based on upstream `main`. Its diff against upstream is exactly one possible pull request. |

When upstream `main` moves, the fork rebuilds every `tnkz/*` branch on the new `main`, `tnkz/main` included, and force-pushes it. A clone of an older state then cannot fast-forward. Fetch the branch again and reset your copy to it.

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

### Random names (`tnkz/random-names`)

NET can translate the random names on the heroine and protagonist edit screens: the default last and first names when a screen opens, and the names from the random buttons (last name, first name and nickname).

- Folder: `BepInEx/NotEnoughTranslator/RandomName`, with JSONL rows that have `original` and `text`, exact matches only. These rows never enter the `Text` dictionary.
- Hooks: a postfix on the three default-name getters of `MaidRandomName`, and a postfix on `SetRandomName(string)` of the four name panels. The button hook writes the translation into the input field of the pressed button, and the game stores it as it stores a typed name. A name that the player typed is never translated.
- Switch: `8RandomName/Enabled`, off by default, because the button hook reads private game fields and the game stores the translated name in the save.
- Not translated: the default nickname when a screen opens. It is the cast's own nickname from the cast data, not a random name. Names that are already stored do not change.
- Not seen on screen yet: the protagonist and heroine status panels. The second trial cannot open them. On the initial edit screens, the getters and all three buttons give English names.

The details are in the [translation guide](Document/English/TranslationGuide.md), section 9, and in the [module architecture](Document/English/ModuleArchitecture.md).

### UI sprites (`tnkz/sprites`)

Upstream replaces a sprite picture in place only when the sprite covers its whole texture. Most Japanese labels are cut from atlas pages: buttons, tabs, headings and the caution screen. The fork also replaces those, with the English pictures from `BepInEx/NotEnoughTranslator/Texture`.

- Key: the file name is the sprite name, for example `caution_ja.png` for the sprite `caution_ja`. Make the picture the size of the sprite rect. NET scales a picture of another size to the same on-screen size and logs a warning.
- Hooks: prefixes on the `Image.sprite` and `Image.overrideSprite` setters, and a postfix on `Image.OnEnable`. They run after upstream's `TextureReplacePatch` and leave whole textures to it. NET builds one texture and one sprite per original sprite, and destroys them at each reload and at unload.
- Change to upstream code: `TextureReplacePatch` gets two internal members, `IsApplied` and `MarkApplied`, so the two hooks never decode the same picture twice.
- Switch: `5Textures/EnableUISpriteReplace`, on by default. While `Texture` holds no picture, NET installs no hook.
- Reload: a reload shows changed pictures at once, without reopening the screen. A whole texture that upstream changed in place keeps its picture until the game loads it again.
- Second trial: upstream's hook does not install there, because its loader method has the third trial's signature. This hook then shows every picture.
- Cost: the first display of a replaced picture costs about 2 ms for the PNG decode, once per picture and reload.
- Not seen on screen yet: this build in the third trial.

The details are in the [translation guide](Document/English/TranslationGuide.md), section 5, and in the [module architecture](Document/English/ModuleArchitecture.md).

## Pull requests to upstream

None yet.

## Requirements

The same as upstream, plus one setting. `BepInEx/config/BepInEx.cfg` must have `HideManagerGameObject = true`. With the setting off, CRC3D3 destroys the BepInEx manager object at startup, and the plugin can unload itself.

## License

BSD 3-Clause, the same as upstream. See [LICENSE](LICENSE).

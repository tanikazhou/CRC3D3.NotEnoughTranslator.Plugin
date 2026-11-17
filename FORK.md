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

### Speaker labels (`tnkz/speaker-lookup`)

NET can show the plain speaker labels of the scripts from their own store. Some scripts of the third trial name a heroine with a label such as `クール` instead of `[HF]`, so the name box ignores a rename and uses the NPC color.

- Folder: `BepInEx/NotEnoughTranslator/Speaker`, with JSONL rows that have `original` and `text`, exact matches only. The key is the raw `name=` value of the `@talk` tag. These rows never enter the `Text` dictionary, so XUAT still translates the same words in the UI.
- Text: a row can map a label to a name tag, for example `クール` to `[HF]`. The name box then shows the stored name in the heroine color.
- Hook: a postfix on `TagTalk` of the ADV and the romance script managers, after the game resolved the voice. The voice and the lip sync stay as the game plays them.
- Switch: `9Speaker/Enabled`, off by default, because the hook reads a protected game field. On the second trial, only the ADV manager gets the hook, because its romance manager has no `TagTalk`.
- Dump: with `EnableTextDump`, an English speaker text such as `Tutorial` goes to `Dump/Text`. The story hook looks it up in `Text` and misses. The dump gets at most one row per line with such a speaker.
- In the third trial: the hook installs on both managers, and the plain speaker `システム` shows "System". The heroine labels and `[ショップ店員]` were not seen: they appear only in try-on scenes, which no CRC shop item of the trial starts, and in scripts that nothing calls.

The details are in the [translation guide](Document/English/TranslationGuide.md), section 9, and in the [module architecture](Document/English/ModuleArchitecture.md).

### UI text (`tnkz/ui-text`)

NET translates TextMeshPro and uGUI `Text` labels at display, so the UI text can move from XUAT to NET. The rows go into the normal `Text` dictionary, for example `BepInEx/NotEnoughTranslator/Text/ui/menu.jsonl`.

- Hooks: prefixes on the `TMP_Text.text` setter, `TMP_Text.SetText(string, bool)` and the uGUI `Text.text` setter, and on the `OnEnable` methods of both label types for prefab text. They are attribute methods in `TextTranslatePatch`, next to the story hooks.
- Call contexts keep the hook away from text that other code owns. These are the story window, the backlog, the story choice buttons, typed input-field text, the system-message label and I2 output. Read-back contexts give the game the Japanese original where game code reads a label as data. These are the romance menu toggles, the topic filters, the SNS expand window and the romance status panel.
- XUAT: keep it installed until NET has the rows for all UI text. NET translates first, and XUAT gets the misses. XUAT writes its English through the same setters, so a dump for pack work needs XUAT disabled. A label that XUAT already translated keeps XUAT's text after a reload, until the game writes it again.
- Dump: with `EnableTextDump`, a UI miss gets the fields `scene` and `path`.
- Second trial: some targets exist only in the third trial, so the whole `TextTranslatePatch` class does not install there. On the second trial, story text and system messages then stay untranslated too.
- Build: the build needs `Unity.TextMeshPro.dll` in `lib/`. The GameLibs repository does not have it yet, so CI cannot build this branch.
- In the third trial: an automated test driver checked the hook with and without XUAT. It covered the first open, reload, the `4Text/Enabled` switch, input fields, story choices and system messages. It also covered the romance toggles, edit buttons and status line, the topic filter and the dump. Not seen: the SNS expand window and fishing, which the trial does not reach.

The details are in the [translation guide](Document/English/TranslationGuide.md), section 4.12, and in the [module architecture](Document/English/ModuleArchitecture.md), section 8.

## Pull requests to upstream

None yet.

## Requirements

The same as upstream, plus one setting. `BepInEx/config/BepInEx.cfg` must have `HideManagerGameObject = true`. With the setting off, CRC3D3 destroys the BepInEx manager object at startup, and the plugin can unload itself.

## License

BSD 3-Clause, the same as upstream. See [LICENSE](LICENSE).

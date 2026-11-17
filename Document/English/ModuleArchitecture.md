# NotEnoughTranslator Module Architecture

[简体中文](../简体中文_SimplifiedChinese/模块架构.md) | [Home](../../README.md) | [Translation and resource guide](TranslationGuide.md)

This document describes the current foundation for plugin development and integration. See the [translation guide](TranslationGuide.md) for resource formats and translation-pack authoring.

## 1. Current Scope

| Module | Implemented | Not yet connected or verified |
| --- | --- | --- |
| UI / I2 | CSV loading, central query hook, parameter and AdvancedFormat processing, forced refresh | In-game UI coverage and font adaptation |
| General text | JSONL loading, exact matching, regex replacement, secondary translation of captures, query API, system-message queue display hook, script display hooks (talk lines, speaker names, choices), TMP / uGUI text display hook | In-game coverage |
| Texture replacement | PNG / JPG / JPEG indexing, reading data by texture name, in-place replacement at the sprite load entry and on uGUI image assignment, original dumps on a miss, uGUI `Image` sprite replacement by sprite name for the other sprites, reload and unload | Non-Texture2D textures and asynchronous load paths outside uGUI images |
| Personality label (opt-in) | JSONL loading, label postfix through reflection | In-game check of the heroine status panel |
| Random names (opt-in) | JSONL loading, getter and button postfixes | In-game check of every caller |
| Speaker label (opt-in) | JSONL loading, `TagTalk` postfixes through reflection | In-game check of a plain-label speaker line |

This is a foundation, not a claim of complete game text or image coverage. Each module can be enabled or disabled independently.

XUAT interop is shared infrastructure, not another translation module. It protects recorded NET translations but does not supply the missing TMP / uGUI display hooks.

## 2. Design Constraints

- Do not register languages, add a `LanguageSource`, or modify the game language or `Product.supportMultiLanguage`.
- Do not provide JAT-style translation-language selection or multilingual-string splitting. A pack supplies the text to display directly.
- Do not use LRU or text hit/miss caches. Compile regex rules during loading and match them directly during queries.
- XUAT protection records returned translations only, without caching source-to-result queries or adding, detecting, or stripping special markers.
- Use .NET regex syntax with secondary exact translation of captures, without custom `Unescape` processing.
- The general text module reads only JSONL; do not introduce a TXT compatibility layer. The personality, random-name, and speaker modules use the same JSONL format.
- Do not port old NGUI or COM3D2 filesystem patches. Do not add a global TMP or uGUI setter patch without the call contexts of section 8, the per-component records of section 7 and a check in the game.
- Translate at display or query boundaries, without modifying the game's business data in place.

## 3. Data Flow

Paths below are relative to `BepInEx/NotEnoughTranslator`. They follow JAT's module boundaries without a language directory:

| Module | Resource loading | Dump output |
| --- | --- | --- |
| UI / I2 | `UI/Text` | `Dump/UI/Text` |
| General text | `Text` | `Dump/Text` |
| Texture replacement | `Texture` | `Dump/Texture` |
| Personality label | `Personality` | None |
| Random names | `RandomName` | None |
| Speaker label | `Speaker` | None |

Each loader scans only its own resource subtree, never the shared root or dump directories. ZIP packs must also be placed in the corresponding text module's subtree.

```text
UI/Text: CSV / ZIP
  -> AsyncTranslationLoader
  -> TranslationLoadResult.I2Terms
  -> UITranslateManager
  -> UITextTranslatePatch
  -> I2 display flow

Text: JSONL / ZIP
  -> AsyncTranslationLoader
  -> TextTranslations / TextRegexTranslations
  -> TextTranslateManager
  -> TextTranslatePatch (system-message queue, talk lines, speaker names, choices, TMP_Text / UI.Text)
  -> DisplayTextRecords (per-component display state)

Images under Texture
  -> TextureReplacementCatalog path index
  -> TextureReplaceManager
  -> TextureReplacePatch (GameResource.LoadAsset<Sprite> / Image.sprite, whole textures in place)
  -> ImageSpriteReplacePatch (uGUI Image sprites: atlas sub-rects and the rest)

Personality: JSONL / ZIP
  -> AsyncTranslationLoader
  -> TextTranslations (regex rows ignored)
  -> PersonalityTranslateManager (ScopedLookupCatalog)
  -> PersonalityTextTranslatePatch
  -> Heroine status panel label, after the game writes it

RandomName: JSONL / ZIP
  -> AsyncTranslationLoader
  -> TextTranslations (regex rows ignored)
  -> RandomNameTranslateManager (ScopedLookupCatalog)
  -> RandomNameTranslatePatch -> Random-name getters, before the game stores the name
  -> RandomNameButtonTranslatePatch -> SetRandomName on four name panels, writes the selected input field

Speaker: JSONL / ZIP
  -> AsyncTranslationLoader
  -> TextTranslations (regex rows ignored)
  -> SpeakerTranslateManager (ScopedLookupCatalog)
  -> SpeakerNameTranslatePatch
  -> TagTalk speaker name, after the game resolved the voice target
```

`TextTranslatePatch` also holds the script entries: a prefix and a postfix on `AdvSystem.Utility.ReplaceCharaName(string)`, and a prefix and a finalizer on each of `ADVKagManager.TagHitRet` and `TagChoicesSet`. Inside a choice tag, the game calls `ReplaceCharaName(string)` twice, the second time on the result of the first; only the first call looks up a translation. The postfix records the final text for XUAT when the prefix replaced the text, and also when the prefix received text that NET already recorded, for example `[HF]` after an entry that maps `[HF]` to itself. If the prefix receives text that NET already recorded, and the script context has a context row for it, the prefix uses that row.

The two text modules share loading infrastructure, but keep their resource channels and consumers separate. `TranslationCatalog` handles exact and regex queries; `ResourceModule<T>` manages loading tasks and publishes results on the main thread.

The TMP / uGUI entries keep one display record per text component that they changed, in `DisplayTextRecords`. The records hold display state for the refresh and the read-back of sections 5 and 8. They never answer a lookup (section 7).

## 4. UI / I2 Query Overrides

The target is CRC's `LocalizationManager.TryGetTranslation`, including its `Localize.AdvancedFormat` parameter. If the expected signature is unavailable, the plugin logs an error and leaves the UI module disabled instead of forcing a language change.

Query behavior:

1. Preserve the original result when `overrideLanguage` is specified. Nested term queries within that call are also left unchanged.
2. Preserve the original translation and success flag when the custom dictionary has no match.
3. Do not replace resource identifiers as text when the original term metadata describes a non-text type.
4. On a match, apply localization parameters, RTL processing, and `AdvancedFormat.Format` to the new translation under the original conditions.
5. Replace the translation and set the success flag only after processing succeeds and the result is not blank.
6. On a processing exception, preserve the original result and warn only once per term per resource load.

There is no need to change the game's language list, language indices, or Source priority. See the [translation guide](TranslationGuide.md) for UI translation formatting requirements.

## 5. Lifecycle and Threading

Each manager provides `Init` / `Reload` / `Unload`, called on Unity's main thread. Resource loading is completion-driven, with no per-frame `Tick` calls.

- Translation-file reading, JSON / CSV parsing, regex compilation, and texture-path scanning run in the background without calling Unity APIs.
- Awaiting a load captures Unity's synchronization context; completion resumes on the main thread to publish the result, without polling task status.
- Reloading keeps the previous snapshot available. Cancelled or obsolete tasks cannot publish stale results.
- File-reading failures retain the previous snapshot. Successfully loading an empty directory clears that module's old resources.
- Texture bytes are read when queried, without caching image contents; path scanning and image reading are separate stages. A built UI sprite keeps its pixels in GPU memory until the next publication or unload; the encoded bytes are not kept.
- Configuration-change events post reload requests to the main thread, coalescing pending requests. The plugin's `Update` only checks reload and dump-flush shortcuts; it never polls resource loading, dump buffers, or interop initialization.

### Display Refresh

After publishing UI resources, the module calls `LocalizeAll(true)` if `LocalizeManager.isSetupCompleted` is already true. Otherwise, a postfix on that property's setter triggers the pending refresh when setup completes, without a frame loop.

When UI translation is enabled, `UITranslateManager.Init` installs both the I2 translation hook and the localization-readiness hook. `Unload` removes the translation hook but retains readiness callbacks, so disabling UI translation before setup completes does not lose a pending native refresh. On plugin destruction, `UITranslateManager.Shutdown` unloads the UI module, clears pending refresh state, and removes the readiness hook. The plugin entry point only calls manager lifecycle methods; it does not manage UI patches directly. None of these operations switches the game's language.

If the game has no original translation for a term, a null I2 result merely leaves the component's current text unchanged; it does not guarantee restoration of an earlier value.

General text already replaces display values at the system-message queue entry: after a reload, newly queued messages use the new dictionary while queued or already shown messages are not refreshed retroactively. At each general-text publication, the TMP / uGUI entries walk their display records on the main thread. Each live component gets the translation from the new snapshot. If the new snapshot has no translation, the component gets its original text back and loses its record. Components with the excluded-writer mark of section 8 are skipped. This refresh covers only components that the hook translated. Other on-screen text updates at its next write or `OnEnable`. On unload, the patches are removed first, and then each live recorded component gets its original text back. `SceneManager.sceneUnloaded` removes the records of destroyed components. Textures replaced in place apply the next time an asset loads or is assigned and are not restored automatically, while UI sprites of the uGUI hook are restored and rebuilt at each publication (below); the remaining text entries currently affect subsequent queries only. Refreshing existing display objects must be implemented alongside their specific display hooks. The personality label changes the next time the panel opens. The random-name module affects only names generated after a reload; names already stored do not change. A speaker label changes from the next talk line.

Script text is not refreshed after a reload or a switch change. The next line uses the new data. The line on screen, the open choices, and the backlog keep their text. The game's `MessageData` keeps only the final text, so the source text is gone, and the backlog holds copies of that final text. A refresh would need a map from shown text back to source text, which comes close to the source-to-result cache that NET does not allow.

### UI Sprites

When a texture snapshot with entries is published and `EnableUISpriteReplace` is on, `TextureReplaceManager` installs `ImageSpriteReplacePatch`: prefixes on the `Image.sprite` and `Image.overrideSprite` setters and a postfix on `Image.OnEnable`. The lookup key is `Sprite.name`, not the texture name, because most UI sprites share large atlas pages. With an empty catalog or the switch off, no hook is installed.

The two texture hooks divide the work. `TextureReplacePatch` replaces whole textures in place. `ImageSpriteReplacePatch` handles every other sprite: atlas sub-rects, and whole textures that `TextureReplacePatch` did not replace, for example a sprite that only reaches `overrideSprite`. Its setter prefixes declare `after` with the `TextureReplacePatch` ID, so `TextureReplacePatch` sees each sprite first. A sprite that covers its whole texture passes through unchanged when `TextureReplacePatch.IsApplied` reports its texture. The registry marks each texture that it builds with `TextureReplacePatch.MarkApplied`, so `TextureReplacePatch` never decodes into it.

`ReplacementSpriteRegistry` builds one texture and one sprite per original Sprite object on the main thread, and owns both. It keeps two tables (original to replacement, and replacement to original) and no table keyed by name. At each publication, `TextureReplaceManager` first has the registry put the original sprites back on every `Image`, destroy its own objects and clear the tables. Then the manager calls `TextureReplacePatch.Reset()`, so an edited picture of a whole texture is decoded again at its next load or assignment. Last, the registry assigns the sprite of each active and enabled Image to itself, so both setter prefixes run in order. A hidden Image gets its replacement when the game enables it, so a reload builds only the pictures on screen. `Unload` removes the hooks first, then restores and destroys. A failed load publishes nothing, so the previous replacements stay.

### Harmony Patch IDs

Following JAT, each patch class declares an explicit, stable, lowercase `HarmonyId`. Prefixes, postfixes, and finalizers in the same patch class share that owner ID; different patch classes use separate IDs. Installation, failure rollback, and unloading use the same Harmony instance, and `UnpatchSelf()` removes only that owner's patches.

| Patch owner | Harmony ID |
| --- | --- |
| `UITextTranslatePatch` | `github.meidopromotionassociation.crc3d3.notenoughtranslator.plugin.hooks.ui.uitexttranslatepatch` |
| `LocalizationReadyPatch` | `github.meidopromotionassociation.crc3d3.notenoughtranslator.plugin.hooks.ui.localizationreadypatch` |
| `TextureReplacePatch` | `github.meidopromotionassociation.crc3d3.notenoughtranslator.plugin.hooks.texture.texturereplacepatch` |
| `TextTranslatePatch` | `github.meidopromotionassociation.crc3d3.notenoughtranslator.plugin.hooks.text.texttranslatepatch` |
| `XUATInterop` | `github.meidopromotionassociation.crc3d3.notenoughtranslator.plugin.utils.xuatinterop` |
| `PersonalityTextTranslatePatch` | `github.meidopromotionassociation.crc3d3.notenoughtranslator.plugin.hooks.ui.personalitytexttranslatepatch` |
| `RandomNameTranslatePatch` | `github.meidopromotionassociation.crc3d3.notenoughtranslator.plugin.hooks.status.randomnametranslatepatch` |
| `RandomNameButtonTranslatePatch` | `github.meidopromotionassociation.crc3d3.notenoughtranslator.plugin.hooks.ui.randomnamebuttontranslatepatch` |
| `ImageSpriteReplacePatch` | `github.meidopromotionassociation.crc3d3.notenoughtranslator.plugin.hooks.texture.imagespritereplacepatch` |
| `SpeakerNameTranslatePatch` | `github.meidopromotionassociation.crc3d3.notenoughtranslator.plugin.hooks.text.speakernametranslatepatch` |

These are Harmony owner IDs, not the BepInEx plugin GUID; the plugin GUID remains unchanged. Future patch classes should declare their own IDs rather than reuse a module-level shorthand.

All three patch classes follow JAT's layout: the methods in `UITextTranslatePatch` / `TextTranslatePatch` only carry `[HarmonyPatch]` with `[HarmonyPrefix]`, `[HarmonyPostfix]` or `[HarmonyFinalizer]` attributes. The owning manager installs them all with `Harmony.CreateAndPatchAll` and rolls back with `UnpatchSelf` on failure. Adding an entry means adding one annotated method to that class; the installation code stays unchanged. `UITextTranslatePatch` targets a method with an `out string` parameter that cannot appear in an attribute argument, so it resolves the target through `[HarmonyTargetMethod]` with the full signature; a signature mismatch throws and disables the UI module. `TextureReplacePatch` targets `GameResource.LoadAsset<Sprite>(string)`; the generic method is likewise built in `[HarmonyTargetMethod]`, and `WfSystem.FileSystem.dll` is a compile reference. `LocalizationReadyPatch` keeps its own `Install` / `Uninstall` because it makes a runtime decision (nothing to await once localization has completed).

The TMP / uGUI entries, the call contexts and the read-back contexts of section 8 are attribute methods in `TextTranslatePatch` too. The same `CreateAndPatchAll` installs them together with the system-message entry. A target that the compile libraries do not have uses the string form `[HarmonyPatch("<type>, Assembly-CSharp", "<method>")]`. Installation is all-or-nothing. If one target is missing in the running game, `UnpatchSelf` rolls back the whole class, and general text stays query-only, the system-message entry included. For example, the `demo2` build has no `CRCRomanceSystem.RomanceMenuControl`, so the display hooks do not install on it.

## 6. Integration APIs

| Manager | Consumer API | Result |
| --- | --- | --- |
| `UITranslateManager` | `TryGetTranslation(term, out translation)` | Text lookup by I2 term |
| `TextTranslateManager` | `TryGetTranslation(sourceText, out translation)` | Exact matching first, then regex rules |
| `TextTranslateManager` | `TryGetTranslation(sourceText, file, label, out translation)` | Context row first (script file and label), then the same path as above |
| `TextTranslateManager` | `TryGetDisplayTranslation(sourceText, out translation)` | The same skips and recording as `TryGetTranslation`, but it never dumps. Display entries that dump with context use it |
| `TextureReplaceManager` | `TryGetReplacement(textureName, out data)` | Encoded replacement-image bytes |
| `TextureReplaceManager` (built in) | uGUI `Image.sprite` / `Image.overrideSprite` setters, `Image.OnEnable` | Replacement sprite built from the same bytes, matched by sprite name |
| `PersonalityTranslateManager` | `TryGetTranslation(original, out translation)` | Exact match of the personality label |
| `RandomNameTranslateManager` | `TryGetTranslation(original, out translation)` | Exact match of a generated random name |
| `SpeakerTranslateManager` | `TryGetTranslation(original, out translation)` | Exact match of the raw speaker label |

All six expose `IsLoaded`, `IsLoading`, `EntryCount`, and `IsHookInstalled`. The text module also exposes `ExactCount` / `RegexCount` / `ContextCount`; the texture module also exposes `HasReplacement`, which queries the index without reading files, and `IsSpriteHookInstalled` / `ReplacedSpriteCount`. The random-name module also exposes `IsButtonHookInstalled`.

The personality hook reaches `TMP_Text.text` through reflection, so the build needs no `Unity.TextMeshPro.dll`. At installation it checks, in order, that `HeroineEditUIStatus` declares the instance field `m_personalityText`, that the field type derives from `TMPro.TMP_Text`, and that `TMP_Text` declares a read-write `string` property `text`. If a check fails, the log names it and no patch is installed.

The random-name module is a named exception to the rule "translate at display or query boundaries" (section 2). Both hooks change a value that the game then stores in the character status and the save. The getter postfix changes the default name that the game stores. The button postfix writes the translation into the input field of the pressed button through the field's `text` setter. The game's own `onValueChanged` listener then stores it, as it stores a typed name. NET writes no status member itself. The button hook reads only the field of the pressed button, so a typed name is never translated. For these reasons, the `8RandomName` switch is disabled by default.

The button hook reaches `TMP_InputField.text` through reflection, so the build needs no `Unity.TextMeshPro.dll`. At installation it checks each field of the four panels, in order: the panel declares the instance field, the field type derives from `TMPro.TMP_InputField`, and `TMP_InputField` declares a read-write `string` property `text`. If a check fails, the log names the failed check, and no button patch is installed. A failed field check also names the panel and the field. The manager installs the getter patch and the button patch independently, and logs the state of both.

The speaker hook finds `Script.ADVKagManager` and `Script.YotogiKagManager` by name, so the build references no member that only newer game builds have. For each manager it checks, in order, that the manager declares an instance method `TagTalk` with one `KagTagSupport` parameter, that it declares the instance field `textData`, and that the field type has a writable public `string` field `talkName`. If a check fails, the log names it, and only that manager gets no patch. The postfix runs after the game resolved the voice target from the raw label, so the voice and the lip sync do not change.

### Script Context Lookup

The overload with `file` and `label` serves callers that know the script position, such as the script hooks. The existing overload is the same call with `null` for both. Its results do not change.

- `TranslationCatalog` keeps one extra dictionary keyed by (file, label, original). It holds only the rows of ambiguous originals, which are originals with more than one distinct text. `ContextCount` is its size. The JSONL processor marks an original as ambiguous when a later exact entry has a different text than the entry before.
- The lookup order is: context dictionary, exact dictionary, regex rules. The probe runs only if the context dictionary is not empty, the `label` is not empty, and `file` is not empty after normalization.
- The catalog compares `file` without case, after it removes any folder and one `.ks` suffix. `label` and `original` use Ordinal comparison. The catalog does not cache hits or misses.
- A row needs both `file` and `label`. The JSONL processor skips a row whose `file` or `label` is not a string.
- The text load log reads `[Text] Loaded/已加载 <exact> exact, <regex> regex, <context> context, <skipped> skipped, <ms> ms`.

The field format is in the [resource guide](TranslationGuide.md#411-script-context-file-and-label).

### Dump Infrastructure

`DumpManager` is shared infrastructure, not another translation module. The plugin initializes its directories once and shuts it down after removing module hooks. A lock protects session deduplication and buffered writes; no timers or frame polling are used.

| API | Purpose |
| --- | --- |
| `UITranslateManager.DumpTerm(term, original)` | Export a missing I2 term after a snapshot is published, including during the first forced UI refresh |
| `DumpManager.DumpTerm(term, original)` | Buffer a caller-confirmed untranslated term as a CSV template |
| `DumpManager.DumpText(original)` | Buffer caller-confirmed untranslated general text as a JSONL template |
| `DumpManager.DumpText(original, file, label)` | The same, with `file` and `label` in the template; deduplicated by (file, label, original) |
| `DumpManager.IsTextDumped(original)` | Side-effect-free probe: is this general text already in the session dedup? |
| `DumpManager.DumpDisplayText(original, scene, path)` | The same guards and dedup as `DumpText`. The JSONL row also has the `scene` and `path` fields |
| `DumpManager.DumpTexture(textureName, pngData)` | Write an original PNG using the replacement catalog's filename key, without overwriting existing files; called by the texture patch on a miss |
| `DumpManager.Flush()` | Flush both text buffers and report whether all writes succeeded |

The I2 query hook forwards missing text terms without modifying native results. General text's normal query API dumps misses only after loading; `IsInTranslationDictionary` remains a side-effect-free probe. Initial loading does not dump the entire pending dictionary as missing. General text display hooks cover the system-message queue, script text and TMP / uGUI text. The TMP / uGUI entries look up with `TryGetDisplayTranslation`. On a miss after loading, they call `DumpDisplayText`. They check `IsTextDumped` first, so they build the scene and path only for a new original. The texture patch encodes and dumps the original image on a miss; the remaining display entries still need game integration.

Text-write failures retain buffers and attempt to restore the previous file length. PNG output uses a temporary file and a non-overwriting move. Dump deduplication survives resource reloads and is separate from XUAT's translated-output registry; it is not a translation-query cache. Switches, thresholds, and flush triggers are documented in the [resource guide](TranslationGuide.md).

Source entry points:

- [UITranslateManager.cs](../../CRC3D3.NotEnoughTranslator.Plugin/Manger/UITranslateManager.cs)
- [TextTranslateManager.cs](../../CRC3D3.NotEnoughTranslator.Plugin/Manger/TextTranslateManager.cs)
- [TextureReplaceManager.cs](../../CRC3D3.NotEnoughTranslator.Plugin/Manger/TextureReplaceManager.cs)
- [PersonalityTranslateManager.cs](../../CRC3D3.NotEnoughTranslator.Plugin/Manger/PersonalityTranslateManager.cs)
- [RandomNameTranslateManager.cs](../../CRC3D3.NotEnoughTranslator.Plugin/Manger/RandomNameTranslateManager.cs)
- [SpeakerTranslateManager.cs](../../CRC3D3.NotEnoughTranslator.Plugin/Manger/SpeakerTranslateManager.cs)
- [ResourceModule.cs](../../CRC3D3.NotEnoughTranslator.Plugin/Loader/ResourceModule.cs)
- [DumpManager.cs](../../CRC3D3.NotEnoughTranslator.Plugin/Manger/DumpManager.cs)

## 7. XUAT Interoperability

Following JAT's decision-hook approach, NET locates the loaded `XUnity.AutoTranslator.Plugin.Core` assembly through reflection. No compile-time XUAT reference or DLL in `lib` is required. Compatible targets are `IsTranslatable` methods returning `bool` with a first parameter of type `string` on these types:

- `Utilities.LanguageHelper`
- `TextTranslationCache`
- `CompositeTextTranslationCache`

Decision order:

1. If the input is a recorded NET UI, general text, personality label, or speaker label translation, return `false` to prevent XUAT from translating it again.
2. If general text display hooks have taken control and the input has an exact or regex translation, also return `false` and leave it to NET.
3. Otherwise preserve XUAT's original decision. Exceptions in NET's decision logic also preserve original behavior rather than blocking every string.

**Step 2 takes effect only after display hooks are actually installed.** `TextTranslateManager.Init` calls `SetDisplayHooksInstalled` with the result of `TextTranslatePatch.Install()`, and removing the patches or unloading the module resets it to false. It is not a translation-pack setting. One patch class covers several entries: the system-message queue, script text and TMP / uGUI text. So this interception means that dictionary sources are no longer handed to XUAT, while its coverage stays limited to integrated entries. Limit: while the display hooks are installed, step 2 refuses XUAT for every dictionary string. If such a string reaches the screen through an excluded call context or an unhooked path (section 8), neither plugin translates it.

The flag is global. While it is on, XUAT skips every string that NET's `Text` dictionary can translate, including strings that no NET hook displays. Data rule: do not put entries into `Text` for text that reaches the screen only through an excluded call context or an unhooked path (section 8). XUAT can no longer translate that text.

Translation recording and query APIs:

- With interop active, general text `TryGetTranslation` skips recorded NET translations and records successful results before returning them.
- With interop active, `TryGetContextTranslation` reads only the context rows, does not skip recorded text, does not dump, and records a hit. The script entries use it for recorded text that has a file and a label.
- `IsInTranslationDictionary` only probes translation availability without recording candidate results; a probe is not treated as output.
- UI calls `MarkTranslated` only after parameter, RTL, and `AdvancedFormat` processing succeeds, recording final results rather than unexpanded templates.
- The UI, general text, personality, and speaker managers expose `IsNetTranslatedText` and `MarkTranslated`. The personality hook records a label before it writes it, because XUAT's TMP setter hook can check the new text during the write. Future display hooks that further modify a returned value should record the final text they actually submit.
- Each of these managers uses a separate, thread-safe set with Ordinal matching. Publishing new resources successfully or unloading a module clears its own set; failed loads preserve the previous state.
- These sets only prevent retranslation; they never return cached translations and contain no miss records or LRU logic. No additional normalization, unescaping, or placeholder inference is performed.

Next to these sets, the TMP / uGUI entries keep the per-component display records of `DisplayTextRecords`. A record holds the component, its original and the translation that the hook wrote. If the game writes that same translation to that component again, the hook passes it through without a lookup. So NET recognizes its own output per component without XUAT, and for any target language. A record never answers a lookup and is not a cache: each new original still goes through the catalog. When XUAT is installed, `TryGetDisplayTranslation` still records the final text with `MarkTranslated`, so XUAT skips it.

Limit: game code can copy NET output from one component to another. The target component has no record for the copy. When XUAT is off, the copy then goes to the lookup and, on a miss, to the dump. The read-back contexts of section 8 cover the known copies.

Limit: while XUAT is installed, XUAT writes its own translations through the same TMP and uGUI setters. The hook sees each of these writes as new game text, so a miss puts XUAT's translation into the dump as an `original`.

Initialization runs from plugin `Start`. Late assembly-load events post a callback to Unity's synchronization context; actual patching runs on the main thread without `Update` polling. Queued callbacks do nothing after interop is unloaded. Without XUAT, NET behaves normally. Incompatible signatures or installation failures produce warnings. Unloading removes only NET's own Harmony patches and unsubscribes from assembly-load events.

`EnableXUATInterop/启用 XUAT 互操作` in `2General` is enabled by default. `XUATInterop.IsInstalled` and `PatchedMethodCount` expose integration status. Reflection compatibility checks do not replace validation with a real XUAT version or the game.

See [XUATInterop.cs](../../CRC3D3.NotEnoughTranslator.Plugin/Utils/XUATInterop.cs) and [TranslatedTextRegistry.cs](../../CRC3D3.NotEnoughTranslator.Plugin/Translation/TranslatedTextRegistry.cs).

## 8. Future Integration Requirements

- General text hooks must replace display values only, without modifying names that double as business keys, CSV data, or game state.
- New general-text display entries belong in `Hooks/Text/TextTranslatePatch.cs` and reuse its Harmony ID and `Translate` helper: adding one `[HarmonyPatch]` method is enough for `PatchAll` to pick it up, without creating one patch class per entry.
- Isolate I2 call context to avoid translating an I2-produced display value a second time.
- Do not permanently exclude a component merely because it has `Localize`; the game can still assign text to that same component directly.
- Texture integration must create and manage `Texture` / `Sprite` objects on Unity's main thread, with clear ownership of original and replacement resources. The uGUI sprite hook owns the textures and sprites that it builds, and restores the originals at each publication and at unload.
- Do not treat the current query APIs or loaded-entry counts as evidence of in-game coverage.

### TMP / uGUI Display Entries

Prefixes on the `TMP_Text.text` setter, on `TMP_Text.SetText(string, bool)` and on the `UnityEngine.UI.Text.text` setter translate each write. Prefab text reaches the screen without a setter call. For it, prefixes on `TextMeshProUGUI.OnEnable` and `UnityEngine.UI.Text.OnEnable` read the current text. On a hit, they set the record first and then assign the translation through the setter. All entries use the source filter of the system-message entry: blank text, numbers and recorded output pass unchanged. On a write, a hit sets the component's record, and a miss removes it. A miss after loading also dumps the original with its scene and path.

Excluded call contexts: a depth counter marks each context, as `_explicitLanguageDepth` does in `UITextTranslatePatch`. A prefix raises it, and a finalizer lowers it, also on an exception. While a depth is above 0, the hook does not look up or dump a write. It removes the component's record and gives the component the excluded-writer mark.

| Context | Targets | Reason |
| --- | --- | --- |
| Story window | `AdvSystem.MessageDisplayClassicStyle.charaNameText` and `messageText` setters, `AdvSystem.NewStyleMessageBlock.SetText(TMP_Text, string)` | Story text and speaker names belong to script display hooks. These writers also carry the typewriter writes and every other message caller. |
| Backlog | `AdvSystem.BackLogMessageUnit.SetMessage(MessageData)` | Backlog text is story text too. Without this context, the backlog shows translations while the story window shows the source text. |
| Story choices | `AdvSystem.ChoicesButton.SetData(ADVKagManager.ChoiceData)` | The choice button shows choice text, which belongs to script display hooks. Without this context, the UI hook looks up and dumps each choice as a UI label. |
| Input fields | `TMPro.TMP_InputField.UpdateLabel` | It writes the typed text. Typed text must not reach the lookup or the dump. Placeholders are normal TMP text and are translated. |
| NET output | `CRC.UI.SystemMessage.ShowSystemMessage(string)` | This label shows the output of the system-message entry. Without XUAT, `MarkTranslated` records nothing, so only this context keeps that output out of a second lookup. |
| I2 output | `I2.Loc.LocalizeTarget_TextMeshPro_UGUI.DoLocalize`, `LocalizeTarget_TextMeshPro_Label.DoLocalize`, `LocalizeTarget_UnityUI_Text.DoLocalize`, `I2.Loc.LocalizeDropdown.OnLocalize` | These methods write I2 output to TMP and uGUI labels. They are the I2 isolation that the requirement above asks for. |

The `OnEnable` prefixes and the refresh run outside every call context. They skip a component with the excluded-writer mark. A later write outside every context clears the mark. So the mark follows the last observed writer and is never permanent. If the game assigns text directly to a component with `Localize`, that write still gets general translation, as the `Localize` requirement above asks.

Read-back contexts: some game code reads a label back and stores, compares or copies the value. Inside a read-back context, a postfix on the `TMP_Text.text` getter checks the component. If it still shows its recorded translation, the getter returns the recorded original. Writes inside a read-back context are translated as usual. Outside every read-back context, the postfix only reads a counter.

| Targets | Read value | Reason |
| --- | --- | --- |
| `CRCRomanceSystem.RomanceMenuControl.Start`, `PlayFlowONOFF`, `RomanceMessageONOFF` | The ON / OFF toggle labels | The game stores the label text in `romanceMenuPlayFlow` / `romanceMenuMessage`, and the configuration save writes it. |
| `RomanceMenuControl.OnEnable` | `[SF]` and `[HF]` in the button labels | The game replaces these tags in the label text with the stored names. |
| `CRC.UI.TopicListManager.Open`, `NameFilter`, `ExpirationTypeFilter` | The filter labels | The game compares the label text with the source category name and copies it to a new label. The click listeners pass the label text from outside any context. So the `NameFilter` and `ExpirationTypeFilter` prefix also replaces the `name` argument with the item's `FilterName`, read inside the context. |
| `CRC.UI.SNS.SNSExpandWindow.OpenWindow` | Six post labels | The window copies the labels into its own labels. Inside the context, it copies the originals, and the write entries translate them. Not checked in the game yet. |
| `CRCRomanceSystem.StatusPanelControl.RefreshFromSession` | Status text, play name, play personality | Each frame, the panel compares the label text with the new Japanese value and writes only on a change. Inside the context, the check sees the original, so a translated label is not rewritten and looked up again every frame. |

Known limits:

- `SNSTrendTabManager` shows the first 30 characters of the translation.
- `ShowCookingRecipeItemChain.ChangeData` and `ScoreFalse.Move` build labels with `text += line`. These labels show mixed text, and each step can add a dump row.
- `FishingGameManager.Cast_FollowCastFishName` compares the fish name label with the Japanese name every frame while the player aims. A translated name fails that check, so the label is written and looked up again every frame. A read-back context there would size the name plate from the Japanese length, so the entry has none.

## 9. Building

Target environment: .NET Framework 4.8 / Unity 2022.3 / BepInEx 5.

Place matching CRC versions of these assemblies in the repository-root `lib` directory:

- `I2.Localization.dll`
- `Assembly-CSharp.dll`
- `Assembly-CSharp-firstpass.dll`
- `WfSystem.FileSystem.dll`
- `UnityEngine.UI.dll`
- `Unity.TextMeshPro.dll`

Run from the repository root:

```powershell
dotnet build CRC3D3.NotEnoughTranslator.Plugin/CRC3D3.NotEnoughTranslator.Plugin.csproj -c Release
```

Async code uses `Task`, without adding a `System.Threading.Tasks.Extensions` / `ValueTask` runtime dependency. A successful build does not replace in-game verification.

CI builds the same project: `.github/workflows/build.yml` downloads the six assemblies from the private `MeidoPromotionAssociation/CRC3D3.GameLibs` repository into `lib` before building, so they are never committed to this public repository. That repository keeps one directory per game version (`2.49.2/`, `demo2/`, ...); which one is used is decided by `DEFAULT_VERSION` at the top of `.github/scripts/fetch_game_libs.sh`, and the `GAME_LIBS_VERSION` repository variable overrides it. A tag matching `PluginVersion` additionally runs `.github/workflows/release.yml`, which packages and publishes the release.

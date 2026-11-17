using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using AdvSystem;
using CRC.UI;
using CRC3D3.NotEnoughTranslator.Plugin.Manger;
using CRC3D3.NotEnoughTranslator.Plugin.Translation;
using CRC3D3.NotEnoughTranslator.Plugin.Utils;
using HarmonyLib;
using I2.Loc;
using Script;
using TMPro;
using UnityEngine;
using UguiText = UnityEngine.UI.Text;

namespace CRC3D3.NotEnoughTranslator.Plugin.Hooks.Text;

/// <summary>
/// 通用文本显示补丁集合：在游戏的具体显示入口把原文交给 Text 词典。
/// 由 <see cref="TextTranslateManager"/> 通过 CreateAndPatchAll 一次性安装。
/// General-text display patches that hand source strings to the Text dictionary at each display entry.
/// Installed in one go by <see cref="TextTranslateManager"/> through CreateAndPatchAll.
/// </summary>
internal static class TextTranslatePatch
{
    /// <summary>
    /// 通用文本显示补丁的独立 Harmony ID。
    /// The dedicated Harmony ID for general-text display patches.
    /// </summary>
    public const string HarmonyId =
        "github.meidopromotionassociation.crc3d3.notenoughtranslator.plugin.hooks.text.texttranslatepatch";

    private static int _failureWarningLogged;
    private const string RomanceMenuControlType = "CRCRomanceSystem.RomanceMenuControl, Assembly-CSharp";
    private const string StatusPanelControlType = "CRCRomanceSystem.StatusPanelControl, Assembly-CSharp";

    private static int _uiFailureWarningLogged;

    [ThreadStatic] private static int _excludedDepth;
    [ThreadStatic] private static int _readBackDepth;

    /// <summary>
    /// 当前线程正在执行的剧情标签上下文。
    /// The script tag context that the current thread is executing.
    /// </summary>
    [ThreadStatic] private static ScriptScope _scope;

    /// <summary>
    /// 上次调试日志记录的剧情文件，用于只在文件变化时记录。
    /// The script file of the last debug log, used to log only when the file changes.
    /// </summary>
    private static string _lastLoggedFile;

    /// <summary>
    /// 本次会话是否已报告剧情上下文读取失败。
    /// Whether a script context read failure was already reported in this session.
    /// </summary>
    private static bool _contextWarningLogged;

    /// <summary>
    /// 剧情标签种类。
    /// The kind of script tag.
    /// </summary>
    private enum ScriptTagKind
    {
        /// <summary>
        /// 不在剧情标签内。
        /// Not inside a script tag.
        /// </summary>
        None,

        /// <summary>
        /// 对白标签 hitret。
        /// The talk tag hitret.
        /// </summary>
        Talk,

        /// <summary>
        /// 选项标签 choicesset。
        /// The choice tag choicesset.
        /// </summary>
        Choice
    }

    /// <summary>
    /// 一次剧情标签调用的上下文：种类、原始文件名、原始标签及选项查询状态。
    /// The context of one script tag call: kind, raw file name, raw label, and choice lookup state.
    /// </summary>
    private struct ScriptScope
    {
        /// <summary>
        /// 当前标签种类。
        /// The current tag kind.
        /// </summary>
        public ScriptTagKind Kind;

        /// <summary>
        /// 游戏报告的原始剧情文件名，读取失败时为 null。
        /// The raw script file name reported by the game, or null if the read failed.
        /// </summary>
        public string File;

        /// <summary>
        /// 游戏报告的原始剧情标签，读取失败时为 null。
        /// The raw script label reported by the game, or null if the read failed.
        /// </summary>
        public string Label;

        /// <summary>
        /// 选项标签内是否已完成首次查询。
        /// Whether the first lookup inside a choice tag is done.
        /// </summary>
        public bool ChoiceLookupDone;
    }

    /// <summary>
    /// 系统消息队列入口，覆盖不经过 I2 的硬编码提示。
    /// The system-message queue entry, covering hardcoded notices that never reach I2.
    /// </summary>
    /// <param name="__0">按位置绑定的消息原文，游戏改名不影响补丁 / The message text bound by position, unaffected by game-side parameter renames.</param>
    [HarmonyPatch(typeof(SystemMessagesManager), nameof(SystemMessagesManager.ShowSystemMessage), typeof(string))]
    [HarmonyPrefix]
    private static void SystemMessagePrefix(ref string __0)
    {
        Translate(ref __0);
    }

    /// <summary>
    /// 剧情文本入口：在游戏插入角色名之前翻译对白、说话人名和选项；选项标签内只有首次调用查询。
    /// The script text entry: translate talk lines, speaker names and choices before the game inserts character names; inside a choice tag only the first call looks up.
    /// </summary>
    /// <param name="__0">按位置绑定的剧情文本，命中时替换为译文 / The script text bound by position, replaced with the translation on a hit.</param>
    /// <param name="__state">本次调用替换了文本或收到已登记的 NET 输出时为 true / True if this call replaced the text or received recorded NET output.</param>
    [HarmonyPatch(typeof(AdvSystem.Utility), nameof(AdvSystem.Utility.ReplaceCharaName), typeof(string))]
    [HarmonyPrefix]
    private static void ScriptTextPrefix(ref string __0, out bool __state)
    {
        __state = false;
        if (_scope.Kind == ScriptTagKind.Choice)
        {
            if (_scope.ChoiceLookupDone) return;
            _scope.ChoiceLookupDone = true;
        }

        var source = __0;
        __state = Translate(ref __0, _scope.File, _scope.Label) || TextTranslateManager.IsNetTranslatedText(source) ||
                  UITranslateManager.IsNetTranslatedText(source);
    }

    /// <summary>
    /// 前置补丁替换了文本，或收到已登记的 NET 输出（例如命中把 [HF] 映射为自身的词条后的 [HF]）时，登记插入角色名后的最终文本，避免 XUAT 再次翻译。
    /// Record the final text with names inserted when the prefix replaced the text or received recorded NET output (for example [HF] after an entry that maps [HF] to itself), so XUAT does not translate it again.
    /// </summary>
    /// <param name="__result">游戏插入角色名后的文本 / The text after the game inserted character names.</param>
    /// <param name="__state">前置补丁替换了文本或收到已登记的 NET 输出时为 true / True if the prefix replaced the text or received recorded NET output.</param>
    [HarmonyPatch(typeof(AdvSystem.Utility), nameof(AdvSystem.Utility.ReplaceCharaName), typeof(string))]
    [HarmonyPostfix]
    private static void ScriptTextPostfix(string __result, bool __state)
    {
        if (__state) TextTranslateManager.MarkTranslated(__result);
    }

    /// <summary>
    /// 进入对白标签：保存上一层上下文，并在文件变化时记录调试日志。
    /// Enter a talk tag: save the previous context, and write a debug log when the file changes.
    /// </summary>
    /// <param name="__instance">执行标签的剧情管理器 / The script manager executing the tag.</param>
    /// <param name="__state">进入前的上下文，由清理补丁恢复 / The context before entry, restored by the finalizer.</param>
    [HarmonyPatch(typeof(ADVKagManager), nameof(ADVKagManager.TagHitRet))]
    [HarmonyPrefix]
    private static void TagHitRetPrefix(ADVKagManager __instance, out ScriptScope __state)
    {
        __state = EnterScope(__instance, ScriptTagKind.Talk);
        if (_scope.File == _lastLoggedFile) return;
        _lastLoggedFile = _scope.File;
        LogManager.Debug($"[Text] Script context/剧情上下文: file={_scope.File}, label={_scope.Label}");
    }

    /// <summary>
    /// 离开对白标签：恢复进入前的上下文，并原样传递标签方法产生的异常。
    /// Leave a talk tag: restore the context from before the tag and pass on exceptions from the tag method unchanged.
    /// </summary>
    /// <param name="__state">前置补丁保存的上下文 / The context saved by the prefix.</param>
    /// <param name="__exception">标签方法的异常，成功时为 null / The tag method exception, or null on success.</param>
    /// <returns>未经修改的原异常 / The original exception without modification.</returns>
    [HarmonyPatch(typeof(ADVKagManager), nameof(ADVKagManager.TagHitRet))]
    [HarmonyFinalizer]
    private static Exception TagHitRetFinalizer(ScriptScope __state, Exception __exception)
    {
        _scope = __state;
        return __exception;
    }

    /// <summary>
    /// 进入选项标签：保存上一层上下文并开始选项查询保护。
    /// Enter a choice tag: save the previous context and start the choice lookup guard.
    /// </summary>
    /// <param name="__instance">执行标签的剧情管理器 / The script manager executing the tag.</param>
    /// <param name="__state">进入前的上下文，由清理补丁恢复 / The context before entry, restored by the finalizer.</param>
    [HarmonyPatch(typeof(ADVKagManager), nameof(ADVKagManager.TagChoicesSet))]
    [HarmonyPrefix]
    private static void TagChoicesSetPrefix(ADVKagManager __instance, out ScriptScope __state)
    {
        __state = EnterScope(__instance, ScriptTagKind.Choice);
    }

    /// <summary>
    /// 离开选项标签：恢复进入前的上下文，并原样传递标签方法产生的异常。
    /// Leave a choice tag: restore the context from before the tag and pass on exceptions from the tag method unchanged.
    /// </summary>
    /// <param name="__state">前置补丁保存的上下文 / The context saved by the prefix.</param>
    /// <param name="__exception">标签方法的异常，成功时为 null / The tag method exception, or null on success.</param>
    /// <returns>未经修改的原异常 / The original exception without modification.</returns>
    [HarmonyPatch(typeof(ADVKagManager), nameof(ADVKagManager.TagChoicesSet))]
    [HarmonyFinalizer]
    private static Exception TagChoicesSetFinalizer(ScriptScope __state, Exception __exception)
    {
        _scope = __state;
        return __exception;
    }

    /// <summary>
    /// TMP 文本属性写入入口，翻译游戏写入的原文并登记组件显示状态。
    /// The TMP text property write entry: translate the source the game writes and record the component's display state.
    /// </summary>
    /// <param name="__instance">被写入的文本组件 / The text component being written.</param>
    /// <param name="value">即将写入的文字，命中后改写成译文 / The text about to be written, rewritten to the translation on a hit.</param>
    [HarmonyPatch(typeof(TMP_Text), nameof(TMP_Text.text), MethodType.Setter)]
    [HarmonyPrefix]
    private static void TmpTextSetterPrefix(TMP_Text __instance, ref string value)
    {
        TranslateDisplayWrite(__instance, ref value);
    }

    /// <summary>
    /// TMP SetText 入口，它不经过 text 属性直接写入文字。
    /// The TMP SetText entry, which writes the text without the text property.
    /// </summary>
    /// <param name="__instance">被写入的文本组件 / The text component being written.</param>
    /// <param name="__0">按位置绑定的即将写入的文字 / The text about to be written, bound by position.</param>
    [HarmonyPatch(typeof(TMP_Text), nameof(TMP_Text.SetText), typeof(string), typeof(bool))]
    [HarmonyPrefix]
    private static void TmpSetTextPrefix(TMP_Text __instance, ref string __0)
    {
        TranslateDisplayWrite(__instance, ref __0);
    }

    /// <summary>
    /// uGUI Text 文本属性写入入口。
    /// The uGUI Text property write entry.
    /// </summary>
    /// <param name="__instance">被写入的文本组件 / The text component being written.</param>
    /// <param name="value">即将写入的文字，命中后改写成译文 / The text about to be written, rewritten to the translation on a hit.</param>
    [HarmonyPatch(typeof(UguiText), nameof(UguiText.text), MethodType.Setter)]
    [HarmonyPrefix]
    private static void UguiTextSetterPrefix(UguiText __instance, ref string value)
    {
        TranslateDisplayWrite(__instance, ref value);
    }

    /// <summary>
    /// TMP UGUI 启用入口：预制体文字不经过任何写入，启用时检查一次。
    /// The TMP UGUI enable entry: prefab text reaches the screen without any write, so it is checked once on enable.
    /// </summary>
    /// <param name="__instance">被启用的文本组件 / The text component being enabled.</param>
    [HarmonyPatch(typeof(TextMeshProUGUI), "OnEnable")]
    [HarmonyPrefix]
    private static void TmpOnEnablePrefix(TextMeshProUGUI __instance)
    {
        TranslateOnEnable(__instance, __instance.text);
    }

    /// <summary>
    /// uGUI Text 启用入口。
    /// The uGUI Text enable entry.
    /// </summary>
    /// <param name="__instance">被启用的文本组件 / The text component being enabled.</param>
    [HarmonyPatch(typeof(UguiText), "OnEnable")]
    [HarmonyPrefix]
    private static void UguiOnEnablePrefix(UguiText __instance)
    {
        TranslateOnEnable(__instance, __instance.text);
    }

    /// <summary>
    /// 读取上下文中，TMP 文本读取返回登记的原文，使游戏逻辑看到游戏自己的文字。
    /// In a read-back context, a TMP text read returns the recorded original, so game logic sees the game's own text.
    /// </summary>
    /// <param name="__instance">被读取的文本组件 / The text component being read.</param>
    /// <param name="__result">读取结果，读取上下文中改写成原文 / The read result, rewritten to the original in a read-back context.</param>
    [HarmonyPatch(typeof(TMP_Text), nameof(TMP_Text.text), MethodType.Getter)]
    [HarmonyPostfix]
    private static void TmpTextGetterPostfix(TMP_Text __instance, ref string __result)
    {
        if (_readBackDepth == 0) return;
        if (DisplayTextRecords.TryGetOriginal(__instance, __result, out var original)) __result = original;
    }

    /// <summary>
    /// 经典剧情窗口的说话人写入：剧情文字由剧本入口负责，UI 补丁不处理。
    /// The speaker write of the classic story window: story text belongs to the script entries, not to the UI patches.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    [HarmonyPatch(typeof(MessageDisplayClassicStyle), nameof(MessageDisplayClassicStyle.charaNameText), MethodType.Setter)]
    [HarmonyPrefix]
    private static void ClassicNameExcludedPrefix(out bool __state)
    {
        __state = true;
        _excludedDepth++;
    }

    /// <summary>
    /// 离开排除上下文：仅在本补丁的前缀进入时降低深度，异常照常抛出。
    /// Leave the excluded context: lower the depth only if this patch's prefix entered it, and let exceptions propagate.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="__exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    [HarmonyPatch(typeof(MessageDisplayClassicStyle), nameof(MessageDisplayClassicStyle.charaNameText), MethodType.Setter)]
    [HarmonyFinalizer]
    private static Exception ClassicNameExcludedFinalizer(bool __state, Exception __exception) =>
        ExcludedFinalizer(__state, __exception);

    /// <summary>
    /// 经典剧情窗口的正文写入，打字机效果每帧经过这里。
    /// The message write of the classic story window; the typewriter passes here every frame.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    [HarmonyPatch(typeof(MessageDisplayClassicStyle), nameof(MessageDisplayClassicStyle.messageText), MethodType.Setter)]
    [HarmonyPrefix]
    private static void ClassicMessageExcludedPrefix(out bool __state)
    {
        __state = true;
        _excludedDepth++;
    }

    /// <summary>
    /// 离开排除上下文：仅在本补丁的前缀进入时降低深度，异常照常抛出。
    /// Leave the excluded context: lower the depth only if this patch's prefix entered it, and let exceptions propagate.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="__exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    [HarmonyPatch(typeof(MessageDisplayClassicStyle), nameof(MessageDisplayClassicStyle.messageText), MethodType.Setter)]
    [HarmonyFinalizer]
    private static Exception ClassicMessageExcludedFinalizer(bool __state, Exception __exception) =>
        ExcludedFinalizer(__state, __exception);

    /// <summary>
    /// 新式剧情窗口的统一写入方法，说话人与正文都经过这里。
    /// The single write method of the new-style story window; speaker and message both pass here.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    [HarmonyPatch(typeof(NewStyleMessageBlock), "SetText", typeof(TMP_Text), typeof(string))]
    [HarmonyPrefix]
    private static void NewStyleExcludedPrefix(out bool __state)
    {
        __state = true;
        _excludedDepth++;
    }

    /// <summary>
    /// 离开排除上下文：仅在本补丁的前缀进入时降低深度，异常照常抛出。
    /// Leave the excluded context: lower the depth only if this patch's prefix entered it, and let exceptions propagate.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="__exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    [HarmonyPatch(typeof(NewStyleMessageBlock), "SetText", typeof(TMP_Text), typeof(string))]
    [HarmonyFinalizer]
    private static Exception NewStyleExcludedFinalizer(bool __state, Exception __exception) =>
        ExcludedFinalizer(__state, __exception);

    /// <summary>
    /// 回想记录的剧情行，与剧情窗口保持一致。
    /// Story lines in the backlog, kept consistent with the story window.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    [HarmonyPatch(typeof(BackLogMessageUnit), nameof(BackLogMessageUnit.SetMessage))]
    [HarmonyPrefix]
    private static void BackLogExcludedPrefix(out bool __state)
    {
        __state = true;
        _excludedDepth++;
    }

    /// <summary>
    /// 离开排除上下文：仅在本补丁的前缀进入时降低深度，异常照常抛出。
    /// Leave the excluded context: lower the depth only if this patch's prefix entered it, and let exceptions propagate.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="__exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    [HarmonyPatch(typeof(BackLogMessageUnit), nameof(BackLogMessageUnit.SetMessage))]
    [HarmonyFinalizer]
    private static Exception BackLogExcludedFinalizer(bool __state, Exception __exception) =>
        ExcludedFinalizer(__state, __exception);

    /// <summary>
    /// 剧情选项按钮的写入：剧情文字由剧本入口负责，UI 补丁不处理。
    /// The write of the story choice button: story text belongs to the script entries, not to the UI patches.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    [HarmonyPatch(typeof(ChoicesButton), nameof(ChoicesButton.SetData))]
    [HarmonyPrefix]
    private static void ChoicesButtonExcludedPrefix(out bool __state)
    {
        __state = true;
        _excludedDepth++;
    }

    /// <summary>
    /// 离开排除上下文：仅在本补丁的前缀进入时降低深度，异常照常抛出。
    /// Leave the excluded context: lower the depth only if this patch's prefix entered it, and let exceptions propagate.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="__exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    [HarmonyPatch(typeof(ChoicesButton), nameof(ChoicesButton.SetData))]
    [HarmonyFinalizer]
    private static Exception ChoicesButtonExcludedFinalizer(bool __state, Exception __exception) =>
        ExcludedFinalizer(__state, __exception);

    /// <summary>
    /// 输入框把玩家输入的文字写入显示组件，这些文字不查询也不导出。
    /// The input field writes the player's typed text into its display component; that text is neither looked up nor dumped.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    [HarmonyPatch(typeof(TMP_InputField), "UpdateLabel")]
    [HarmonyPrefix]
    private static void InputFieldExcludedPrefix(out bool __state)
    {
        __state = true;
        _excludedDepth++;
    }

    /// <summary>
    /// 离开排除上下文：仅在本补丁的前缀进入时降低深度，异常照常抛出。
    /// Leave the excluded context: lower the depth only if this patch's prefix entered it, and let exceptions propagate.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="__exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    [HarmonyPatch(typeof(TMP_InputField), "UpdateLabel")]
    [HarmonyFinalizer]
    private static Exception InputFieldExcludedFinalizer(bool __state, Exception __exception) =>
        ExcludedFinalizer(__state, __exception);

    /// <summary>
    /// 系统消息标签显示的是 <see cref="SystemMessagePrefix"/> 已处理的 NET 输出，不再查询第二次。
    /// The system-message label shows NET output that <see cref="SystemMessagePrefix"/> already handled, so it is not looked up a second time.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    [HarmonyPatch(typeof(SystemMessage), nameof(SystemMessage.ShowSystemMessage), typeof(string))]
    [HarmonyPrefix]
    private static void SystemMessageLabelExcludedPrefix(out bool __state)
    {
        __state = true;
        _excludedDepth++;
    }

    /// <summary>
    /// 离开排除上下文：仅在本补丁的前缀进入时降低深度，异常照常抛出。
    /// Leave the excluded context: lower the depth only if this patch's prefix entered it, and let exceptions propagate.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="__exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    [HarmonyPatch(typeof(SystemMessage), nameof(SystemMessage.ShowSystemMessage), typeof(string))]
    [HarmonyFinalizer]
    private static Exception SystemMessageLabelExcludedFinalizer(bool __state, Exception __exception) =>
        ExcludedFinalizer(__state, __exception);

    /// <summary>
    /// I2 写入 TMP UGUI 的译文，隔离 I2 调用上下文以免二次翻译。
    /// I2 output written to TMP UGUI; the I2 call context is isolated to avoid translating it twice.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    [HarmonyPatch(typeof(LocalizeTarget_TextMeshPro_UGUI), nameof(LocalizeTarget_TextMeshPro_UGUI.DoLocalize))]
    [HarmonyPrefix]
    private static void I2TmpUguiExcludedPrefix(out bool __state)
    {
        __state = true;
        _excludedDepth++;
    }

    /// <summary>
    /// 离开排除上下文：仅在本补丁的前缀进入时降低深度，异常照常抛出。
    /// Leave the excluded context: lower the depth only if this patch's prefix entered it, and let exceptions propagate.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="__exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    [HarmonyPatch(typeof(LocalizeTarget_TextMeshPro_UGUI), nameof(LocalizeTarget_TextMeshPro_UGUI.DoLocalize))]
    [HarmonyFinalizer]
    private static Exception I2TmpUguiExcludedFinalizer(bool __state, Exception __exception) =>
        ExcludedFinalizer(__state, __exception);

    /// <summary>
    /// I2 写入 TMP 3D 标签的译文。
    /// I2 output written to a 3D TMP label.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    [HarmonyPatch(typeof(LocalizeTarget_TextMeshPro_Label), nameof(LocalizeTarget_TextMeshPro_Label.DoLocalize))]
    [HarmonyPrefix]
    private static void I2TmpLabelExcludedPrefix(out bool __state)
    {
        __state = true;
        _excludedDepth++;
    }

    /// <summary>
    /// 离开排除上下文：仅在本补丁的前缀进入时降低深度，异常照常抛出。
    /// Leave the excluded context: lower the depth only if this patch's prefix entered it, and let exceptions propagate.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="__exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    [HarmonyPatch(typeof(LocalizeTarget_TextMeshPro_Label), nameof(LocalizeTarget_TextMeshPro_Label.DoLocalize))]
    [HarmonyFinalizer]
    private static Exception I2TmpLabelExcludedFinalizer(bool __state, Exception __exception) =>
        ExcludedFinalizer(__state, __exception);

    /// <summary>
    /// I2 写入 uGUI Text 的译文。
    /// I2 output written to a uGUI Text.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    [HarmonyPatch(typeof(LocalizeTarget_UnityUI_Text), nameof(LocalizeTarget_UnityUI_Text.DoLocalize))]
    [HarmonyPrefix]
    private static void I2UguiExcludedPrefix(out bool __state)
    {
        __state = true;
        _excludedDepth++;
    }

    /// <summary>
    /// 离开排除上下文：仅在本补丁的前缀进入时降低深度，异常照常抛出。
    /// Leave the excluded context: lower the depth only if this patch's prefix entered it, and let exceptions propagate.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="__exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    [HarmonyPatch(typeof(LocalizeTarget_UnityUI_Text), nameof(LocalizeTarget_UnityUI_Text.DoLocalize))]
    [HarmonyFinalizer]
    private static Exception I2UguiExcludedFinalizer(bool __state, Exception __exception) =>
        ExcludedFinalizer(__state, __exception);

    /// <summary>
    /// I2 下拉框重填选项后刷新标题，标题文字来自 I2。
    /// The I2 dropdown refills its options and refreshes its caption, whose text comes from I2.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    [HarmonyPatch(typeof(LocalizeDropdown), nameof(LocalizeDropdown.OnLocalize))]
    [HarmonyPrefix]
    private static void I2DropdownExcludedPrefix(out bool __state)
    {
        __state = true;
        _excludedDepth++;
    }

    /// <summary>
    /// 离开排除上下文：仅在本补丁的前缀进入时降低深度，异常照常抛出。
    /// Leave the excluded context: lower the depth only if this patch's prefix entered it, and let exceptions propagate.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="__exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    [HarmonyPatch(typeof(LocalizeDropdown), nameof(LocalizeDropdown.OnLocalize))]
    [HarmonyFinalizer]
    private static Exception I2DropdownExcludedFinalizer(bool __state, Exception __exception) =>
        ExcludedFinalizer(__state, __exception);

    /// <summary>
    /// 恋爱菜单读取开关标签中的“　ON”/“　OFF”，并把结果写入游戏配置。
    /// The romance menu reads U+3000 + "ON"/"OFF" in its toggle labels and stores the result in the game configuration.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    [HarmonyPatch(RomanceMenuControlType, "Start")]
    [HarmonyPrefix]
    private static void RomanceStartReadBackPrefix(out bool __state)
    {
        __state = true;
        _readBackDepth++;
    }

    /// <summary>
    /// 离开读取上下文：仅在本补丁的前缀进入时降低深度，异常照常抛出。
    /// Leave the read-back context: lower the depth only if this patch's prefix entered it, and let exceptions propagate.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="__exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    [HarmonyPatch(RomanceMenuControlType, "Start")]
    [HarmonyFinalizer]
    private static Exception RomanceStartReadBackFinalizer(bool __state, Exception __exception) =>
        ReadBackFinalizer(__state, __exception);

    /// <summary>
    /// 恋爱菜单在标签的 [SF]/[HF] 处插入角色称呼。
    /// The romance menu inserts the stored call names at [SF]/[HF] in its labels.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    [HarmonyPatch(RomanceMenuControlType, "OnEnable")]
    [HarmonyPrefix]
    private static void RomanceEnableReadBackPrefix(out bool __state)
    {
        __state = true;
        _readBackDepth++;
    }

    /// <summary>
    /// 离开读取上下文：仅在本补丁的前缀进入时降低深度，异常照常抛出。
    /// Leave the read-back context: lower the depth only if this patch's prefix entered it, and let exceptions propagate.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="__exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    [HarmonyPatch(RomanceMenuControlType, "OnEnable")]
    [HarmonyFinalizer]
    private static Exception RomanceEnableReadBackFinalizer(bool __state, Exception __exception) =>
        ReadBackFinalizer(__state, __exception);

    /// <summary>
    /// 播放流程开关：读取标签决定 romanceMenuPlayFlow。
    /// The play-flow toggle: the label decides romanceMenuPlayFlow.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    [HarmonyPatch(RomanceMenuControlType, "PlayFlowONOFF")]
    [HarmonyPrefix]
    private static void RomancePlayFlowReadBackPrefix(out bool __state)
    {
        __state = true;
        _readBackDepth++;
    }

    /// <summary>
    /// 离开读取上下文：仅在本补丁的前缀进入时降低深度，异常照常抛出。
    /// Leave the read-back context: lower the depth only if this patch's prefix entered it, and let exceptions propagate.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="__exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    [HarmonyPatch(RomanceMenuControlType, "PlayFlowONOFF")]
    [HarmonyFinalizer]
    private static Exception RomancePlayFlowReadBackFinalizer(bool __state, Exception __exception) =>
        ReadBackFinalizer(__state, __exception);

    /// <summary>
    /// 通知开关：读取标签切换 ON/OFF。
    /// The notification toggle: the label switches ON/OFF.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    [HarmonyPatch(RomanceMenuControlType, "RomanceMessageONOFF")]
    [HarmonyPrefix]
    private static void RomanceMessageReadBackPrefix(out bool __state)
    {
        __state = true;
        _readBackDepth++;
    }

    /// <summary>
    /// 离开读取上下文：仅在本补丁的前缀进入时降低深度，异常照常抛出。
    /// Leave the read-back context: lower the depth only if this patch's prefix entered it, and let exceptions propagate.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="__exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    [HarmonyPatch(RomanceMenuControlType, "RomanceMessageONOFF")]
    [HarmonyFinalizer]
    private static Exception RomanceMessageReadBackFinalizer(bool __state, Exception __exception) =>
        ReadBackFinalizer(__state, __exception);

    /// <summary>
    /// 恋爱游玩状态面板每帧比较标签文字与新的日文值后才写入；读取原文使比较成立，避免每帧重写与查询。
    /// The romance play status panel compares label text with the new Japanese value every frame before it writes; reading the original keeps that check true and avoids a rewrite and a lookup every frame.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    [HarmonyPatch(StatusPanelControlType, "RefreshFromSession")]
    [HarmonyPrefix]
    private static void StatusRefreshReadBackPrefix(out bool __state)
    {
        __state = true;
        _readBackDepth++;
    }

    /// <summary>
    /// 离开读取上下文：仅在本补丁的前缀进入时降低深度，异常照常抛出。
    /// Leave the read-back context: lower the depth only if this patch's prefix entered it, and let exceptions propagate.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="__exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    [HarmonyPatch(StatusPanelControlType, "RefreshFromSession")]
    [HarmonyFinalizer]
    private static Exception StatusRefreshReadBackFinalizer(bool __state, Exception __exception) =>
        ReadBackFinalizer(__state, __exception);

    /// <summary>
    /// 话题列表打开时用筛选标签文字与日文分类名比较，避免重复筛选项。
    /// On open, the topic list compares filter label text with the Japanese category name to avoid duplicate filter items.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    [HarmonyPatch(typeof(TopicListManager), nameof(TopicListManager.Open))]
    [HarmonyPrefix]
    private static void TopicOpenReadBackPrefix(out bool __state)
    {
        __state = true;
        _readBackDepth++;
    }

    /// <summary>
    /// 离开读取上下文：仅在本补丁的前缀进入时降低深度，异常照常抛出。
    /// Leave the read-back context: lower the depth only if this patch's prefix entered it, and let exceptions propagate.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="__exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    [HarmonyPatch(typeof(TopicListManager), nameof(TopicListManager.Open))]
    [HarmonyFinalizer]
    private static Exception TopicOpenReadBackFinalizer(bool __state, Exception __exception) =>
        ReadBackFinalizer(__state, __exception);

    /// <summary>
    /// 社交动态展开窗口把帖子标签的文字复制到自己的标签，复制原文后由写入补丁重新翻译。
    /// The SNS expand window copies the post labels into its own labels; it copies the originals, and the write patch translates them again.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    [HarmonyPatch(typeof(CRC.UI.SNS.SNSExpandWindow), nameof(CRC.UI.SNS.SNSExpandWindow.OpenWindow))]
    [HarmonyPrefix]
    private static void SnsExpandReadBackPrefix(out bool __state)
    {
        __state = true;
        _readBackDepth++;
    }

    /// <summary>
    /// 离开读取上下文：仅在本补丁的前缀进入时降低深度，异常照常抛出。
    /// Leave the read-back context: lower the depth only if this patch's prefix entered it, and let exceptions propagate.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="__exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    [HarmonyPatch(typeof(CRC.UI.SNS.SNSExpandWindow), nameof(CRC.UI.SNS.SNSExpandWindow.OpenWindow))]
    [HarmonyFinalizer]
    private static Exception SnsExpandReadBackFinalizer(bool __state, Exception __exception) =>
        ReadBackFinalizer(__state, __exception);

    /// <summary>
    /// 名称筛选：游戏的监听器在任何上下文之外读取标签文字，因此在这里换回筛选项的原文。
    /// The name filter: the game's listener reads the label text outside any context, so the filter item's original is put back here.
    /// </summary>
    /// <param name="__0">按位置绑定的筛选项 / The filter item, bound by position.</param>
    /// <param name="__2">按位置绑定的筛选名，换成原文 / The filter name, bound by position, replaced by the original.</param>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    [HarmonyPatch(typeof(TopicListManager), nameof(TopicListManager.NameFilter))]
    [HarmonyPrefix]
    private static void TopicNameFilterReadBackPrefix(TopicFilterItem __0, ref string __2, out bool __state) =>
        __state = EnterFilterReadBack(__0, ref __2);

    /// <summary>
    /// 离开读取上下文：仅在本补丁的前缀进入时降低深度，异常照常抛出。
    /// Leave the read-back context: lower the depth only if this patch's prefix entered it, and let exceptions propagate.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="__exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    [HarmonyPatch(typeof(TopicListManager), nameof(TopicListManager.NameFilter))]
    [HarmonyFinalizer]
    private static Exception TopicNameFilterReadBackFinalizer(bool __state, Exception __exception) =>
        ReadBackFinalizer(__state, __exception);

    /// <summary>
    /// 期限筛选：把筛选名换回原文后，游戏把它复制到新的已选标签。
    /// The expiration filter: the filter name is put back to the original before the game copies it to a new selected label.
    /// </summary>
    /// <param name="__0">按位置绑定的筛选项 / The filter item, bound by position.</param>
    /// <param name="__2">按位置绑定的筛选名，换成原文 / The filter name, bound by position, replaced by the original.</param>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    [HarmonyPatch(typeof(TopicListManager), nameof(TopicListManager.ExpirationTypeFilter))]
    [HarmonyPrefix]
    private static void TopicExpirationFilterReadBackPrefix(TopicFilterItem __0, ref string __2, out bool __state) =>
        __state = EnterFilterReadBack(__0, ref __2);

    /// <summary>
    /// 离开读取上下文：仅在本补丁的前缀进入时降低深度，异常照常抛出。
    /// Leave the read-back context: lower the depth only if this patch's prefix entered it, and let exceptions propagate.
    /// </summary>
    /// <param name="__state">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="__exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    [HarmonyPatch(typeof(TopicListManager), nameof(TopicListManager.ExpirationTypeFilter))]
    [HarmonyFinalizer]
    private static Exception TopicExpirationFilterReadBackFinalizer(bool __state, Exception __exception) =>
        ReadBackFinalizer(__state, __exception);

    /// <summary>
    /// 发布新快照后在主线程刷新本补丁译过的组件：命中写入新译文，未命中写回原文；带排除标记的组件跳过。
    /// After a new snapshot is published, refresh on the main thread the components these patches translated: a hit writes the new translation, a miss writes the original back; marked components are skipped.
    /// </summary>
    internal static void RefreshDisplayedText()
    {
        foreach (var (component, original) in DisplayTextRecords.TakeLive())
        {
            if (DisplayTextRecords.IsExcluded(component)) continue;
            try
            {
                if (TextTranslateManager.TryGetDisplayTranslation(original, out var translated) &&
                    !string.IsNullOrWhiteSpace(translated))
                {
                    DisplayTextRecords.Set(component, original, translated);
                    WriteText(component, translated);
                }
                else
                {
                    DisplayTextRecords.Remove(component);
                    WriteText(component, original);
                }
            }
            catch (Exception exception)
            {
                WarnUiFailureOnce(original, exception);
            }
        }
    }

    /// <summary>
    /// 补丁移除后在主线程把仍存活的组件写回原文，并清空显示状态。
    /// After the patches are removed, write the originals back to live components on the main thread and clear the display state.
    /// </summary>
    internal static void RestoreDisplayedText()
    {
        foreach (var (component, original) in DisplayTextRecords.TakeLive())
        {
            try
            {
                WriteText(component, original);
            }
            catch (Exception exception)
            {
                WarnUiFailureOnce(original, exception);
            }
        }

        DisplayTextRecords.Clear();
    }

    /// <summary>
    /// 场景卸载后移除已销毁组件的显示状态。
    /// Remove the display state of destroyed components after a scene unloads.
    /// </summary>
    /// <param name="scene">已卸载的场景 / The unloaded scene.</param>
    internal static void OnSceneUnloaded(UnityEngine.SceneManagement.Scene scene)
    {
        DisplayTextRecords.PruneDestroyed();
    }

    /// <summary>
    /// 替换可翻译的原文，失败时保留原值并只警告一次。
    /// Replace translatable source text, preserving the original value and warning only once on failure.
    /// </summary>
    /// <param name="text">显示入口收到的原文，命中后改写成译文 / The source text received by the display entry, rewritten to the translation on a hit.</param>
    /// <returns>原文被替换为译文时为 true / True if the source text was replaced with a translation.</returns>
    private static bool Translate(ref string text)
    {
        return Translate(ref text, null, null);
    }

    /// <summary>
    /// 按剧情文件与标签上下文替换可翻译的原文，失败时保留原值并只警告一次；文件或标签为 null 时等同于无上下文查询；已登记为 NET 输出的剧情文字只查询该位置的上下文行。
    /// Replace translatable source text with the script file and label as context, preserving the original value and warning only once on failure; a null file or label means a lookup without context; script text that is recorded NET output looks up only the context rows of its place.
    /// </summary>
    /// <param name="text">显示入口收到的原文，命中后改写成译文 / The source text received by the display entry, rewritten to the translation on a hit.</param>
    /// <param name="file">游戏报告的原始剧情文件名，无上下文时为 null / The raw script file name reported by the game, or null without context.</param>
    /// <param name="label">游戏报告的原始剧情标签，无上下文时为 null / The raw script label reported by the game, or null without context.</param>
    /// <returns>原文被替换为译文时为 true / True if the source text was replaced with a translation.</returns>
    private static bool Translate(ref string text, string file, string label)
    {
        var recorded = IsRecordedScriptSource(text, file, label);
        if (!recorded && !IsTranslatableSource(text)) return false;
        try
        {
            var found = recorded
                ? TextTranslateManager.TryGetContextTranslation(text, file, label, out var translated)
                : TextTranslateManager.TryGetTranslation(text, file, label, out translated);
            if (found && !string.IsNullOrWhiteSpace(translated))
            {
                text = translated;
                return true;
            }
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _failureWarningLogged, 1) != 0) return false;
            LogManager.Warning($"[Text] Display translation failed; original preserved/显示翻译失败，保留原文: {text}: {exception.Message}");
        }

        return false;
    }

    /// <summary>
    /// 检查带剧情上下文的文字是否为互操作启用时已登记的 NET 输出（通用文本或 UI），空白与纯数字除外；登记检查与管理器的跳过条件相同。
    /// Check whether text with script context is NET output recorded while interop is active (general text or UI), excluding blank text and bare numbers; the registry check equals the manager's skip.
    /// </summary>
    /// <param name="text">剧情入口收到的原文 / The source text received by the script entry.</param>
    /// <param name="file">游戏报告的原始剧情文件名 / The raw script file name reported by the game.</param>
    /// <param name="label">游戏报告的原始剧情标签 / The raw script label reported by the game.</param>
    /// <returns>文件与标签存在且文字已登记时为 true / True if file and label are present and the text is recorded.</returns>
    private static bool IsRecordedScriptSource(string text, string file, string label)
    {
        if (file == null || label == null || string.IsNullOrWhiteSpace(text) || !XUATInterop.IsInstalled) return false;
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return false;
        return TextTranslateManager.IsNetTranslatedText(text) || UITranslateManager.IsNetTranslatedText(text);
    }

    /// <summary>
    /// 排除空白、纯数字和已登记的 NET 译文，避免无意义查询与重复导出。
    /// Exclude blank text, bare numbers, and recorded NET output to avoid pointless queries and repeated dumps.
    /// </summary>
    /// <param name="text">显示入口收到的原文 / The source text received by the display entry.</param>
    /// <returns>需要查询词典时为 true / True if the dictionary should be queried.</returns>
    private static bool IsTranslatableSource(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (TextTranslateManager.IsNetTranslatedText(text)) return false;

        return !double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>
    /// UI 显示入口的翻译：与 <see cref="Translate(ref string)"/> 共用来源过滤、空译文保护和一次性告警；
    /// 查询不导出，未命中时在加载完成后带场景与路径导出。
    /// Translation for UI display entries: shares the source filter, the blank-result guard and the one-time warning with <see cref="Translate(ref string)"/>;
    /// the lookup does not dump, and a miss is dumped with scene and path once loading has completed.
    /// </summary>
    /// <param name="text">显示入口收到的原文，命中后改写成译文 / The source text received by the display entry, rewritten to the translation on a hit.</param>
    /// <param name="source">显示该文字的组件，用于导出上下文 / The component showing the text, used for the dump context.</param>
    /// <returns>命中并改写时为 true / True if the text was translated and rewritten.</returns>
    private static bool Translate(ref string text, Component source)
    {
        if (!IsTranslatableSource(text)) return false;
        try
        {
            if (TextTranslateManager.TryGetDisplayTranslation(text, out var translated) &&
                !string.IsNullOrWhiteSpace(translated))
            {
                text = translated;
                return true;
            }

            if (TextTranslateManager.IsLoaded && NotEnoughTranslator.EnableTextDump.Value &&
                !DumpManager.IsTextDumped(text))
                DumpManager.DumpDisplayText(text, SceneName(source), GameObjectPath(source));
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _failureWarningLogged, 1) != 0) return false;
            LogManager.Warning($"[Text] Display translation failed; original preserved/显示翻译失败，保留原文: {text}: {exception.Message}");
        }

        return false;
    }

    /// <summary>
    /// 写入入口的公共处理：排除上下文中只做标记；NET 自身输出直接放行；否则翻译并登记显示状态。
    /// Shared handling of the write entries: in an excluded context only mark; pass NET's own output through; otherwise translate and record the display state.
    /// </summary>
    /// <param name="component">被写入的文本组件 / The text component being written.</param>
    /// <param name="value">即将写入的文字 / The text about to be written.</param>
    private static void TranslateDisplayWrite(Component component, ref string value)
    {
        try
        {
            if (_excludedDepth > 0)
            {
                DisplayTextRecords.Remove(component);
                DisplayTextRecords.MarkExcluded(component);
                return;
            }

            DisplayTextRecords.ClearMark(component);
            if (DisplayTextRecords.IsOwnOutput(component, value)) return;

            var translated = value;
            if (Translate(ref translated, component))
            {
                DisplayTextRecords.Set(component, value, translated);
                value = translated;
            }
            else
            {
                DisplayTextRecords.Remove(component);
            }
        }
        catch (Exception exception)
        {
            WarnUiFailureOnce(value, exception);
        }
    }

    /// <summary>
    /// 启用入口的公共处理：先登记再经属性写入，使写入补丁把它识别为 NET 自身输出；未命中时移除旧登记。
    /// Shared handling of the enable entries: record first, then write through the property, so the write patch sees NET's own output; a miss removes an old record.
    /// </summary>
    /// <param name="component">被启用的文本组件 / The text component being enabled.</param>
    /// <param name="current">组件当前的文字 / The current text of the component.</param>
    private static void TranslateOnEnable(Component component, string current)
    {
        try
        {
            if (_excludedDepth > 0 || DisplayTextRecords.IsExcluded(component) ||
                DisplayTextRecords.IsOwnOutput(component, current)) return;

            var translated = current;
            if (!Translate(ref translated, component))
            {
                DisplayTextRecords.Remove(component);
                return;
            }

            DisplayTextRecords.Set(component, current, translated);
            WriteText(component, translated);
        }
        catch (Exception exception)
        {
            WarnUiFailureOnce(current, exception);
        }
    }

    /// <summary>
    /// 先在读取上下文中把筛选名换成筛选项标签登记的原文，再进入筛选读取上下文；读取失败时深度不变。
    /// Replace the filter name with the original recorded for the filter item's label inside a read-back context, then enter the filter context; a failed read leaves the depth unchanged.
    /// </summary>
    /// <param name="item">筛选项 / The filter item.</param>
    /// <param name="name">筛选名 / The filter name.</param>
    /// <returns>总是 true，表示已进入上下文 / Always true: the context was entered.</returns>
    private static bool EnterFilterReadBack(TopicFilterItem item, ref string name)
    {
        if (item != null)
        {
            _readBackDepth++;
            try
            {
                name = item.FilterName;
            }
            finally
            {
                _readBackDepth--;
            }
        }

        _readBackDepth++;
        return true;
    }

    /// <summary>
    /// 离开排除上下文：仅在前缀进入时降低深度，异常照常抛出。
    /// Leave an excluded context: lower the depth only if the prefix entered it; exceptions propagate unchanged.
    /// </summary>
    /// <param name="entered">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    private static Exception ExcludedFinalizer(bool entered, Exception exception)
    {
        if (entered) _excludedDepth--;
        return exception;
    }

    /// <summary>
    /// 离开读取上下文：仅在前缀进入时降低深度，异常照常抛出。
    /// Leave a read-back context: lower the depth only if the prefix entered it; exceptions propagate unchanged.
    /// </summary>
    /// <param name="entered">本补丁的前缀是否已进入上下文 / Whether this patch's prefix entered the context.</param>
    /// <param name="exception">原方法抛出的异常 / The exception thrown by the original method.</param>
    /// <returns>原样返回的异常 / The exception, returned unchanged.</returns>
    private static Exception ReadBackFinalizer(bool entered, Exception exception)
    {
        if (entered) _readBackDepth--;
        return exception;
    }

    /// <summary>
    /// 经组件的文本属性写入文字。
    /// Write text through the component's text property.
    /// </summary>
    /// <param name="component">文本组件 / The text component.</param>
    /// <param name="text">要写入的文字 / The text to write.</param>
    private static void WriteText(Component component, string text)
    {
        switch (component)
        {
            case TMP_Text tmp:
                tmp.text = text;
                break;
            case UguiText ugui:
                ugui.text = text;
                break;
        }
    }

    /// <summary>
    /// 组件所属的场景名，未知时为 null。
    /// The scene name of the component's GameObject, or null if unknown.
    /// </summary>
    /// <param name="component">文本组件 / The text component.</param>
    /// <returns>场景名或 null / The scene name, or null.</returns>
    private static string SceneName(Component component)
    {
        var gameObject = component.gameObject;
        return gameObject == null ? null : gameObject.scene.name;
    }

    /// <summary>
    /// 从根到组件的 GameObject 路径，名称以“/”连接。
    /// The GameObject path from the root to the component, names joined with "/".
    /// </summary>
    /// <param name="component">文本组件 / The text component.</param>
    /// <returns>路径，未知时为 null / The path, or null if unknown.</returns>
    private static string GameObjectPath(Component component)
    {
        var names = new List<string>();
        for (var transform = component.transform; transform != null; transform = transform.parent)
            names.Add(transform.name);
        if (names.Count == 0) return null;
        names.Reverse();
        return string.Join("/", names);
    }

    /// <summary>
    /// UI 显示补丁出错时保留原文并只警告一次。
    /// Keep the original and warn only once when a UI display patch fails.
    /// </summary>
    /// <param name="text">相关文字 / The text involved.</param>
    /// <param name="exception">异常 / The exception.</param>
    private static void WarnUiFailureOnce(string text, Exception exception)
    {
        if (Interlocked.Exchange(ref _uiFailureWarningLogged, 1) != 0) return;
        LogManager.Warning($"[Text] UI display patch failed; original preserved/UI 显示补丁出错，保留原文: {text}: {exception.Message}");
    }
    /// <summary>
    /// 设置新的标签上下文并读取原始文件名与标签；读取失败时两者为 null，仅按文本查询。
    /// Set a new tag context and read the raw file name and label; if the read fails both are null and lookup uses text only.
    /// </summary>
    /// <param name="instance">执行标签的剧情管理器 / The script manager executing the tag.</param>
    /// <param name="kind">新的标签种类 / The new tag kind.</param>
    /// <returns>进入前的上下文 / The context before entry.</returns>
    private static ScriptScope EnterScope(ADVKagManager instance, ScriptTagKind kind)
    {
        var previous = _scope;
        _scope = new ScriptScope { Kind = kind };
        try
        {
            _scope.File = instance.currentFileName;
            _scope.Label = instance.currentLabel;
        }
        catch (Exception exception)
        {
            _scope.File = null;
            _scope.Label = null;
            if (!_contextWarningLogged)
            {
                _contextWarningLogged = true;
                LogManager.Warning("[Text] Script context unreadable; lookup by text only/" +
                                   $"无法读取剧情上下文，仅按文本查询: {exception.Message}");
            }
        }

        return previous;
    }
}

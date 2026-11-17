using System;
using System.Globalization;
using System.Threading;
using CRC.UI;
using CRC3D3.NotEnoughTranslator.Plugin.Manger;
using CRC3D3.NotEnoughTranslator.Plugin.Utils;
using HarmonyLib;
using Script;

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

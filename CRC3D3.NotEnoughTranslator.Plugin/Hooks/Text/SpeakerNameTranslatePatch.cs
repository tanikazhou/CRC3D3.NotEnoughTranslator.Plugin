using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using CRC3D3.NotEnoughTranslator.Plugin.Manger;
using CRC3D3.NotEnoughTranslator.Plugin.Utils;
using HarmonyLib;

namespace CRC3D3.NotEnoughTranslator.Plugin.Hooks.Text;

internal static class SpeakerNameTranslatePatch
{
    /// <summary>
    /// 说话人补丁的独立 Harmony ID。
    /// The dedicated Harmony ID for speaker patches.
    /// </summary>
    public const string HarmonyId =
        "github.meidopromotionassociation.crc3d3.notenoughtranslator.plugin.hooks.text.speakernametranslatepatch";

    /// <summary>
    /// ADV 剧情管理器的类型全名；不在编译期引用，因为旧版游戏的成员不同。
    /// The full type name of the ADV script manager; not referenced at compile time, because older game builds differ.
    /// </summary>
    private const string AdvManagerName = "Script.ADVKagManager";

    /// <summary>
    /// 恋爱玩法剧情管理器的类型全名；第二次体验版中它没有 TagTalk。
    /// The full type name of the romance script manager; in the second trial it has no TagTalk.
    /// </summary>
    private const string RomanceManagerName = "Script.YotogiKagManager";

    /// <summary>
    /// 剧情标签参数类型的简单名称，按名称比较以避免引用 Com3d2Native。
    /// The simple name of the script tag parameter type, compared by name to avoid a reference to Com3d2Native.
    /// </summary>
    private const string TagTypeName = "KagTagSupport";

    /// <summary>
    /// 已安装补丁的 Harmony 实例，未安装时为 null。
    /// The Harmony instance of the installed patches, or null when not installed.
    /// </summary>
    private static Harmony _harmony;

    /// <summary>
    /// 已打补丁的剧情管理器类型及其安装时解析的 textData 与 talkName 字段。
    /// The patched script manager types with their textData and talkName fields resolved at installation.
    /// </summary>
    private static readonly Dictionary<Type, (FieldInfo TextData, FieldInfo TalkName)> Targets = new();

    /// <summary>
    /// 本次会话是否已报告说话人翻译失败，非零表示已报告。
    /// Whether a speaker translation failure was already reported in this session; non-zero means reported.
    /// </summary>
    private static int _translationWarningLogged;

    /// <summary>
    /// 是否至少在一个剧情管理器上安装了说话人补丁。
    /// Whether the speaker patch is installed on at least one script manager.
    /// </summary>
    public static bool IsInstalled => _harmony != null;

    /// <summary>
    /// 指定的剧情管理器是否已安装说话人补丁。
    /// Whether the speaker patch is installed on the given script manager.
    /// </summary>
    /// <param name="manager">剧情管理器类型 / The script manager type.</param>
    /// <returns>已安装时为 true，否则为 false / True if installed; otherwise false.</returns>
    public static bool IsInstalledOn(Type manager)
    {
        return _harmony != null && manager != null && Targets.ContainsKey(manager);
    }

    /// <summary>
    /// 按名称查找 ADV 与恋爱玩法剧情管理器，并分别安装说话人补丁，记录两者的安装结果。
    /// Find the ADV and romance script managers by name, install the speaker patch on each independently, and log both results.
    /// </summary>
    /// <returns>至少一个管理器已安装时为 true，否则为 false / True if at least one manager is installed; otherwise false.</returns>
    public static bool Install()
    {
        if (_harmony != null) return true;
        var managers = new List<Type>();
        foreach (var name in new[] { AdvManagerName, RomanceManagerName })
        {
            var type = AccessTools.TypeByName(name);
            if (type == null)
                LogManager.Warning($"[Speaker] Hook disabled for one manager/一个剧情管理器的补丁已停用: {name}: type not found");
            else managers.Add(type);
        }

        var installed = Install(managers);
        LogManager.Info($"[Speaker] Hooks/补丁: adv={IsInstalledOn(AccessTools.TypeByName(AdvManagerName))}, " +
                        $"romance={IsInstalledOn(AccessTools.TypeByName(RomanceManagerName))}");
        return installed;
    }

    /// <summary>
    /// 移除说话人补丁，清除解析的字段和告警去重记录。
    /// Remove the speaker patches and clear the resolved fields and the warning deduplication record.
    /// </summary>
    public static void Uninstall()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
        Targets.Clear();
        Interlocked.Exchange(ref _translationWarningLogged, 0);
    }

    /// <summary>
    /// 逐个检查剧情管理器，只在检查通过的管理器的 TagTalk 上安装后置补丁；失败的管理器单独停用。
    /// Check each script manager and install a postfix only on the TagTalk of the managers that pass; a failed manager is disabled alone.
    /// </summary>
    /// <param name="managers">候选剧情管理器类型 / The candidate script manager types.</param>
    /// <returns>已安装或至少一个管理器安装成功时为 true，否则为 false / True if already installed or at least one manager was installed; otherwise false.</returns>
    internal static bool Install(IReadOnlyList<Type> managers)
    {
        if (_harmony != null) return true;
        var resolved = new List<(Type Manager, MethodInfo TagTalk, FieldInfo TextData, FieldInfo TalkName)>();
        foreach (var manager in managers)
        {
            if (TryResolveTarget(manager, out var tagTalk, out var textData, out var talkName, out var failure))
                resolved.Add((manager, tagTalk, textData, talkName));
            else
                LogManager.Warning($"[Speaker] Hook disabled for one manager/一个剧情管理器的补丁已停用: {failure}");
        }

        if (resolved.Count == 0) return false;
        var harmony = new Harmony(HarmonyId);
        try
        {
            var postfix = new HarmonyMethod(typeof(SpeakerNameTranslatePatch), nameof(Postfix));
            foreach (var target in resolved) harmony.Patch(target.TagTalk, postfix: postfix);
        }
        catch (Exception exception)
        {
            harmony.UnpatchSelf();
            LogManager.Error($"[Speaker] Cannot install speaker hook/无法安装说话人补丁: {exception}");
            return false;
        }

        foreach (var target in resolved) Targets[target.Manager] = (target.TextData, target.TalkName);
        _harmony = harmony;
        return true;
    }

    /// <summary>
    /// 按顺序检查 TagTalk(KagTagSupport) 方法、textData 实例字段和其中可写的 string 类型 talkName 字段。
    /// Check, in order, the TagTalk(KagTagSupport) method, the textData instance field, and its writable string field talkName.
    /// </summary>
    /// <param name="manager">剧情管理器类型 / The script manager type.</param>
    /// <param name="tagTalk">检查通过时的 TagTalk 方法 / The TagTalk method when the checks pass.</param>
    /// <param name="textData">检查通过时的 textData 字段 / The textData field when the checks pass.</param>
    /// <param name="talkName">检查通过时的 talkName 字段 / The talkName field when the checks pass.</param>
    /// <param name="failure">失败的检查说明，成功时为 null / The failed check, or null on success.</param>
    /// <returns>所有检查均通过时为 true / True if all checks pass.</returns>
    internal static bool TryResolveTarget(Type manager, out MethodInfo tagTalk, out FieldInfo textData,
        out FieldInfo talkName, out string failure)
    {
        tagTalk = null;
        textData = null;
        talkName = null;
        if (manager == null)
        {
            failure = "type not found";
            return false;
        }

        foreach (var method in manager.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                                  BindingFlags.DeclaredOnly))
        {
            if (method.Name != "TagTalk") continue;
            var parameters = method.GetParameters();
            if (parameters.Length != 1 || parameters[0].ParameterType.Name != TagTypeName) continue;
            tagTalk = method;
            break;
        }

        if (tagTalk == null)
        {
            failure = $"{manager.FullName}: TagTalk({TagTypeName}) not found";
            return false;
        }

        textData = AccessTools.DeclaredField(manager, "textData");
        if (textData == null || textData.IsStatic)
        {
            tagTalk = null;
            textData = null;
            failure = $"{manager.FullName}: field textData not found";
            return false;
        }

        talkName = textData.FieldType.GetField("talkName", BindingFlags.Public | BindingFlags.Instance);
        if (talkName == null || talkName.FieldType != typeof(string) || talkName.IsInitOnly || talkName.IsLiteral)
        {
            tagTalk = null;
            textData = null;
            talkName = null;
            failure = $"{manager.FullName}: textData has no writable string field talkName";
            return false;
        }

        failure = null;
        return true;
    }

    /// <summary>
    /// 在游戏按原始名字解析语音对象之后，把说话人名替换为 Speaker 中的显示文字；未命中或失败时保留原名。
    /// After the game resolved the voice target from the raw label, replace the speaker name with the display text from Speaker; keep the original on a miss or failure.
    /// </summary>
    /// <param name="__instance">执行对白标签的剧情管理器 / The script manager executing the talk tag.</param>
    /// <param name="__originalMethod">被补丁的 TagTalk 方法 / The patched TagTalk method.</param>
    private static void Postfix(object __instance, MethodBase __originalMethod)
    {
        try
        {
            if (!Targets.TryGetValue(__originalMethod.DeclaringType, out var target)) return;
            var data = target.TextData.GetValue(__instance);
            if (data is null) return;
            var original = target.TalkName.GetValue(data) as string;
            if (string.IsNullOrEmpty(original)) return;
            if (!SpeakerTranslateManager.TryGetTranslation(original, out var text)) return;
            SpeakerTranslateManager.MarkTranslated(text);
            target.TalkName.SetValue(data, text);
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _translationWarningLogged, 1) != 0) return;
            LogManager.Warning($"[Speaker] Speaker translation failed; original kept/说话人翻译失败，保留原名: {exception.Message}");
        }
    }
}

using System;
using System.Reflection;
using System.Threading;
using CRC.UI;
using CRC3D3.NotEnoughTranslator.Plugin.Manger;
using CRC3D3.NotEnoughTranslator.Plugin.Utils;
using HarmonyLib;

namespace CRC3D3.NotEnoughTranslator.Plugin.Hooks.UI;

internal static class PersonalityTextTranslatePatch
{
    /// <summary>
    /// 性格标签补丁的独立 Harmony ID。
    /// The dedicated Harmony ID for personality label patches.
    /// </summary>
    public const string HarmonyId =
        "github.meidopromotionassociation.crc3d3.notenoughtranslator.plugin.hooks.ui.personalitytexttranslatepatch";

    /// <summary>
    /// 游戏写入性格标签的私有字段名。
    /// The private field into which the game writes the personality label.
    /// </summary>
    private const string LabelFieldName = "m_personalityText";

    /// <summary>
    /// 标签字段的基类全名；不在编译期引用 Unity.TextMeshPro。
    /// The full name of the label field's base type; Unity.TextMeshPro is not referenced at compile time.
    /// </summary>
    private const string TmpTextTypeName = "TMPro.TMP_Text";

    /// <summary>
    /// 已安装补丁的 Harmony 实例，未安装时为 null。
    /// The Harmony instance of the installed patch, or null when not installed.
    /// </summary>
    private static Harmony _harmony;

    /// <summary>
    /// 安装时解析的性格标签字段。
    /// The personality label field resolved at installation.
    /// </summary>
    private static FieldInfo _labelField;

    /// <summary>
    /// 安装时解析的 TMP_Text.text 属性。
    /// The TMP_Text.text property resolved at installation.
    /// </summary>
    private static PropertyInfo _textProperty;

    /// <summary>
    /// 本次会话是否已报告性格标签翻译失败，非零表示已报告。
    /// Whether a personality label translation failure was already reported in this session; non-zero means reported.
    /// </summary>
    private static int _translationWarningLogged;

    /// <summary>
    /// 性格标签补丁是否已安装。
    /// Whether the personality label patch is installed.
    /// </summary>
    public static bool IsInstalled => _harmony != null;

    /// <summary>
    /// 解析标签成员后在女主角状态面板初始化方法上安装后置补丁，任一检查失败时不安装。
    /// Resolve the label members, then install a postfix on the heroine status panel's initialization; if any check fails, nothing is installed.
    /// </summary>
    /// <returns>已安装或安装成功时为 true，否则为 false / True if already installed or successfully installed; otherwise false.</returns>
    public static bool Install()
    {
        if (_harmony != null) return true;
        var target = AccessTools.Method(typeof(HeroineEditUIStatus), nameof(HeroineEditUIStatus.Initialization),
            new[] { typeof(EditUIManager), typeof(int) });
        if (target == null)
        {
            LogManager.Error("[Personality] Hook disabled/补丁已停用: " +
                             "HeroineEditUIStatus.Initialization(EditUIManager, int) not found");
            return false;
        }

        if (!TryResolveLabelMembers(typeof(HeroineEditUIStatus), LabelFieldName, out var field, out var text,
                out var failure))
        {
            LogManager.Error($"[Personality] Hook disabled/补丁已停用: {failure}");
            return false;
        }

        _labelField = field;
        _textProperty = text;
        var harmony = new Harmony(HarmonyId);
        try
        {
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(PersonalityTextTranslatePatch), nameof(Postfix)));
            _harmony = harmony;
            return true;
        }
        catch (Exception exception)
        {
            harmony.UnpatchSelf();
            LogManager.Error($"[Personality] Cannot install personality label hook/无法安装性格标签补丁: {exception}");
            return false;
        }
    }

    /// <summary>
    /// 移除性格标签补丁并清除告警去重记录。
    /// Remove the personality label patch and clear the warning deduplication record.
    /// </summary>
    public static void Uninstall()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
        Interlocked.Exchange(ref _translationWarningLogged, 0);
    }

    /// <summary>
    /// 按顺序检查标签字段、其 TMP_Text 基类和可读写的 text 属性。
    /// Check, in order, the label field, its TMP_Text base type, and the read-write text property.
    /// </summary>
    /// <param name="ownerType">声明标签字段的类型 / The type that declares the label field.</param>
    /// <param name="fieldName">标签字段名 / The label field name.</param>
    /// <param name="field">检查通过时的标签字段 / The label field when the checks pass.</param>
    /// <param name="text">检查通过时的 TMP_Text.text 属性 / The TMP_Text.text property when the checks pass.</param>
    /// <param name="failure">失败的检查说明，成功时为 null / The failed check, or null on success.</param>
    /// <returns>三项检查均通过时为 true / True if all three checks pass.</returns>
    internal static bool TryResolveLabelMembers(Type ownerType, string fieldName, out FieldInfo field,
        out PropertyInfo text, out string failure)
    {
        text = null;
        field = AccessTools.DeclaredField(ownerType, fieldName);
        if (field == null || field.IsStatic)
        {
            field = null;
            failure = $"field {fieldName} not found";
            return false;
        }

        var tmpText = field.FieldType;
        while (tmpText != null && tmpText.FullName != TmpTextTypeName) tmpText = tmpText.BaseType;
        if (tmpText == null)
        {
            field = null;
            failure = $"field type is not {TmpTextTypeName}";
            return false;
        }

        text = AccessTools.DeclaredProperty(tmpText, "text");
        if (text == null || text.PropertyType != typeof(string) ||
            text.GetGetMethod(true) == null || text.GetSetMethod(true) == null)
        {
            field = null;
            text = null;
            failure = "TMP_Text.text not found or not a read-write string";
            return false;
        }

        failure = null;
        return true;
    }

    /// <summary>
    /// 在游戏写入性格标签后替换为译文，未命中或失败时保留原标签。
    /// Replace the personality label after the game writes it, keeping the original on a miss or failure.
    /// </summary>
    /// <param name="__instance">正在初始化的女主角状态面板 / The heroine status panel being initialized.</param>
    private static void Postfix(HeroineEditUIStatus __instance)
    {
        try
        {
            var label = _labelField.GetValue(__instance);
            if (label is null) return;
            var original = _textProperty.GetValue(label, null) as string;
            if (!PersonalityTranslateManager.TryGetTranslation(original, out var translation)) return;
            // XUAT 的 TMP setter Hook 会在赋值过程中判定新文字，所以先登记再赋值。
            PersonalityTranslateManager.MarkTranslated(translation);
            _textProperty.SetValue(label, translation, null);
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _translationWarningLogged, 1) != 0) return;
            LogManager.Warning($"[Personality] Translation failed; original kept/翻译失败，保留原标签: {exception.Message}");
        }
    }
}

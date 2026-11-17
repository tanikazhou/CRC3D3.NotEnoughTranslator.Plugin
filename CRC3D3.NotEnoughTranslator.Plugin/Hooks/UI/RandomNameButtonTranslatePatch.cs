using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using CRC.UI;
using CRC3D3.NotEnoughTranslator.Plugin.Manger;
using CRC3D3.NotEnoughTranslator.Plugin.Utils;
using HarmonyLib;

namespace CRC3D3.NotEnoughTranslator.Plugin.Hooks.UI;

internal static class RandomNameButtonTranslatePatch
{
    /// <summary>
    /// 随机名字按钮补丁的独立 Harmony ID。
    /// The dedicated Harmony ID for random-name button patches.
    /// </summary>
    public const string HarmonyId =
        "github.meidopromotionassociation.crc3d3.notenoughtranslator.plugin.hooks.ui.randomnamebuttontranslatepatch";

    /// <summary>
    /// 输入框字段的基类全名；不在编译期引用 Unity.TextMeshPro。
    /// The full name of the input field's base type; Unity.TextMeshPro is not referenced at compile time.
    /// </summary>
    private const string InputFieldTypeName = "TMPro.TMP_InputField";

    /// <summary>
    /// 四个名字面板及其姓、名、昵称输入框字段名，顺序与按钮参数一致。
    /// The four name panels and their last-name, first-name and nickname input field names, in button argument order.
    /// </summary>
    private static readonly (Type Panel, string[] Fields)[] DefaultTargets =
    {
        (typeof(HeroineInitialEditUIName),
            new[] { "m_lastNameInputField", "m_firstNameInputField", "m_nickNameInputField" }),
        (typeof(InitialEditUIName),
            new[] { "m_lastNameInputField", "m_firstNameInputField", "m_nicknameInputField" }),
        (typeof(StatusUIName),
            new[] { "m_lastNameInputField", "m_firstNameInputField", "m_nicknameInputField" }),
        (typeof(HeroineStatusUIName),
            new[] { "m_lastNameInputField", "m_firstNameInputField", "m_nickNameInputField" }),
    };

    /// <summary>
    /// 已安装补丁的 Harmony 实例，未安装时为 null。
    /// The Harmony instance of the installed patches, or null when not installed.
    /// </summary>
    private static Harmony _harmony;

    /// <summary>
    /// 安装时解析的输入框字段，按面板类型索引。
    /// The input fields resolved at installation, keyed by panel type.
    /// </summary>
    private static Dictionary<Type, FieldInfo[]> _fields;

    /// <summary>
    /// 安装时解析的 TMP_InputField.text 属性。
    /// The TMP_InputField.text property resolved at installation.
    /// </summary>
    private static PropertyInfo _textProperty;

    /// <summary>
    /// 本次会话是否已报告按钮翻译失败，非零表示已报告。
    /// Whether a button translation failure was already reported in this session; non-zero means reported.
    /// </summary>
    private static int _translationWarningLogged;

    /// <summary>
    /// 随机名字按钮补丁是否已安装。
    /// Whether the random-name button patches are installed.
    /// </summary>
    public static bool IsInstalled => _harmony != null;

    /// <summary>
    /// 在四个名字面板的 SetRandomName(string) 上安装后置补丁。
    /// Install postfixes on SetRandomName(string) of the four name panels.
    /// </summary>
    /// <returns>已安装或安装成功时为 true，否则为 false / True if already installed or successfully installed; otherwise false.</returns>
    public static bool Install()
    {
        return Install(DefaultTargets);
    }

    /// <summary>
    /// 解析每个目标的方法和输入框字段后安装后置补丁，任一检查失败或安装失败时不保留任何补丁。
    /// Resolve each target's method and input fields, then install postfixes; if any check or patch fails, no patch is kept.
    /// </summary>
    /// <param name="targets">面板类型及其姓、名、昵称输入框字段名 / The panel types and their last-name, first-name and nickname input field names.</param>
    /// <returns>已安装或安装成功时为 true，否则为 false / True if already installed or successfully installed; otherwise false.</returns>
    internal static bool Install(IReadOnlyList<(Type Panel, string[] Fields)> targets)
    {
        if (_harmony != null) return true;
        var methods = new List<MethodInfo>();
        var fields = new Dictionary<Type, FieldInfo[]>();
        PropertyInfo text = null;
        foreach (var (panel, names) in targets)
        {
            var method = AccessTools.Method(panel, nameof(HeroineInitialEditUIName.SetRandomName),
                new[] { typeof(string) });
            if (method == null)
            {
                LogManager.Error("[RandomName] Button hook disabled/按钮补丁已停用: " +
                                 $"{panel.FullName}.SetRandomName(string) not found");
                return false;
            }

            if (!TryResolveInputFields(panel, names, out var panelFields, out text, out var failure))
            {
                LogManager.Error($"[RandomName] Button hook disabled/按钮补丁已停用: {failure}");
                return false;
            }

            methods.Add(method);
            fields[panel] = panelFields;
        }

        _fields = fields;
        _textProperty = text;
        var harmony = new Harmony(HarmonyId);
        try
        {
            var postfix = new HarmonyMethod(typeof(RandomNameButtonTranslatePatch), nameof(Postfix));
            foreach (var method in methods)
                harmony.Patch(method, postfix: postfix);
            _harmony = harmony;
            return true;
        }
        catch (Exception exception)
        {
            harmony.UnpatchSelf();
            _fields = null;
            _textProperty = null;
            LogManager.Error($"[RandomName] Cannot install random-name button hook/无法安装随机名字按钮补丁: {exception}");
            return false;
        }
    }

    /// <summary>
    /// 移除按钮补丁，清除已解析的成员和告警去重记录。
    /// Remove the button patches, and clear the resolved members and the warning deduplication record.
    /// </summary>
    public static void Uninstall()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
        _fields = null;
        _textProperty = null;
        Interlocked.Exchange(ref _translationWarningLogged, 0);
    }

    /// <summary>
    /// 按顺序检查每个输入框字段、其 TMP_InputField 基类和可读写的 text 属性。
    /// Check, in order, each input field, its TMP_InputField base type, and the read-write text property.
    /// </summary>
    /// <param name="panel">声明输入框字段的面板类型 / The panel type that declares the input fields.</param>
    /// <param name="fieldNames">输入框字段名 / The input field names.</param>
    /// <param name="fields">检查通过时的输入框字段，顺序与字段名一致 / The input fields when the checks pass, in name order.</param>
    /// <param name="text">检查通过时的 TMP_InputField.text 属性 / The TMP_InputField.text property when the checks pass.</param>
    /// <param name="failure">失败的检查说明，成功时为 null / The failed check, or null on success.</param>
    /// <returns>所有检查均通过时为 true / True if all checks pass.</returns>
    internal static bool TryResolveInputFields(Type panel, string[] fieldNames, out FieldInfo[] fields,
        out PropertyInfo text, out string failure)
    {
        fields = null;
        text = null;
        var resolved = new FieldInfo[fieldNames.Length];
        for (var i = 0; i < fieldNames.Length; i++)
        {
            var field = AccessTools.DeclaredField(panel, fieldNames[i]);
            if (field == null || field.IsStatic)
            {
                failure = $"{panel.FullName}: field {fieldNames[i]} not found";
                return false;
            }

            var inputField = field.FieldType;
            while (inputField != null && inputField.FullName != InputFieldTypeName) inputField = inputField.BaseType;
            if (inputField == null)
            {
                failure = $"{panel.FullName}: field {fieldNames[i]} is not {InputFieldTypeName}";
                return false;
            }

            var property = AccessTools.DeclaredProperty(inputField, "text");
            if (property == null || property.PropertyType != typeof(string) ||
                property.GetGetMethod(true) == null || property.GetSetMethod(true) == null)
            {
                failure = "TMP_InputField.text not found or not a read-write string";
                return false;
            }

            resolved[i] = field;
            text = property;
        }

        fields = resolved;
        failure = null;
        return true;
    }

    /// <summary>
    /// 在游戏把随机名字写入所选输入框后替换为译文，游戏自己的监听器随后保存译文；未命中或失败时保留原名字。
    /// Replace the random name after the game writes it into the selected input field, so the game's own listener stores the translation; keep the original on a miss or failure.
    /// </summary>
    /// <param name="__instance">按下随机按钮的名字面板 / The name panel whose random button was pressed.</param>
    /// <param name="type">按钮参数：苗字、名前或ニックネーム / The button argument: 苗字, 名前 or ニックネーム.</param>
    /// <param name="__originalMethod">被补丁的方法，用于确定面板类型 / The patched method, used to find the panel type.</param>
    private static void Postfix(object __instance, string type, MethodBase __originalMethod)
    {
        try
        {
            var index = type switch
            {
                "苗字" => 0,
                "名前" => 1,
                "ニックネーム" => 2,
                _ => -1,
            };
            if (index < 0) return;
            var fields = _fields;
            var textProperty = _textProperty;
            if (fields == null || textProperty == null) return;
            if (!fields.TryGetValue(__originalMethod.DeclaringType, out var panelFields)) return;
            var inputField = panelFields[index].GetValue(__instance);
            if (inputField is null) return;
            var original = textProperty.GetValue(inputField, null) as string;
            if (string.IsNullOrEmpty(original)) return;
            if (RandomNameTranslateManager.TryGetTranslation(original, out var translation))
                textProperty.SetValue(inputField, translation, null);
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _translationWarningLogged, 1) != 0) return;
            LogManager.Warning($"[RandomName] Button translation failed; original kept/按钮翻译失败，保留原名字: {exception.Message}");
        }
    }
}

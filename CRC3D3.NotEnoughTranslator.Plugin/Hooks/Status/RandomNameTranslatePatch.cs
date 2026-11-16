using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using CRC;
using CRC3D3.NotEnoughTranslator.Plugin.Manger;
using CRC3D3.NotEnoughTranslator.Plugin.Utils;
using HarmonyLib;

namespace CRC3D3.NotEnoughTranslator.Plugin.Hooks.Status;

internal static class RandomNameTranslatePatch
{
    /// <summary>
    /// 随机名字补丁的独立 Harmony ID。
    /// The dedicated Harmony ID for random-name patches.
    /// </summary>
    public const string HarmonyId =
        "github.meidopromotionassociation.crc3d3.notenoughtranslator.plugin.hooks.status.randomnametranslatepatch";

    /// <summary>
    /// 游戏中的随机名字类名；该类为 internal，只能按名称查找。
    /// The game's random-name class; it is internal, so it can only be found by name.
    /// </summary>
    private const string TargetTypeName = "MaidRandomName";

    /// <summary>
    /// 被补丁的三个随机名字方法名。
    /// The names of the three patched random-name getters.
    /// </summary>
    private static readonly string[] TargetMethodNames = { "GetLastName", "GetFirstName", "GetPlayerName" };

    /// <summary>
    /// 已安装补丁的 Harmony 实例，未安装时为 null。
    /// The Harmony instance of the installed patches, or null when not installed.
    /// </summary>
    private static Harmony _harmony;

    /// <summary>
    /// 本次会话是否已报告随机名字翻译失败，非零表示已报告。
    /// Whether a random-name translation failure was already reported in this session; non-zero means reported.
    /// </summary>
    private static int _translationWarningLogged;

    /// <summary>
    /// 随机名字补丁是否已安装。
    /// Whether the random-name patches are installed.
    /// </summary>
    public static bool IsInstalled => _harmony != null;

    /// <summary>
    /// 在三个随机名字方法上安装后置补丁，任一目标缺失或安装失败时不保留任何补丁。
    /// Install postfixes on the three random-name getters; if any target is missing or patching fails, no patch is kept.
    /// </summary>
    /// <returns>已安装或安装成功时为 true，否则为 false / True if already installed or successfully installed; otherwise false.</returns>
    public static bool Install()
    {
        if (_harmony != null) return true;
        var type = typeof(CastManager).Assembly.GetType(TargetTypeName);
        if (type == null)
        {
            LogManager.Error("[RandomName] Random-name class not found/未找到随机名字类");
            return false;
        }

        var targets = new List<MethodInfo>();
        foreach (var name in TargetMethodNames)
        {
            var target = AccessTools.Method(type, name, Type.EmptyTypes);
            if (target == null)
            {
                LogManager.Error($"[RandomName] Random-name getter not found/未找到随机名字方法: {name}");
                return false;
            }
            targets.Add(target);
        }

        var harmony = new Harmony(HarmonyId);
        try
        {
            var postfix = new HarmonyMethod(typeof(RandomNameTranslatePatch), nameof(Postfix));
            foreach (var target in targets)
                harmony.Patch(target, postfix: postfix);
            _harmony = harmony;
            return true;
        }
        catch (Exception exception)
        {
            harmony.UnpatchSelf();
            LogManager.Error($"[RandomName] Cannot install random-name hook/无法安装随机名字补丁: {exception}");
            return false;
        }
    }

    /// <summary>
    /// 移除随机名字补丁并清除告警去重记录。
    /// Remove the random-name patches and clear the warning deduplication record.
    /// </summary>
    public static void Uninstall()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
        Interlocked.Exchange(ref _translationWarningLogged, 0);
    }

    /// <summary>
    /// 在游戏保存前替换随机生成的名字，未命中或失败时保留原名字。
    /// Replace a randomly generated name before the game stores it, keeping the original on a miss or failure.
    /// </summary>
    /// <param name="__result">游戏生成的名字，命中时替换为译文 / The generated name, replaced with the translation on a hit.</param>
    private static void Postfix(ref string __result)
    {
        try
        {
            if (string.IsNullOrEmpty(__result)) return;
            if (RandomNameTranslateManager.TryGetTranslation(__result, out var translation)) __result = translation;
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _translationWarningLogged, 1) != 0) return;
            LogManager.Warning($"[RandomName] Translation failed; original kept/翻译失败，保留原名字: {exception.Message}");
        }
    }
}

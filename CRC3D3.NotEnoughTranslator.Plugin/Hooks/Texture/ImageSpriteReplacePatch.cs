using System;
using System.Reflection;
using CRC3D3.NotEnoughTranslator.Plugin.Translation;
using CRC3D3.NotEnoughTranslator.Plugin.Utils;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace CRC3D3.NotEnoughTranslator.Plugin.Hooks.Texture;

/// <summary>
/// 在 uGUI Image 存入或启用 Sprite 时换成按名称匹配的替换 Sprite。
/// Swap in the name-matched replacement sprite when a uGUI Image stores or enables a sprite.
/// </summary>
internal static class ImageSpriteReplacePatch
{
    /// <summary>
    /// UI 精灵图替换补丁的独立 Harmony ID。
    /// The dedicated Harmony ID for the UI sprite replacement patches.
    /// </summary>
    public const string HarmonyId =
        "github.meidopromotionassociation.crc3d3.notenoughtranslator.plugin.hooks.texture.imagespritereplacepatch";

    private static Harmony _harmony;

    /// <summary>
    /// 补丁已安装时为 true。
    /// True while the patches are installed.
    /// </summary>
    public static bool IsInstalled => _harmony != null;

    /// <summary>
    /// 在 Image 的 sprite 与 overrideSprite setter 及 OnEnable 上安装补丁。
    /// Install the patches on the Image sprite and overrideSprite setters and on OnEnable.
    /// </summary>
    /// <returns>已安装或安装成功时为 true，否则为 false / True if already installed or successfully installed; otherwise false.</returns>
    public static bool Install()
    {
        return Install(AccessTools.PropertySetter(typeof(Image), nameof(Image.sprite)),
            AccessTools.PropertySetter(typeof(Image), nameof(Image.overrideSprite)),
            AccessTools.DeclaredMethod(typeof(Image), "OnEnable"));
    }

    /// <summary>
    /// 在给定目标上安装补丁；目标缺失或打补丁失败时撤销本类的全部补丁。
    /// Install the patches on the given targets; remove every patch of this class if a target is missing or patching fails.
    /// </summary>
    /// <param name="spriteSetter">Image.sprite 的 setter / The Image.sprite setter.</param>
    /// <param name="overrideSpriteSetter">Image.overrideSprite 的 setter / The Image.overrideSprite setter.</param>
    /// <param name="onEnable">Image 自身声明的 OnEnable / The OnEnable declared on Image.</param>
    /// <returns>已安装或安装成功时为 true，否则为 false / True if already installed or successfully installed; otherwise false.</returns>
    internal static bool Install(MethodBase spriteSetter, MethodBase overrideSpriteSetter, MethodBase onEnable)
    {
        if (_harmony != null) return true;
        var missing = spriteSetter == null ? "Image.sprite setter"
            : overrideSpriteSetter == null ? "Image.overrideSprite setter"
            : onEnable == null ? "Image.OnEnable"
            : null;
        if (missing != null)
        {
            LogManager.Warning($"[Texture] Cannot install UI sprite hooks/无法安装 UI 精灵图补丁: {missing}");
            return false;
        }

        var harmony = new Harmony(HarmonyId);
        try
        {
            // 排在原地替换补丁之后，让整张纹理先由它处理
            var setterPrefix = new HarmonyMethod(typeof(ImageSpriteReplacePatch), nameof(SetterPrefix))
            {
                after = new[] { TextureReplacePatch.HarmonyId }
            };
            harmony.Patch(spriteSetter, prefix: setterPrefix);
            harmony.Patch(overrideSpriteSetter, prefix: setterPrefix);
            harmony.Patch(onEnable,
                postfix: new HarmonyMethod(typeof(ImageSpriteReplacePatch), nameof(OnEnablePostfix)));
            _harmony = harmony;
            return true;
        }
        catch (Exception exception)
        {
            harmony.UnpatchSelf();
            LogManager.Warning($"[Texture] Cannot install UI sprite hooks/无法安装 UI 精灵图补丁: {exception}");
            return false;
        }
    }

    /// <summary>
    /// 移除本类的全部补丁。
    /// Remove every patch of this class.
    /// </summary>
    public static void Uninstall()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
    }

    /// <summary>
    /// 在 setter 存入 Sprite 之前换成替换 Sprite；null 原样传递。
    /// Swap in the replacement sprite before the setter stores it; null passes through.
    /// </summary>
    /// <param name="value">待存入的 Sprite / The sprite to store.</param>
    private static void SetterPrefix(ref Sprite value)
    {
        value = ReplacementSpriteRegistry.Resolve(value);
    }

    /// <summary>
    /// Image 启用后，若其 sprite 有替换对象，则通过 sprite setter 写入；不修改 overrideSprite。
    /// After an Image is enabled, assign the replacement of its sprite through the sprite setter; overrideSprite is not changed.
    /// </summary>
    /// <param name="__instance">刚启用的 Image / The Image that was enabled.</param>
    private static void OnEnablePostfix(Image __instance)
    {
        var sprite = __instance.sprite;
        var replacement = ReplacementSpriteRegistry.Resolve(sprite);
        if (!ReferenceEquals(replacement, sprite)) __instance.sprite = replacement;
    }
}

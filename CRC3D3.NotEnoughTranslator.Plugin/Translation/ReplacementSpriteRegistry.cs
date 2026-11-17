using System;
using System.Collections.Generic;
using CRC3D3.NotEnoughTranslator.Plugin.Hooks.Texture;
using CRC3D3.NotEnoughTranslator.Plugin.Manger;
using CRC3D3.NotEnoughTranslator.Plugin.Utils;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CRC3D3.NotEnoughTranslator.Plugin.Translation;

/// <summary>
/// 按原始 Sprite 对象生成并持有替换 Sprite，负责还原和销毁；仅在 Unity 主线程使用。
/// Build and own replacement sprites per original Sprite object, and restore and destroy them; main thread only.
/// </summary>
internal static class ReplacementSpriteRegistry
{
    /// <summary>
    /// 生成的纹理名称后缀，避免其他插件把它当成游戏原始纹理。
    /// The name suffix of built textures, so other plugins do not take them for game textures.
    /// </summary>
    private const string TextureNameSuffix = "_net";

    /// <summary>
    /// 原始 Sprite 到 NET 生成的 Sprite。
    /// Original sprite to the sprite that NET built.
    /// </summary>
    private static readonly Dictionary<Sprite, Sprite> Replacements = new();

    /// <summary>
    /// NET 生成的 Sprite 到其原始 Sprite 和生成的纹理，用于还原和销毁。
    /// NET-built sprite to its original sprite and its built texture, for restore and destroy.
    /// </summary>
    private static readonly Dictionary<Sprite, (Sprite Original, Texture2D Texture)> Originals = new();

    /// <summary>
    /// 本次资源快照中已告警的名称，只用于避免重复告警，不阻止重试。
    /// Names already warned about in this snapshot; only stops repeated warnings, not retries.
    /// </summary>
    private static readonly HashSet<string> WarnedNames = new(StringComparer.Ordinal);

    /// <summary>
    /// 还原期间为 true，此时补丁原样传递所有值。
    /// True while restoring; the hooks then pass every value through.
    /// </summary>
    private static bool _restoring;

    /// <summary>
    /// 当前持有的替换 Sprite 数量。
    /// The number of replacement sprites currently owned.
    /// </summary>
    internal static int Count => Replacements.Count;

    /// <summary>
    /// 返回 Sprite 的替换对象；没有替换、已是替换对象、整张纹理已被原地替换或出错时返回原值。
    /// Return the replacement of a sprite; return the input if it has none, is already ours, covers a whole texture that was replaced in place, or on any error.
    /// </summary>
    /// <param name="sprite">待显示的 Sprite，可为 null / The sprite to show, or null.</param>
    /// <returns>替换 Sprite 或原值 / The replacement sprite or the input.</returns>
    internal static Sprite Resolve(Sprite sprite)
    {
        if (_restoring || ReferenceEquals(sprite, null) || TextureReplaceManager.EntryCount == 0) return sprite;

        string name = null;
        try
        {
            if (sprite == null || Originals.ContainsKey(sprite)) return sprite;
            if (IsReplacedInPlace(sprite)) return sprite;
            if (Replacements.TryGetValue(sprite, out var replacement)) return replacement;

            name = sprite.name;
            if (!TextureReplaceManager.TryGetReplacement(name, out var bytes)) return sprite;
            return Build(sprite, name, bytes) ?? sprite;
        }
        catch (Exception exception)
        {
            WarnOnce(name ?? string.Empty,
                $"[Texture] Cannot build UI sprite/无法生成 UI 精灵图: {name}: {exception.Message}");
            return sprite;
        }
    }

    /// <summary>
    /// 用替换图片为一个原始 Sprite 生成纹理和 Sprite 并记录所有权；解码失败时返回 null。
    /// Build a texture and a sprite for one original sprite from replacement bytes and record ownership; return null if decoding fails.
    /// </summary>
    /// <param name="original">原始 Sprite / The original sprite.</param>
    /// <param name="name">原始 Sprite 名称 / The original sprite name.</param>
    /// <param name="bytes">替换图片的编码字节 / The encoded replacement-image bytes.</param>
    /// <returns>生成的 Sprite，失败时为 null / The built sprite, or null on failure.</returns>
    private static Sprite Build(Sprite original, string name, byte[] bytes)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        Sprite built = null;
        try
        {
            if (!ImageConversion.LoadImage(texture, bytes, true))
            {
                WarnOnce(name, $"[Texture] Cannot build UI sprite/无法生成 UI 精灵图: {name}: image decode failed/图片解码失败");
                Object.Destroy(texture);
                return null;
            }

            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.name = name + TextureNameSuffix;

            var rect = original.rect;
            if (texture.width != Mathf.RoundToInt(rect.width) || texture.height != Mathf.RoundToInt(rect.height))
                WarnOnce(name, "[Texture] Replacement size differs from sprite/替换图片尺寸与精灵图不同: " +
                               $"{name}: {texture.width}x{texture.height} vs {rect.width}x{rect.height}");

            var scale = texture.width / rect.width;
            var pivot = new Vector2(original.pivot.x / rect.width, original.pivot.y / rect.height);
            built = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), pivot,
                original.pixelsPerUnit * scale, 0, SpriteMeshType.FullRect, original.border * scale);
            built.name = name;

            TextureReplacePatch.MarkApplied(texture);
            LogManager.Debug($"[Texture] Replaced UI sprite/已替换 UI 精灵图: {name}");
            Replacements[original] = built;
            Originals[built] = (original, texture);
            return built;
        }
        catch
        {
            if (built != null) Object.Destroy(built);
            Object.Destroy(texture);
            throw;
        }
    }

    /// <summary>
    /// 把所有 Image 上的替换 Sprite 换回原始 Sprite，然后销毁 NET 生成的对象并清空记录。
    /// Put the original sprites back on every Image, then destroy NET-built objects and clear the tables.
    /// </summary>
    /// <returns>被修改的 Image 数量 / The number of Images that were changed.</returns>
    internal static int RestoreAll()
    {
        WarnedNames.Clear();
        if (Originals.Count == 0) return 0;

        var restored = 0;
        var failed = 0;
        Exception firstError = null;
        _restoring = true;
        try
        {
            foreach (var image in Resources.FindObjectsOfTypeAll<Image>())
            {
                try
                {
                    if (image == null) continue;
                    var changed = false;
                    // 必须先还原 sprite：未设置 override 时 overrideSprite 的 getter 返回 sprite。
                    if (TryGetOriginal(image.sprite, out var original))
                    {
                        image.sprite = original;
                        changed = true;
                    }

                    if (TryGetOriginal(image.overrideSprite, out var overrideOriginal))
                    {
                        image.overrideSprite = overrideOriginal;
                        changed = true;
                    }

                    if (changed) restored++;
                }
                catch (Exception exception)
                {
                    failed++;
                    firstError ??= exception;
                }
            }
        }
        finally
        {
            _restoring = false;
        }

        if (failed > 0)
            LogManager.Warning($"[Texture] Cannot restore/无法还原 {failed} UI images/UI 图片: {firstError?.Message}");

        foreach (var pair in Originals)
        {
            DestroySafely(pair.Key);
            DestroySafely(pair.Value.Texture);
        }

        Originals.Clear();
        Replacements.Clear();
        return restored;
    }

    /// <summary>
    /// 判断 Sprite 是否覆盖整张纹理且该纹理已被原地替换补丁处理；图集子图始终返回 false。
    /// Tell whether a sprite covers its whole texture and that texture was handled by the in-place patch; atlas sub-rects always return false.
    /// </summary>
    /// <param name="sprite">非 null 的 Sprite / A sprite that is not null.</param>
    /// <returns>应保留原 Sprite 时为 true / True if the original sprite must stay.</returns>
    private static bool IsReplacedInPlace(Sprite sprite)
    {
        var texture = sprite.texture;
        if (!TextureReplacePatch.IsApplied(texture)) return false;
        var rect = sprite.rect;
        return Mathf.Approximately(rect.width, texture.width) && Mathf.Approximately(rect.height, texture.height);
    }

    /// <summary>
    /// 把已启用且处于激活状态的 Image 的 sprite 重新赋给自身，让两个 setter 前缀按顺序处理，不修改 overrideSprite；隐藏的 Image 在 OnEnable 时替换。
    /// Assign the sprite of each active and enabled Image to itself so both setter prefixes run in order, without changing overrideSprite; hidden Images get theirs in OnEnable.
    /// </summary>
    /// <returns>由本补丁替换的 Image 数量 / The number of Images that this hook replaced.</returns>
    internal static int ApplyAll()
    {
        var applied = 0;
        var failed = 0;
        Exception firstError = null;
        foreach (var image in Resources.FindObjectsOfTypeAll<Image>())
        {
            try
            {
                if (image == null || !image.isActiveAndEnabled) continue;
                var sprite = image.sprite;
                if (ReferenceEquals(sprite, null)) continue;
                image.sprite = sprite;
                if (!ReferenceEquals(image.sprite, sprite)) applied++;
            }
            catch (Exception exception)
            {
                failed++;
                firstError ??= exception;
            }
        }

        if (failed > 0)
            LogManager.Warning($"[Texture] Cannot apply UI sprites to/无法替换 {failed} UI images/UI 图片: {firstError?.Message}");
        return applied;
    }

    /// <summary>
    /// 查找 NET 生成的 Sprite 对应的原始 Sprite。
    /// Find the original sprite of a NET-built sprite.
    /// </summary>
    /// <param name="sprite">待检查的 Sprite / The sprite to check.</param>
    /// <param name="original">找到时为原始 Sprite / The original sprite if found.</param>
    /// <returns>该 Sprite 由 NET 生成时为 true / True if NET built the sprite.</returns>
    private static bool TryGetOriginal(Sprite sprite, out Sprite original)
    {
        original = null;
        if (ReferenceEquals(sprite, null) || !Originals.TryGetValue(sprite, out var entry)) return false;
        original = entry.Original;
        return true;
    }

    /// <summary>
    /// 销毁一个 NET 拥有的 Unity 对象，忽略已销毁对象引发的异常。
    /// Destroy one NET-owned Unity object, ignoring errors from an object that is already destroyed.
    /// </summary>
    /// <param name="target">待销毁的对象 / The object to destroy.</param>
    private static void DestroySafely(Object target)
    {
        try
        {
            if (target != null) Object.Destroy(target);
        }
        catch (Exception exception)
        {
            LogManager.Debug($"[Texture] Cannot destroy a UI sprite object/无法销毁 UI 精灵图对象: {exception.Message}");
        }
    }

    /// <summary>
    /// 每个名称在每次资源快照中只记录一次告警。
    /// Log a warning only once per name and snapshot.
    /// </summary>
    /// <param name="name">告警对应的 Sprite 名称 / The sprite name of the warning.</param>
    /// <param name="message">告警文本 / The warning text.</param>
    private static void WarnOnce(string name, string message)
    {
        if (WarnedNames.Add(name)) LogManager.Warning(message);
    }
}

using System;
using System.Threading.Tasks;
using CRC3D3.NotEnoughTranslator.Plugin.Hooks.Texture;
using CRC3D3.NotEnoughTranslator.Plugin.Loader;
using CRC3D3.NotEnoughTranslator.Plugin.Translation;
using CRC3D3.NotEnoughTranslator.Plugin.Utils;
using HarmonyLib;

namespace CRC3D3.NotEnoughTranslator.Plugin.Manger;

public static class TextureReplaceManager
{
    private static ResourceModule<TextureReplacementCatalog> _module;
    private static volatile TextureReplacementCatalog _catalog = TextureReplacementCatalog.Empty;
    private static Harmony _textureHarmony;

    public static bool IsLoaded => _module?.IsLoaded == true;
    public static bool IsLoading => _module?.IsLoading == true;
    public static bool IsHookInstalled => _textureHarmony != null;
    public static int EntryCount => _catalog.Count;

    /// <summary>
    /// UI 精灵图补丁已安装时为 true。
    /// True while the UI sprite hooks are installed.
    /// </summary>
    public static bool IsSpriteHookInstalled => ImageSpriteReplacePatch.IsInstalled;

    /// <summary>
    /// 当前由 NET 生成并持有的替换 Sprite 数量。
    /// The number of replacement sprites that NET built and owns now.
    /// </summary>
    public static int ReplacedSpriteCount => ReplacementSpriteRegistry.Count;

    /// <summary>
    /// 在 Unity 主线程初始化纹理模块，安装精灵加载补丁并异步索引图片路径而不创建 Unity 纹理对象。
    /// Initialize the texture module on Unity's main thread, install the sprite load patch, and index image paths asynchronously without creating Unity textures.
    /// </summary>
    public static void Init()
    {
        if (_module != null || !NotEnoughTranslator.EnableTextureReplacement.Value) return;
        try
        {
            _textureHarmony = Harmony.CreateAndPatchAll(typeof(TextureReplacePatch), TextureReplacePatch.HarmonyId);
        }
        catch (Exception exception)
        {
            new Harmony(TextureReplacePatch.HarmonyId).UnpatchSelf();
            _textureHarmony = null;
            LogManager.Warning($"[Texture] Cannot install hooks; textures stay query-only/无法安装纹理补丁，纹理仍仅提供查询: {exception.Message}");
        }

        _module = new ResourceModule<TextureReplacementCatalog>("Texture", token =>
        {
            var directory = NotEnoughTranslator.TextureReplacePath;
            return Task.Run(() => TextureReplacementCatalog.Load(directory, token), token);
        }, Publish);
        _module.Reload();
    }

    /// <summary>
    /// 检查是否存在该名称的替换图片，只查询索引而不读取文件。
    /// Check whether a replacement exists for the name, querying the index without reading the file.
    /// </summary>
    /// <param name="textureName">纹理名称或包含扩展名的路径 / The texture name or a path including its extension.</param>
    /// <returns>索引中存在该名称时为 true / True when the name exists in the index.</returns>
    public static bool HasReplacement(string textureName)
    {
        return _catalog.Contains(textureName);
    }

    /// <summary>
    /// 在 Unity 主线程发布新索引：先还原并销毁旧的替换 Sprite、清空原地替换记录，再按开关和条目数安装或移除 UI 精灵图补丁。
    /// Publish a new index on Unity's main thread: restore and destroy old replacement sprites and clear the in-place records, then install or remove the UI sprite hooks by switch and entry count.
    /// </summary>
    /// <param name="catalog">新加载的纹理索引 / The newly loaded texture index.</param>
    private static void Publish(TextureReplacementCatalog catalog)
    {
        LogRestored(ReplacementSpriteRegistry.RestoreAll());
        // 清空后，修改过的图片会在下次加载或赋值时重新原地替换
        TextureReplacePatch.Reset();
        _catalog = catalog;
        LogManager.Info($"[Texture] Indexed/已索引 {EntryCount} replacement textures/替换纹理");

        if (NotEnoughTranslator.EnableUISpriteReplace.Value && EntryCount > 0)
        {
            if (!ImageSpriteReplacePatch.IsInstalled && ImageSpriteReplacePatch.Install() && IsHookInstalled)
                LogManager.Info("[Texture] Atlas sub-rect sprites are shown by the UI sprite hooks/图集子图精灵由 UI 精灵图补丁显示");
            if (ImageSpriteReplacePatch.IsInstalled) ReplacementSpriteRegistry.ApplyAll();
        }
        else
        {
            ImageSpriteReplacePatch.Uninstall();
        }

        LogManager.Info($"[Texture] UI sprite hooks/UI 精灵图补丁: installed={IsSpriteHookInstalled}");
    }

    /// <summary>
    /// 还原了 Image 时记录数量，数量为 0 时不记录。
    /// Log the number of restored Images, with no line for 0.
    /// </summary>
    /// <param name="count">被还原的 Image 数量 / The number of restored Images.</param>
    private static void LogRestored(int count)
    {
        if (count > 0) LogManager.Info($"[Texture] Restored/已还原 {count} UI images/UI 图片");
    }

    /// <summary>
    /// 按纹理名称读取替换图片的编码数据，不缓存图片内容。
    /// Read encoded replacement-image data by texture name without caching image contents.
    /// </summary>
    /// <param name="textureName">待替换的纹理名称 / The name of the texture to replace.</param>
    /// <param name="data">成功读取的图片编码字节，失败时为 null / Encoded image bytes on success, or null on failure.</param>
    /// <returns>找到并成功读取替换图片时为 true / True if a replacement image is found and read successfully.</returns>
    public static bool TryGetReplacement(string textureName, out byte[] data)
    {
        return _catalog.TryGetBytes(textureName, out data);
    }

    /// <summary>
    /// 在 Unity 主线程按启用配置初始化、重载或卸载纹理路径索引。
    /// Initialize, reload, or unload the texture-path index on Unity's main thread according to its enable setting.
    /// </summary>
    public static void Reload()
    {
        if (!NotEnoughTranslator.EnableTextureReplacement.Value) Unload();
        else if (_module == null) Init();
        else _module.Reload();
    }

    /// <summary>
    /// 先移除纹理补丁，使还原时不再原地替换；再移除 UI 精灵图补丁并还原、销毁 NET 生成的 Sprite，取消索引加载并清空替换路径；已原地替换的纹理不还原。
    /// Remove the texture patch first so the restore triggers no in-place replacement; then remove the UI sprite hooks, restore and destroy NET-built sprites, cancel index loading, and clear replacement paths; textures replaced in place are not restored.
    /// </summary>
    public static void Unload()
    {
        _textureHarmony?.UnpatchSelf();
        _textureHarmony = null;
        var wasInstalled = ImageSpriteReplacePatch.IsInstalled;
        ImageSpriteReplacePatch.Uninstall();
        LogRestored(ReplacementSpriteRegistry.RestoreAll());
        if (wasInstalled) LogManager.Info($"[Texture] UI sprite hooks/UI 精灵图补丁: installed={IsSpriteHookInstalled}");
        TextureReplacePatch.Reset();
        _module?.Dispose();
        _module = null;
        _catalog = TextureReplacementCatalog.Empty;
    }
}

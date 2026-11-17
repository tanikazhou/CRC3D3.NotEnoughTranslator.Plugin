using System;
using CRC3D3.NotEnoughTranslator.Plugin.Hooks.Status;
using CRC3D3.NotEnoughTranslator.Plugin.Hooks.UI;
using CRC3D3.NotEnoughTranslator.Plugin.Loader;
using CRC3D3.NotEnoughTranslator.Plugin.Loader.Processor;
using CRC3D3.NotEnoughTranslator.Plugin.Translation;
using CRC3D3.NotEnoughTranslator.Plugin.Utils;

namespace CRC3D3.NotEnoughTranslator.Plugin.Manger;

public static class RandomNameTranslateManager
{
    /// <summary>
    /// 随机名字资源的加载模块，未初始化或已卸载时为 null。
    /// The loading module for random-name resources, or null before initialization and after unloading.
    /// </summary>
    private static ResourceModule<TranslationLoadResult> _module;

    /// <summary>
    /// 当前发布的随机名字查询快照，独立于通用文本词典。
    /// The published random-name lookup snapshot, separate from the general-text dictionary.
    /// </summary>
    private static volatile ScopedLookupCatalog _catalog = ScopedLookupCatalog.Empty;

    /// <summary>
    /// 是否已发布过一次加载结果。
    /// Whether a load result has been published.
    /// </summary>
    public static bool IsLoaded => _module?.IsLoaded == true;

    /// <summary>
    /// 是否有加载正在进行。
    /// Whether a load is in progress.
    /// </summary>
    public static bool IsLoading => _module?.IsLoading == true;

    /// <summary>
    /// 当前快照中的精确词条数量。
    /// The number of exact entries in the current snapshot.
    /// </summary>
    public static int EntryCount => _catalog.Count;

    /// <summary>
    /// 随机名字方法补丁是否已安装。
    /// Whether the random-name getter patches are installed.
    /// </summary>
    public static bool IsHookInstalled => RandomNameTranslatePatch.IsInstalled;

    /// <summary>
    /// 随机名字按钮补丁是否已安装。
    /// Whether the random-name button patches are installed.
    /// </summary>
    public static bool IsButtonHookInstalled => RandomNameButtonTranslatePatch.IsInstalled;

    /// <summary>
    /// 在 Unity 主线程初始化随机名字模块，分别安装随机名字方法补丁和按钮补丁，并从 RandomName 异步加载 JSONL 精确译文。
    /// Initialize the random-name module on Unity's main thread, install the getter and button patches independently, and load JSONL exact translations asynchronously from RandomName.
    /// </summary>
    public static void Init()
    {
        if (_module != null || !NotEnoughTranslator.EnableRandomNameTranslation.Value) return;
        _module = new ResourceModule<TranslationLoadResult>("RandomName", token =>
            AsyncTranslationLoader.LoadOnceAsync("RandomName", NotEnoughTranslator.RandomNamePath,
                NotEnoughTranslator.AllowFilesInZipLoadInOrder.Value, token,
                new JsonlTranslationFileProcessor(TimeSpan.FromMilliseconds(
                    NotEnoughTranslator.RegexTimeoutMilliseconds.Value))), ApplyResult);
        var getters = RandomNameTranslatePatch.Install();
        var buttons = RandomNameButtonTranslatePatch.Install();
        LogManager.Info($"[RandomName] Hooks/补丁: getters={getters}, buttons={buttons}");
        _module.Reload();
    }

    /// <summary>
    /// 按游戏生成的名字精确查询译文，不导出未命中的名字。
    /// Look up an exact translation for a generated name, without dumping misses.
    /// </summary>
    /// <param name="original">随机名字方法返回或随机按钮写入的名字 / The name returned by a random-name getter or written by a random button.</param>
    /// <param name="translation">查询成功时返回的译文 / The translation returned on a successful lookup.</param>
    /// <returns>查询命中时为 true，否则为 false / True on a match; otherwise false.</returns>
    public static bool TryGetTranslation(string original, out string translation)
    {
        return _catalog.TryGetTranslation(original, out translation);
    }

    /// <summary>
    /// 在 Unity 主线程按启用配置初始化、重载或卸载随机名字模块。
    /// Initialize, reload, or unload the random-name module on Unity's main thread according to its enable setting.
    /// </summary>
    public static void Reload()
    {
        if (!NotEnoughTranslator.EnableRandomNameTranslation.Value) Unload();
        else if (_module == null) Init();
        else _module.Reload();
    }

    /// <summary>
    /// 移除随机名字方法补丁和按钮补丁，取消加载并清空随机名字资源；已保存到角色的名字不变。
    /// Remove the getter and button patches, cancel loading, and clear random-name resources; names already stored in characters do not change.
    /// </summary>
    public static void Unload()
    {
        RandomNameTranslatePatch.Uninstall();
        RandomNameButtonTranslatePatch.Uninstall();
        _module?.Dispose();
        _module = null;
        _catalog = ScopedLookupCatalog.Empty;
    }

    /// <summary>
    /// 发布新的随机名字快照，只使用精确词条，并记录被忽略的正则行数量。
    /// Publish a new random-name snapshot from exact entries only, and log the number of ignored regex rows.
    /// </summary>
    /// <param name="result">成功完成的随机名字加载结果 / The successfully loaded random-name resources.</param>
    private static void ApplyResult(TranslationLoadResult result)
    {
        _catalog = new ScopedLookupCatalog(result.TextTranslations);
        LogManager.Info($"[RandomName] Loaded/已加载 {EntryCount} entries/条, " +
                        $"{result.TextRegexTranslations.Count} regex ignored/忽略正则, " +
                        $"{result.SkippedLines} skipped/跳过, {result.ElapsedMilliseconds} ms");
    }
}

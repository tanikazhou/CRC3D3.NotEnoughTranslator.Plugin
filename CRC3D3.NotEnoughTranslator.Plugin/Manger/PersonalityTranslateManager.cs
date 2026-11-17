using System;
using CRC3D3.NotEnoughTranslator.Plugin.Hooks.UI;
using CRC3D3.NotEnoughTranslator.Plugin.Loader;
using CRC3D3.NotEnoughTranslator.Plugin.Loader.Processor;
using CRC3D3.NotEnoughTranslator.Plugin.Translation;
using CRC3D3.NotEnoughTranslator.Plugin.Utils;

namespace CRC3D3.NotEnoughTranslator.Plugin.Manger;

public static class PersonalityTranslateManager
{
    /// <summary>
    /// NET 已写入的性格标签译文登记，供 XUAT 互操作防止重复翻译。
    /// Personality label translations written by NET, recorded for XUAT retranslation prevention.
    /// </summary>
    private static readonly TranslatedTextRegistry TranslatedTexts = new();

    /// <summary>
    /// 性格标签资源的加载模块，未初始化或已卸载时为 null。
    /// The loading module for personality label resources, or null before initialization and after unloading.
    /// </summary>
    private static ResourceModule<TranslationLoadResult> _module;

    /// <summary>
    /// 当前发布的性格标签查询快照，独立于通用文本词典。
    /// The published personality label lookup snapshot, separate from the general-text dictionary.
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
    /// 性格标签补丁是否已安装。
    /// Whether the personality label patch is installed.
    /// </summary>
    public static bool IsHookInstalled => PersonalityTextTranslatePatch.IsInstalled;

    /// <summary>
    /// 开关开启时在 Unity 主线程初始化性格标签模块，安装标签补丁，并从 Personality 异步加载 JSONL 精确译文。
    /// When the switch is on, initialize the personality module on Unity's main thread, install the label patch, and load JSONL exact translations asynchronously from Personality.
    /// </summary>
    public static void Init()
    {
        if (_module != null || !NotEnoughTranslator.EnablePersonalityTranslation.Value) return;
        _module = new ResourceModule<TranslationLoadResult>("Personality", token =>
            AsyncTranslationLoader.LoadOnceAsync("Personality", NotEnoughTranslator.PersonalityPath,
                NotEnoughTranslator.AllowFilesInZipLoadInOrder.Value, token,
                new JsonlTranslationFileProcessor(TimeSpan.FromMilliseconds(
                    NotEnoughTranslator.RegexTimeoutMilliseconds.Value))), ApplyResult);
        PersonalityTextTranslatePatch.Install();
        _module.Reload();
    }

    /// <summary>
    /// 按游戏写入的性格标签精确查询译文，不导出未命中的标签。
    /// Look up an exact translation for the personality label that the game wrote, without dumping misses.
    /// </summary>
    /// <param name="original">游戏写入的性格标签 / The personality label written by the game.</param>
    /// <param name="translation">查询成功时返回的译文 / The translation returned on a successful lookup.</param>
    /// <returns>查询命中时为 true，否则为 false / True on a match; otherwise false.</returns>
    public static bool TryGetTranslation(string original, out string translation)
    {
        return _catalog.TryGetTranslation(original, out translation);
    }

    /// <summary>
    /// 检查文字是否已登记为 NET 的性格标签译文。
    /// Check whether text is recorded as NET personality label output.
    /// </summary>
    /// <param name="text">待检查的文字 / The text to check.</param>
    /// <returns>已登记时为 true，否则为 false / True if the text is recorded; otherwise false.</returns>
    public static bool IsNetTranslatedText(string text)
    {
        return TranslatedTexts.Contains(text);
    }

    /// <summary>
    /// XUAT 互操作启用时登记性格标签译文，避免再次翻译。
    /// Record personality label translations while XUAT interop is active to prevent retranslation.
    /// </summary>
    /// <param name="text">写入标签的最终译文 / The final translation written into the label.</param>
    public static void MarkTranslated(string text)
    {
        if (XUATInterop.IsInstalled) TranslatedTexts.Add(text);
    }

    /// <summary>
    /// 在 Unity 主线程按启用配置初始化、重载或卸载性格标签模块。
    /// Initialize, reload, or unload the personality module on Unity's main thread according to its enable setting.
    /// </summary>
    public static void Reload()
    {
        if (!NotEnoughTranslator.EnablePersonalityTranslation.Value) Unload();
        else if (_module == null) Init();
        else _module.Reload();
    }

    /// <summary>
    /// 移除性格标签补丁，取消加载，清空性格标签资源及译文登记；已显示的标签不刷新。
    /// Remove the personality label patch, cancel loading, and clear resources and recorded outputs; labels already shown are not refreshed.
    /// </summary>
    public static void Unload()
    {
        PersonalityTextTranslatePatch.Uninstall();
        _module?.Dispose();
        _module = null;
        _catalog = ScopedLookupCatalog.Empty;
        TranslatedTexts.Clear();
    }

    /// <summary>
    /// 发布新的性格标签快照，只使用精确词条，清空上一轮的译文登记，并记录被忽略的正则行数量。
    /// Publish a new personality snapshot from exact entries only, clear previously recorded outputs, and log the number of ignored regex rows.
    /// </summary>
    /// <param name="result">成功完成的性格标签加载结果 / The successfully loaded personality label resources.</param>
    private static void ApplyResult(TranslationLoadResult result)
    {
        _catalog = new ScopedLookupCatalog(result.TextTranslations);
        TranslatedTexts.Clear();
        LogManager.Info($"[Personality] Loaded/已加载 {EntryCount} entries/条, " +
                        $"{result.TextRegexTranslations.Count} regex ignored/忽略正则, " +
                        $"{result.SkippedLines} skipped/跳过, {result.ElapsedMilliseconds} ms");
    }
}

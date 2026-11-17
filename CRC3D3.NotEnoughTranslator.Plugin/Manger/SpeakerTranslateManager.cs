using System;
using CRC3D3.NotEnoughTranslator.Plugin.Hooks.Text;
using CRC3D3.NotEnoughTranslator.Plugin.Loader;
using CRC3D3.NotEnoughTranslator.Plugin.Loader.Processor;
using CRC3D3.NotEnoughTranslator.Plugin.Translation;
using CRC3D3.NotEnoughTranslator.Plugin.Utils;

namespace CRC3D3.NotEnoughTranslator.Plugin.Manger;

public static class SpeakerTranslateManager
{
    /// <summary>
    /// NET 已写入的说话人名登记，供 XUAT 互操作防止重复翻译。
    /// Speaker names written by NET, recorded for XUAT retranslation prevention.
    /// </summary>
    private static readonly TranslatedTextRegistry TranslatedTexts = new();

    /// <summary>
    /// 说话人资源的加载模块，未初始化或已卸载时为 null。
    /// The loading module for speaker resources, or null before initialization and after unloading.
    /// </summary>
    private static ResourceModule<TranslationLoadResult> _module;

    /// <summary>
    /// 当前发布的说话人查询快照，独立于通用文本词典。
    /// The published speaker lookup snapshot, separate from the general-text dictionary.
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
    /// 至少一个剧情管理器的说话人补丁是否已安装。
    /// Whether the speaker patch is installed on at least one script manager.
    /// </summary>
    public static bool IsHookInstalled => SpeakerNameTranslatePatch.IsInstalled;

    /// <summary>
    /// 开关开启时在 Unity 主线程初始化说话人模块，安装说话人补丁，并从 Speaker 异步加载 JSONL 精确译文。
    /// When the switch is on, initialize the speaker module on Unity's main thread, install the speaker patch, and load JSONL exact translations asynchronously from Speaker.
    /// </summary>
    public static void Init()
    {
        if (_module != null || !NotEnoughTranslator.EnableSpeakerTranslation.Value) return;
        _module = new ResourceModule<TranslationLoadResult>("Speaker", token =>
            AsyncTranslationLoader.LoadOnceAsync("Speaker", NotEnoughTranslator.SpeakerPath,
                NotEnoughTranslator.AllowFilesInZipLoadInOrder.Value, token,
                new JsonlTranslationFileProcessor(TimeSpan.FromMilliseconds(
                    NotEnoughTranslator.RegexTimeoutMilliseconds.Value))), ApplyResult);
        SpeakerNameTranslatePatch.Install();
        _module.Reload();
    }

    /// <summary>
    /// 按剧情写入的说话人名精确查询显示文字，不导出未命中的名字。
    /// Look up the exact display text for the speaker label that the script wrote, without dumping misses.
    /// </summary>
    /// <param name="original">剧情 name= 属性的原始值 / The raw value of the script's name= property.</param>
    /// <param name="translation">查询成功时返回的显示文字 / The display text returned on a successful lookup.</param>
    /// <returns>查询命中时为 true，否则为 false / True on a match; otherwise false.</returns>
    public static bool TryGetTranslation(string original, out string translation)
    {
        return _catalog.TryGetTranslation(original, out translation);
    }

    /// <summary>
    /// 检查文字是否已登记为 NET 写入的说话人名。
    /// Check whether text is recorded as a speaker name written by NET.
    /// </summary>
    /// <param name="text">待检查的文字 / The text to check.</param>
    /// <returns>已登记时为 true，否则为 false / True if the text is recorded; otherwise false.</returns>
    public static bool IsNetTranslatedText(string text)
    {
        return TranslatedTexts.Contains(text);
    }

    /// <summary>
    /// XUAT 互操作启用时登记写入的说话人名，避免再次翻译。
    /// Record written speaker names while XUAT interop is active to prevent retranslation.
    /// </summary>
    /// <param name="text">写入说话人字段的显示文字 / The display text written into the speaker field.</param>
    public static void MarkTranslated(string text)
    {
        if (XUATInterop.IsInstalled) TranslatedTexts.Add(text);
    }

    /// <summary>
    /// 在 Unity 主线程按启用配置初始化、重载或卸载说话人模块。
    /// Initialize, reload, or unload the speaker module on Unity's main thread according to its enable setting.
    /// </summary>
    public static void Reload()
    {
        if (!NotEnoughTranslator.EnableSpeakerTranslation.Value) Unload();
        else if (_module == null) Init();
        else _module.Reload();
    }

    /// <summary>
    /// 移除说话人补丁，取消加载，清空说话人资源及登记；已显示的名字不刷新。
    /// Remove the speaker patch, cancel loading, and clear resources and recorded outputs; names already shown are not refreshed.
    /// </summary>
    public static void Unload()
    {
        SpeakerNameTranslatePatch.Uninstall();
        _module?.Dispose();
        _module = null;
        _catalog = ScopedLookupCatalog.Empty;
        TranslatedTexts.Clear();
    }

    /// <summary>
    /// 发布新的说话人快照，只使用精确词条，清空上一轮的登记，并记录被忽略的正则行数量。
    /// Publish a new speaker snapshot with exact entries only, clear the previous recorded outputs, and log the number of ignored regex rows.
    /// </summary>
    /// <param name="result">后台加载完成的资源 / The resources loaded in the background.</param>
    private static void ApplyResult(TranslationLoadResult result)
    {
        _catalog = new ScopedLookupCatalog(result.TextTranslations);
        TranslatedTexts.Clear();
        LogManager.Info($"[Speaker] Loaded/已加载 {EntryCount} entries/条, " +
                        $"{result.TextRegexTranslations.Count} regex ignored/忽略正则, " +
                        $"{result.SkippedLines} skipped/跳过, {result.ElapsedMilliseconds} ms");
    }
}

using System;
using System.Collections.Generic;

namespace CRC3D3.NotEnoughTranslator.Plugin.Translation;

/// <summary>
/// 保存单一类别的精确译文查询快照，不含正则规则，也不进入通用文本词典，不缓存命中或未命中结果。
/// Holds an exact-match lookup snapshot for one scoped kind, without regex rules and outside the general-text dictionary, without caching hits or misses.
/// </summary>
public sealed class ScopedLookupCatalog
{
    /// <summary>
    /// 不含任何词条的空快照，用于模块卸载或尚未加载时。
    /// An empty snapshot without entries, used before loading and after unloading.
    /// </summary>
    public static readonly ScopedLookupCatalog Empty = new(new Dictionary<string, string>());

    /// <summary>
    /// 按 Ordinal 规则比较的原文到译文词典。
    /// The source-to-translation dictionary with Ordinal key comparison.
    /// </summary>
    private readonly Dictionary<string, string> _translations;

    /// <summary>
    /// 快照中的精确词条数量。
    /// The number of exact entries in the snapshot.
    /// </summary>
    public int Count => _translations.Count;

    /// <summary>
    /// 复制精确译文，创建独立的查询快照。
    /// Copy exact translations into a separate lookup snapshot.
    /// </summary>
    /// <param name="translations">按 Ordinal 规则复制的精确译文词典 / The exact translations copied with Ordinal key comparison.</param>
    public ScopedLookupCatalog(IDictionary<string, string> translations)
    {
        _translations = new Dictionary<string, string>(translations, StringComparer.Ordinal);
    }

    /// <summary>
    /// 按原文精确查询译文，空或 null 原文永不命中。
    /// Look up an exact translation by source text; an empty or null source never matches.
    /// </summary>
    /// <param name="original">游戏提供的原文 / The source text supplied by the game.</param>
    /// <param name="translation">命中时返回的译文 / The translation returned on a match.</param>
    /// <returns>精确词条命中时为 true，否则为 false / True if an exact entry matches; otherwise false.</returns>
    public bool TryGetTranslation(string original, out string translation)
    {
        translation = null;
        return !string.IsNullOrEmpty(original) && _translations.TryGetValue(original, out translation);
    }
}

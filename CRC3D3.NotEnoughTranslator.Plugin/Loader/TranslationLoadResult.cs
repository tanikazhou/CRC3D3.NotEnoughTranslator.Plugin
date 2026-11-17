using System;
using System.Collections.Generic;
using CRC3D3.NotEnoughTranslator.Plugin.Translation;

namespace CRC3D3.NotEnoughTranslator.Plugin.Loader;

public sealed class TranslationLoadResult
{
    public readonly Dictionary<string, string> I2Terms = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> TextTranslations = new(StringComparer.Ordinal);
    public readonly List<RegexTranslationRule> TextRegexTranslations = new();

    /// <summary>
    /// 带文件与标签的精确译文行，按加载顺序排列。
    /// Exact rows that carry a file and a label, in loading order.
    /// </summary>
    public readonly List<ContextTranslation> ContextTranslations = new();

    /// <summary>
    /// 译文互不相同的原文集合。
    /// The originals that have more than one distinct text.
    /// </summary>
    public readonly HashSet<string> AmbiguousOriginals = new(StringComparer.Ordinal);

    public long ElapsedMilliseconds;
    public int SkippedLines;
    public int TotalEntries;
    public int TotalFiles;

    public bool HasAnyEntry => I2Terms.Count + TextTranslations.Count + TextRegexTranslations.Count > 0;
}

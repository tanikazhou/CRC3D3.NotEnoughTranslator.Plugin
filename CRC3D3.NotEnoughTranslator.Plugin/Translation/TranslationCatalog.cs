using System;
using System.Collections.Generic;

namespace CRC3D3.NotEnoughTranslator.Plugin.Translation;

/// <summary>
/// 保存精确译文与正则规则的查询快照，优先精确匹配，再按后加载规则优先查询，不缓存命中或未命中结果，并另存歧义原文的上下文行词典。
/// Holds a lookup snapshot of exact translations and regex rules, preferring exact matches and then later-loaded rules without caching hits or misses, plus a context dictionary of rows of ambiguous originals.
/// </summary>
public sealed class TranslationCatalog
{
    public static readonly TranslationCatalog Empty = new(new Dictionary<string, string>(),
        Array.Empty<RegexTranslationRule>());

    /// <summary>
    /// 脚本文件名中的路径分隔符。
    /// The path separators in a script file name.
    /// </summary>
    private static readonly char[] PathSeparators = { '/', '\\' };

    private readonly Dictionary<string, string> _translations;
    private readonly RegexTranslationRule[] _rules;
    private readonly Dictionary<(string File, string Label, string Original), string> _context;

    public int ExactCount => _translations.Count;
    public int RegexCount => _rules.Length;

    /// <summary>
    /// 上下文词典中的条目数，只含歧义原文的行。
    /// The number of entries in the context dictionary, which holds only rows of ambiguous originals.
    /// </summary>
    public int ContextCount => _context.Count;

    /// <summary>
    /// 复制精确译文和规则列表，创建独立的查询快照。
    /// Copy exact translations and the rule list into a separate lookup snapshot.
    /// </summary>
    /// <param name="translations">按 Ordinal 规则复制的精确译文词典 / The exact translations copied with Ordinal key comparison.</param>
    /// <param name="rules">按加载顺序提供的正则规则 / Regex rules supplied in loading order.</param>
    public TranslationCatalog(IDictionary<string, string> translations,
        IEnumerable<RegexTranslationRule> rules)
        : this(translations, rules, Array.Empty<ContextTranslation>(), Array.Empty<string>())
    {
    }

    /// <summary>
    /// 复制精确译文、规则和上下文行，创建独立的查询快照，上下文词典只保留歧义原文的行。
    /// Copy exact translations, rules and context rows into a separate lookup snapshot; the context dictionary keeps only rows of ambiguous originals.
    /// </summary>
    /// <param name="translations">按 Ordinal 规则复制的精确译文词典 / The exact translations copied with Ordinal key comparison.</param>
    /// <param name="rules">按加载顺序提供的正则规则 / Regex rules supplied in loading order.</param>
    /// <param name="contextTranslations">按加载顺序提供的带上下文译文行 / Context rows supplied in loading order.</param>
    /// <param name="ambiguousOriginals">译文互不相同的原文 / The originals that have more than one distinct text.</param>
    public TranslationCatalog(IDictionary<string, string> translations,
        IEnumerable<RegexTranslationRule> rules, IEnumerable<ContextTranslation> contextTranslations,
        ICollection<string> ambiguousOriginals)
    {
        _translations = new Dictionary<string, string>(translations, StringComparer.Ordinal);
        _rules = new List<RegexTranslationRule>(rules).ToArray();
        _context = new Dictionary<(string File, string Label, string Original), string>(new ContextKeyComparer());
        if (contextTranslations == null || ambiguousOriginals == null || ambiguousOriginals.Count == 0) return;

        foreach (var row in contextTranslations)
        {
            if (!ambiguousOriginals.Contains(row.Original)) continue;
            var file = NormalizeScriptFile(row.File);
            if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(row.Label)) continue;
            _context[(file, row.Label, row.Original)] = row.Text;
        }
    }

    /// <summary>
    /// 规范化脚本文件名：取最后一个 / 或 \ 之后的部分，并去掉一个结尾的 .ks，保留大小写。
    /// Normalize a script file name: take the part after the last / or \ and remove one trailing .ks, keeping the case.
    /// </summary>
    /// <param name="file">游戏报告或包内写的文件名 / The file name reported by the game or written in a pack.</param>
    /// <returns>规范化后的文件名，null 保持 null / The normalized file name; null stays null.</returns>
    public static string NormalizeScriptFile(string file)
    {
        if (file == null) return null;
        var name = file.Substring(file.LastIndexOfAny(PathSeparators) + 1);
        if (name.EndsWith(".ks", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 3);
        return name;
    }

    /// <summary>
    /// 先按文件与标签查询上下文词典，未命中时回退到不带上下文的查询。
    /// Probe the context dictionary by file and label first, and fall back to the lookup without context on a miss.
    /// </summary>
    /// <param name="sourceText">待查询的原文 / The source text to look up.</param>
    /// <param name="file">游戏报告的脚本文件名 / The script file name reported by the game.</param>
    /// <param name="label">游戏报告的脚本标签 / The script label reported by the game.</param>
    /// <param name="translation">命中时返回的译文 / The translation returned on a match.</param>
    /// <returns>命中时为 true，否则为 false / True if a match is found; otherwise false.</returns>
    public bool TryGetTranslation(string sourceText, string file, string label, out string translation)
    {
        return TryGetContextTranslation(sourceText, file, label, out translation) ||
               TryGetTranslation(sourceText, out translation);
    }

    /// <summary>
    /// 只按文件与标签查询上下文词典，不回退到精确词条或正则规则。
    /// Probe only the context dictionary by file and label, without falling back to exact entries or regex rules.
    /// </summary>
    /// <param name="sourceText">待查询的原文 / The source text to look up.</param>
    /// <param name="file">游戏报告的脚本文件名 / The script file name reported by the game.</param>
    /// <param name="label">游戏报告的脚本标签 / The script label reported by the game.</param>
    /// <param name="translation">命中时返回的上下文译文 / The context translation returned on a match.</param>
    /// <returns>上下文行命中时为 true，否则为 false / True if a context row matches; otherwise false.</returns>
    public bool TryGetContextTranslation(string sourceText, string file, string label, out string translation)
    {
        translation = null;
        if (_context.Count == 0 || string.IsNullOrEmpty(sourceText) || string.IsNullOrEmpty(label)) return false;
        var normalized = NormalizeScriptFile(file);
        return !string.IsNullOrEmpty(normalized) &&
               _context.TryGetValue((normalized, label, sourceText), out translation);
    }

    /// <summary>
    /// 上下文键的比较器：文件忽略大小写，标签与原文按 Ordinal 比较。
    /// Comparer for context keys: the file ignores case, the label and the original use Ordinal.
    /// </summary>
    private sealed class ContextKeyComparer : IEqualityComparer<(string File, string Label, string Original)>
    {
        /// <summary>
        /// 比较两个上下文键。
        /// Compare two context keys.
        /// </summary>
        /// <param name="x">第一个键 / The first key.</param>
        /// <param name="y">第二个键 / The second key.</param>
        /// <returns>相等时为 true / True if the keys are equal.</returns>
        public bool Equals((string File, string Label, string Original) x,
            (string File, string Label, string Original) y)
        {
            return string.Equals(x.File, y.File, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.Label, y.Label, StringComparison.Ordinal) &&
                   string.Equals(x.Original, y.Original, StringComparison.Ordinal);
        }

        /// <summary>
        /// 计算与 Equals 一致的哈希值。
        /// Compute a hash code consistent with Equals.
        /// </summary>
        /// <param name="key">上下文键 / The context key.</param>
        /// <returns>哈希值 / The hash code.</returns>
        public int GetHashCode((string File, string Label, string Original) key)
        {
            unchecked
            {
                var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(key.File ?? "");
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(key.Label ?? "");
                return hash * 31 + StringComparer.Ordinal.GetHashCode(key.Original ?? "");
            }
        }
    }

    /// <summary>
    /// 先查询精确词条，再按后加载优先的顺序尝试正则规则。
    /// Look up exact entries first, then try regex rules with later-loaded rules taking precedence.
    /// </summary>
    /// <param name="sourceText">待查询的原文或 I2 词条键 / The source text or I2 term key to look up.</param>
    /// <param name="translation">命中时返回的精确译文或正则替换结果 / The exact translation or regex replacement returned on a match.</param>
    /// <returns>精确词条或正则规则命中时为 true，否则为 false / True if an exact entry or regex rule matches; otherwise false.</returns>
    public bool TryGetTranslation(string sourceText, out string translation)
    {
        translation = null;
        if (string.IsNullOrEmpty(sourceText)) return false;
        if (_translations.TryGetValue(sourceText, out translation)) return true;
        if (_rules.Length == 0) return false;

        for (var ruleIndex = _rules.Length - 1; ruleIndex >= 0; ruleIndex--)
        {
            if (!_rules[ruleIndex].TryReplace(sourceText, _translations, out translation)) continue;
            return true;
        }

        return false;
    }
}

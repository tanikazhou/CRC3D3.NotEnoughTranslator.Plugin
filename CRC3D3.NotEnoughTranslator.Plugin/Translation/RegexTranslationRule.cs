using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using CRC3D3.NotEnoughTranslator.Plugin.Utils;

namespace CRC3D3.NotEnoughTranslator.Plugin.Translation;

/// <summary>
/// 封装编译后的正则模式与译文模板，支持捕获内容二次精确翻译，并在执行超时或参数错误时停用规则。
/// Encapsulates a compiled regex and translation template, supports secondary exact translation of captured content, and disables the rule on timeouts or argument errors.
/// </summary>
public sealed class RegexTranslationRule
{
    private static readonly Regex ReplacementTokens = new(
        @"\$(?:\$|&|`|'|\+|_|[0-9]+|\{[^}]+\})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private readonly Regex _regex;
    private readonly ReplacementPart[] _parts;
    private readonly string _source;
    private int _disabled;

    public string Pattern => _regex.ToString();
    public string Replacement { get; }

    /// <summary>
    /// 编译带超时限制的正则，并保存替换模板和诊断来源。
    /// Compile a timeout-bounded regex and store its replacement template and diagnostic source.
    /// </summary>
    /// <param name="pattern">用于匹配原文的 .NET 正则模式 / The .NET regex pattern used to match source text.</param>
    /// <param name="replacement">使用 .NET 替换标记的译文模板 / The translation template using .NET replacement tokens.</param>
    /// <param name="timeout">单次正则匹配的超时时间 / The timeout for a regex match operation.</param>
    /// <param name="source">用于错误定位的规则来源 / The rule source used in diagnostics.</param>
    public RegexTranslationRule(string pattern, string replacement, TimeSpan timeout, string source)
    {
        _regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, timeout);
        Replacement = replacement ?? throw new ArgumentNullException(nameof(replacement));
        _parts = ParseReplacement(_regex, replacement);
        _source = source;
    }

    /// <summary>
    /// 应用正则替换，并对捕获引用的内容再做一次精确词条查询；先匹配，未命中时不分配内存。
    /// Apply regex replacement and perform an additional exact lookup for captured references; match first, so a miss allocates nothing.
    /// </summary>
    /// <param name="input">待匹配和替换的原文 / The source text to match and replace.</param>
    /// <param name="translations">用于捕获内容二次翻译的精确词典 / The exact dictionary used for secondary translation of captured content.</param>
    /// <param name="translation">匹配成功时生成的替换结果 / The replacement result produced on a successful match.</param>
    /// <returns>匹配成功时为 true；未命中、已停用或执行失败时为 false / True on a match; false on a miss, a disabled rule, or an execution failure.</returns>
    public bool TryReplace(string input, IReadOnlyDictionary<string, string> translations,
        out string translation)
    {
        translation = null;
        if (Volatile.Read(ref _disabled) != 0) return false;

        try
        {
            var match = _regex.Match(input);
            if (!match.Success) return false;

            var builder = new StringBuilder(input.Length + Replacement.Length);
            var position = 0;
            do
            {
                builder.Append(input, position, match.Index - position);
                AppendReplacement(builder, match, translations);
                position = match.Index + match.Length;
                match = match.NextMatch();
            } while (match.Success);

            builder.Append(input, position, input.Length - position);
            translation = builder.ToString();
            return true;
        }
        catch (RegexMatchTimeoutException exception)
        {
            Disable(exception.Message);
        }
        catch (ArgumentException exception)
        {
            Disable(exception.Message);
        }

        return false;
    }

    /// <summary>
    /// 把一次匹配的译文模板展开到结果中，捕获引用的内容先做精确词条查询。
    /// Expand the translation template for one match into the result, looking up captured references in the exact dictionary first.
    /// </summary>
    /// <param name="builder">正在拼接的替换结果 / The replacement result being built.</param>
    /// <param name="match">当前正则匹配及其捕获组 / The current regex match and its capture groups.</param>
    /// <param name="translations">用于捕获内容二次翻译的精确词典 / The exact dictionary used for secondary translation of captured content.</param>
    private void AppendReplacement(StringBuilder builder, Match match, IReadOnlyDictionary<string, string> translations)
    {
        foreach (var part in _parts)
        {
            if (part.Literal != null)
            {
                builder.Append(part.Literal);
                continue;
            }

            string value;
            bool isCapture;
            if (part.GroupNumber >= 0)
            {
                var group = match.Groups[part.GroupNumber];
                value = group.Value;
                isCapture = group.Success;
            }
            else
            {
                value = match.Result(part.Token);
                isCapture = IsCaptureReference(match, part.Token);
            }

            if (isCapture && translations.TryGetValue(value, out var capturedTranslation))
                value = capturedTranslation;
            builder.Append(value);
        }
    }

    /// <summary>
    /// 构造时把译文模板拆成字面文字与替换标记；指向本正则有效编号组的 $n 直接读取分组，其余标记沿用 .NET 的 Match.Result 规则。
    /// Split the translation template once into literal text and tokens; a $n that names a valid numbered group of this regex reads the group directly, and every other token keeps the .NET Match.Result rules.
    /// </summary>
    /// <param name="regex">已编译的正则 / The compiled regex.</param>
    /// <param name="replacement">译文模板 / The translation template.</param>
    /// <returns>按顺序排列的模板片段 / The template parts in order.</returns>
    private static ReplacementPart[] ParseReplacement(Regex regex, string replacement)
    {
        var groupNumbers = new HashSet<int>(regex.GetGroupNumbers());
        var parts = new List<ReplacementPart>();
        var position = 0;
        for (var tokenMatch = ReplacementTokens.Match(replacement); tokenMatch.Success;
             tokenMatch = tokenMatch.NextMatch())
        {
            if (tokenMatch.Index > position)
                parts.Add(ReplacementPart.ForLiteral(replacement.Substring(position, tokenMatch.Index - position)));
            var token = tokenMatch.Value;
            var number = -1;
            var isNumber = char.IsDigit(token[1]) &&
                           int.TryParse(token.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture,
                               out number) && groupNumbers.Contains(number);
            parts.Add(isNumber ? ReplacementPart.ForGroup(number) : ReplacementPart.ForToken(token));
            position = tokenMatch.Index + tokenMatch.Length;
        }

        if (position < replacement.Length) parts.Add(ReplacementPart.ForLiteral(replacement.Substring(position)));
        return parts.ToArray();
    }

    /// <summary>
    /// 判断替换标记是否引用捕获内容，以决定是否执行二次翻译。
    /// Determine whether a replacement token refers to captured content eligible for secondary translation.
    /// </summary>
    /// <param name="match">当前正则匹配及其捕获组 / The current regex match and its capture groups.</param>
    /// <param name="token">已识别的 .NET 替换标记 / The recognized .NET replacement token.</param>
    /// <returns>标记引用可用于二次翻译的捕获内容时为 true / True if the token references captured content eligible for secondary translation.</returns>
    private static bool IsCaptureReference(Match match, string token)
    {
        switch (token[1])
        {
            case '&':
            case '+':
                return true;
            case '$':
            case '`':
            case '\'':
            case '_':
                return false;
            case '{':
                return match.Groups[token.Substring(2, token.Length - 3)].Success;
            default:
                return match.Groups[token.Substring(1)].Success;
        }
    }

    /// <summary>
    /// 译文模板的一个片段：字面文字、有效编号组，或交给 Match.Result 的其他标记。
    /// One part of the translation template: literal text, a valid numbered group, or another token for Match.Result.
    /// </summary>
    private readonly struct ReplacementPart
    {
        /// <summary>
        /// 创建一个模板片段。
        /// Create one template part.
        /// </summary>
        /// <param name="literal">字面文字或 null / The literal text, or null.</param>
        /// <param name="groupNumber">编号组或 -1 / The group number, or -1.</param>
        /// <param name="token">其他标记或 null / The other token, or null.</param>
        private ReplacementPart(string literal, int groupNumber, string token)
        {
            Literal = literal;
            GroupNumber = groupNumber;
            Token = token;
        }

        /// <summary>
        /// 字面文字，标记片段为 null。
        /// The literal text; null for a token part.
        /// </summary>
        public string Literal { get; }

        /// <summary>
        /// 直接读取的编号组，其他片段为 -1。
        /// The numbered group read directly; -1 for other parts.
        /// </summary>
        public int GroupNumber { get; }

        /// <summary>
        /// 交给 Match.Result 的标记，其他片段为 null。
        /// The token for Match.Result; null for other parts.
        /// </summary>
        public string Token { get; }

        /// <summary>
        /// 字面文字片段。
        /// A literal part.
        /// </summary>
        /// <param name="literal">字面文字 / The literal text.</param>
        /// <returns>模板片段 / The template part.</returns>
        public static ReplacementPart ForLiteral(string literal) => new(literal, -1, null);

        /// <summary>
        /// 编号组片段。
        /// A numbered-group part.
        /// </summary>
        /// <param name="groupNumber">编号组 / The group number.</param>
        /// <returns>模板片段 / The template part.</returns>
        public static ReplacementPart ForGroup(int groupNumber) => new(null, groupNumber, null);

        /// <summary>
        /// 其他标记片段。
        /// An other-token part.
        /// </summary>
        /// <param name="token">替换标记 / The replacement token.</param>
        /// <returns>模板片段 / The template part.</returns>
        public static ReplacementPart ForToken(string token) => new(null, -1, token);
    }

    /// <summary>
    /// 停用当前规则并仅记录一次告警，资源重载后由新规则恢复处理。
    /// Disable this rule and log once; resource reloading creates a new active rule.
    /// </summary>
    /// <param name="reason">正则停用的诊断原因 / The diagnostic reason for disabling the regex.</param>
    private void Disable(string reason)
    {
        if (Interlocked.Exchange(ref _disabled, 1) != 0) return;
        LogManager.Warning($"[Text] Regex disabled until reload/正则规则已停用，重载后恢复: {_source}: {reason}");
    }
}

namespace CRC3D3.NotEnoughTranslator.Plugin.Translation;

/// <summary>
/// 带脚本文件与标签上下文的一条精确译文，用于区分同一原文在不同脚本位置的译文。
/// One exact translation with a script file and label context, used to tell apart the translations of one original in different script places.
/// </summary>
public readonly struct ContextTranslation
{
    /// <summary>
    /// 脚本文件名，保持包内写法。
    /// The script file name as written in the pack.
    /// </summary>
    public string File { get; }

    /// <summary>
    /// 脚本标签。
    /// The script label.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// 原文。
    /// The original text.
    /// </summary>
    public string Original { get; }

    /// <summary>
    /// 译文。
    /// The translated text.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// 创建一条带上下文的译文。
    /// Create a translation with context.
    /// </summary>
    /// <param name="file">脚本文件名 / The script file name.</param>
    /// <param name="label">脚本标签 / The script label.</param>
    /// <param name="original">原文 / The original text.</param>
    /// <param name="text">译文 / The translated text.</param>
    public ContextTranslation(string file, string label, string original, string text)
    {
        File = file;
        Label = label;
        Original = original;
        Text = text;
    }
}

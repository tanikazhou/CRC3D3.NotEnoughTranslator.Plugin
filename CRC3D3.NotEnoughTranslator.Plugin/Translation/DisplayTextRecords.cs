using System.Collections.Generic;
using UnityEngine;

namespace CRC3D3.NotEnoughTranslator.Plugin.Translation;

/// <summary>
/// 按组件登记 UI 显示补丁写入的原文与译文，以及最后一次写入来自排除上下文的组件；只描述当前显示状态，从不回答词典查询。
/// 仅在 Unity 主线程使用。
/// Per-component display state of the UI text patches: the original and translation each component shows, and the components last written from an excluded context. It never answers a dictionary lookup.
/// Main thread only.
/// </summary>
internal static class DisplayTextRecords
{
    private static readonly Dictionary<int, Record> Records = new();
    private static readonly Dictionary<int, Component> ExcludedWriters = new();

    /// <summary>
    /// 检查传入值是否就是该组件当前登记的译文，即 NET 自身的输出。
    /// Check whether the incoming value is the translation recorded for this component, that is, NET's own output.
    /// </summary>
    /// <param name="component">文本组件 / The text component.</param>
    /// <param name="value">即将写入的文字 / The text about to be written.</param>
    /// <returns>登记存在且译文按 Ordinal 相等时为 true / True if a record exists and its translation equals the value (ordinal).</returns>
    public static bool IsOwnOutput(Component component, string value)
    {
        return Records.TryGetValue(component.GetInstanceID(), out var record) &&
               string.Equals(record.Translation, value, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// 登记或替换组件的原文与译文，并清除排除写入标记；内容不变时保留原登记，不再分配。
    /// Record or replace the original and translation of a component, and clear its excluded-writer mark; an unchanged record is kept without a new allocation.
    /// </summary>
    /// <param name="component">文本组件 / The text component.</param>
    /// <param name="original">游戏写入的原文 / The source text the game wrote.</param>
    /// <param name="translation">NET 显示的译文 / The translation NET shows.</param>
    public static void Set(Component component, string original, string translation)
    {
        var id = component.GetInstanceID();
        ExcludedWriters.Remove(id);
        if (Records.TryGetValue(id, out var record) && ReferenceEquals(record.Component, component) &&
            string.Equals(record.Original, original, System.StringComparison.Ordinal) &&
            string.Equals(record.Translation, translation, System.StringComparison.Ordinal)) return;
        Records[id] = new Record(component, original, translation);
    }

    /// <summary>
    /// 移除组件的登记，组件此后显示的是原文。
    /// Remove the record of a component, which now shows source text.
    /// </summary>
    /// <param name="component">文本组件 / The text component.</param>
    public static void Remove(Component component)
    {
        Records.Remove(component.GetInstanceID());
    }

    /// <summary>
    /// 标记组件最后一次写入来自排除上下文，启用与刷新路径会跳过它。
    /// Mark a component as last written from an excluded context; the enable and refresh paths skip it.
    /// </summary>
    /// <param name="component">文本组件 / The text component.</param>
    public static void MarkExcluded(Component component)
    {
        ExcludedWriters[component.GetInstanceID()] = component;
    }

    /// <summary>
    /// 检查组件是否带有排除写入标记。
    /// Check whether a component has the excluded-writer mark.
    /// </summary>
    /// <param name="component">文本组件 / The text component.</param>
    /// <returns>带有标记时为 true / True if the component has the mark.</returns>
    public static bool IsExcluded(Component component)
    {
        return ExcludedWriters.ContainsKey(component.GetInstanceID());
    }

    /// <summary>
    /// 清除组件的排除写入标记，因为最后一次写入来自普通上下文。
    /// Clear the excluded-writer mark of a component, because its last write came from a normal context.
    /// </summary>
    /// <param name="component">文本组件 / The text component.</param>
    public static void ClearMark(Component component)
    {
        ExcludedWriters.Remove(component.GetInstanceID());
    }

    /// <summary>
    /// 在读取上下文中取回组件显示译文对应的原文。
    /// Get the original behind the translation a component shows, for a read-back context.
    /// </summary>
    /// <param name="component">文本组件 / The text component.</param>
    /// <param name="current">组件当前的文字 / The current text of the component.</param>
    /// <param name="original">登记的原文 / The recorded original.</param>
    /// <returns>登记存在且当前文字就是登记译文时为 true / True if a record exists and the current text is its translation.</returns>
    public static bool TryGetOriginal(Component component, string current, out string original)
    {
        original = null;
        if (!Records.TryGetValue(component.GetInstanceID(), out var record) ||
            !string.Equals(record.Translation, current, System.StringComparison.Ordinal)) return false;
        original = record.Original;
        return true;
    }

    /// <summary>
    /// 移除已销毁组件的登记，返回仍存活组件及其原文。
    /// Remove the records of destroyed components and return the live components with their originals.
    /// </summary>
    /// <returns>存活组件与原文 / The live components with their originals.</returns>
    public static List<(Component Component, string Original)> TakeLive()
    {
        PruneDestroyed();
        var live = new List<(Component, string)>(Records.Count);
        foreach (var record in Records.Values) live.Add((record.Component, record.Original));
        return live;
    }

    /// <summary>
    /// 移除已销毁组件的登记与标记。
    /// Remove the records and marks of destroyed components.
    /// </summary>
    public static void PruneDestroyed()
    {
        List<int> destroyed = null;
        foreach (var pair in Records)
            if (pair.Value.Component == null) (destroyed ??= new List<int>()).Add(pair.Key);
        if (destroyed != null)
            foreach (var id in destroyed) Records.Remove(id);

        destroyed?.Clear();
        foreach (var pair in ExcludedWriters)
            if (pair.Value == null) (destroyed ??= new List<int>()).Add(pair.Key);
        if (destroyed != null)
            foreach (var id in destroyed) ExcludedWriters.Remove(id);
    }

    /// <summary>
    /// 清空全部登记与标记。
    /// Clear all records and marks.
    /// </summary>
    public static void Clear()
    {
        Records.Clear();
        ExcludedWriters.Clear();
    }

    /// <summary>
    /// 单个组件的显示状态。
    /// The display state of one component.
    /// </summary>
    private sealed class Record
    {
        /// <summary>
        /// 创建一条显示状态。
        /// Create one display state.
        /// </summary>
        /// <param name="component">文本组件 / The text component.</param>
        /// <param name="original">原文 / The original.</param>
        /// <param name="translation">译文 / The translation.</param>
        public Record(Component component, string original, string translation)
        {
            Component = component;
            Original = original;
            Translation = translation;
        }

        /// <summary>
        /// 文本组件。
        /// The text component.
        /// </summary>
        public Component Component { get; }

        /// <summary>
        /// 游戏写入的原文。
        /// The source text the game wrote.
        /// </summary>
        public string Original { get; }

        /// <summary>
        /// NET 显示的译文。
        /// The translation NET shows.
        /// </summary>
        public string Translation { get; }
    }
}

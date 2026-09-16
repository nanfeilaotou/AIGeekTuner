namespace AIGeekTuner.Services.Incidents
{
    /// <summary>
    /// 来自 Windows Event Log 的原始事件事实（仅保留映射所需字段）。
    /// Message 可能为 null：FormatDescription 失败不丢事件，由 Mapper 提供回退文本。
    /// </summary>
    public sealed record RawWindowsEvent(
        DateTimeOffset TimeCreatedUtc,
        string ProviderName,
        int EventId,
        int? Level,
        string Channel,
        long? RecordId,
        string? Message);

    public sealed record WindowsEventQueryTarget(
        string ProviderName,
        IReadOnlyList<int>? EventIds = null);

    public sealed record WindowsEventReadResult(
        IReadOnlyList<RawWindowsEvent> Events,
        bool MayBeTruncated);

    /// <summary>
    /// 只负责“读取 raw event records”的最小 seam（Gate D）：
    /// 单层抽象，测试用 fake 提供记录，不做 IPlatform/IRepository 套娃。
    /// 实现：有界读取 [startUtc, endUtc) 窗口内至多 maxResults 条。
    /// </summary>
    public interface IWindowsEventRecordReader
    {
        Task<IReadOnlyList<RawWindowsEvent>> ReadAsync(
            string channel,
            DateTimeOffset startUtc,
            DateTimeOffset endUtc,
            int maxResults,
            CancellationToken cancellationToken);

        /// <summary>
        /// Filtered/status-aware seam. Legacy test readers can use the default
        /// behavior; the Windows implementation pushes targets into XPath and
        /// reports whether maxResults cut off more records.
        /// </summary>
        async Task<WindowsEventReadResult> ReadDetailedAsync(
            string channel,
            DateTimeOffset startUtc,
            DateTimeOffset endUtc,
            int maxResults,
            IReadOnlyList<WindowsEventQueryTarget> targets,
            CancellationToken cancellationToken)
        {
            var events = await ReadAsync(
                channel, startUtc, endUtc, maxResults, cancellationToken);
            return new WindowsEventReadResult(events, MayBeTruncated: false);
        }
    }
}

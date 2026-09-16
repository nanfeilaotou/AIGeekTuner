using System.Diagnostics.Eventing.Reader;
using AIGeekTuner.Services.Diagnostics;

namespace AIGeekTuner.Services.Incidents
{
    /// <summary>
    /// IWindowsEventRecordReader 的 Windows 原生实现（Gate C）：
    /// 只用 System.Diagnostics.Eventing.Reader；不调 wevtutil、不启 PowerShell。
    /// 读取严格有界：XPath 时间窗 + maxResults；取消后尽快停止枚举。
    /// 同步枚举被包在单一 Task.Run 边界内（Gate F 允许，不到处 Task.Run）。
    /// </summary>
    public sealed class WindowsEventRecordReader : IWindowsEventRecordReader
    {
        public Task<IReadOnlyList<RawWindowsEvent>> ReadAsync(
            string channel,
            DateTimeOffset startUtc,
            DateTimeOffset endUtc,
            int maxResults,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(channel);
            if (maxResults < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxResults));
            }

            return ReadDetailedAsync(
                channel, startUtc, endUtc, maxResults, [], cancellationToken)
                .ContinueWith(
                    task => (IReadOnlyList<RawWindowsEvent>)task.GetAwaiter().GetResult().Events,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        public Task<WindowsEventReadResult> ReadDetailedAsync(
            string channel,
            DateTimeOffset startUtc,
            DateTimeOffset endUtc,
            int maxResults,
            IReadOnlyList<WindowsEventQueryTarget> targets,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(channel);
            if (maxResults < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxResults));
            }

            return Task.Run(
                () => ReadCore(channel, startUtc, endUtc, maxResults, targets, cancellationToken),
                cancellationToken);
        }

        private static WindowsEventReadResult ReadCore(
            string channel,
            DateTimeOffset startUtc,
            DateTimeOffset endUtc,
            int maxResults,
            IReadOnlyList<WindowsEventQueryTarget> targets,
            CancellationToken cancellationToken)
        {
            // 事件 XPath 的 SystemTime 比较需要 UTC “Z” 格式。
            var xpath = $"*[System[TimeCreated[@SystemTime >= '{startUtc.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}' "
                + $"and @SystemTime <= '{endUtc.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}']"
                + BuildTargetPredicate(targets) + "]]";

            var results = new List<RawWindowsEvent>(Math.Min(maxResults, 128));
            var query = new EventLogQuery(channel, PathType.LogName, xpath)
            {
                ReverseDirection = true
            };
            using var reader = new EventLogReader(query);

            while (results.Count < maxResults)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using EventRecord? record = reader.ReadEvent();
                if (record is null)
                {
                    break; // 窗口内已无更多事件。
                }

                try
                {
                    var providerName = SafeProviderName(record);
                    var message = SafeMessage(record);
                    results.Add(new RawWindowsEvent(
                        TimeCreatedUtc: record.TimeCreated?.ToUniversalTime() ?? DateTimeOffset.UtcNow,
                        ProviderName: providerName,
                        EventId: record.Id,
                        Level: record.Level,
                        Channel: channel,
                        RecordId: record.RecordId,
                        Message: message));
                }
                catch (Exception exception)
                {
                    // 单条记录元数据异常不终止整个 channel 查询（Gate F）。
                    ExceptionLogWriter.Write(exception, $"IncidentRecord/{channel}");
                }
            }

            var mayBeTruncated = false;
            if (results.Count >= maxResults)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var next = reader.ReadEvent();
                mayBeTruncated = next is not null;
            }

            return new WindowsEventReadResult(results, mayBeTruncated);
        }

        private static string BuildTargetPredicate(
            IReadOnlyList<WindowsEventQueryTarget> targets)
        {
            if (targets.Count == 0)
            {
                return string.Empty;
            }

            var clauses = targets.Select(target =>
            {
                var provider = target.ProviderName.Replace("'", "&apos;", StringComparison.Ordinal);
                var providerClause = $"Provider[@Name='{provider}']";
                if (target.EventIds is null || target.EventIds.Count == 0)
                {
                    return providerClause;
                }

                var ids = string.Join(" or ", target.EventIds.Select(id => $"EventID={id}"));
                return $"({providerClause} and ({ids}))";
            });
            return " and (" + string.Join(" or ", clauses) + ")";
        }

        private static string SafeProviderName(EventRecord record)
        {
            try
            {
                return record.ProviderName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string? SafeMessage(EventRecord record)
        {
            try
            {
                var formatted = record.FormatDescription();
                return string.IsNullOrWhiteSpace(formatted) ? null : formatted;
            }
            catch (EventLogException)
            {
                // 消息 DLL 缺失等：不丢事件，Mapper 会用 Provider+EventId 回退文本。
                return null;
            }
        }
    }
}

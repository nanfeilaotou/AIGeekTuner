using System.Collections.Immutable;
using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Models.Sessions;

/// <summary>
/// 录制中的 UI 小快照。它只包含展示所需的最新读数和增量统计，
/// 不暴露录制服务内部用于最终保存的 Samples 集合。
/// </summary>
public sealed record TelemetryRecordingSnapshot(
    string SessionId,
    DateTimeOffset StartedAtUtc,
    int RequestedIntervalMs,
    RecordingStatus Status,
    int SampleCount,
    DateTimeOffset? LatestCapturedAtUtc,
    ImmutableArray<TelemetryReading> LatestReadings,
    ImmutableArray<TelemetryLiveMetricSnapshot> Metrics,
    ImmutableArray<TelemetrySourceReport> Sources)
{
    public static TelemetryRecordingSnapshot Empty(
        string sessionId,
        DateTimeOffset startedAtUtc,
        int requestedIntervalMs,
        RecordingStatus status = RecordingStatus.Recording) =>
        new(
            sessionId,
            startedAtUtc,
            requestedIntervalMs,
            status,
            0,
            null,
            ImmutableArray<TelemetryReading>.Empty,
            ImmutableArray<TelemetryLiveMetricSnapshot>.Empty,
            ImmutableArray<TelemetrySourceReport>.Empty);
}

/// <summary>一个 UI 指标的增量统计结果。</summary>
public sealed record TelemetryLiveMetricSnapshot(
    string Label,
    TelemetryUnit Unit,
    double Current,
    double Minimum,
    double Maximum,
    double Average,
    int SampleCount,
    DateTimeOffset LastAtUtc);

/// <summary>
/// 与录制页既有文案保持一致的指标标签映射；录制服务和 VM 共用，
/// 避免一边按 label 聚合、另一边按不同 key 展示。
/// </summary>
public static class TelemetryLiveMetricLabel
{
    public static string For(TelemetryReading reading)
    {
        var device = reading.Device.Kind switch
        {
            TelemetryDeviceKind.Cpu => "CPU",
            TelemetryDeviceKind.Gpu => reading.Device.DisplayName.Length > 0
                ? "GPU · " + reading.Device.DisplayName
                : "GPU",
            TelemetryDeviceKind.Memory => "内存",
            TelemetryDeviceKind.Storage => "磁盘 · " + reading.Device.DisplayName,
            _ => reading.Device.DisplayName,
        };
        var metric = reading.MetricKey.Value switch
        {
            "cpu.package.temperature" or "gpu.core.temperature" or "storage.temperature" => "温度",
            "gpu.hotspot.temperature" => "热点温度",
            "gpu.memory.temperature" => "显存温度",
            "cpu.total.utilization" or "gpu.core.utilization" or "memory.utilization" => "使用率",
            "cpu.clock" or "gpu.core.clock" or "memory.clock" => "频率",
            "cpu.package.power" or "gpu.board.power" => "功耗",
            "cpu.throttling" => "降频占比",
            "memory.used" or "gpu.memory.used" => "已用容量",
            _ => reading.MetricKey.Value,
        };
        return device + " " + metric;
    }
}

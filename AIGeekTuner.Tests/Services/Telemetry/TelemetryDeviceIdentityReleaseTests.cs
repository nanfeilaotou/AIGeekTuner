using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.SessionAnalysis;
using AIGeekTuner.Services.Telemetry;
using AIGeekTuner.Services.Telemetry.Recording;

namespace AIGeekTuner.Tests.Services.Telemetry;

public sealed class TelemetryDeviceIdentityReleaseTests
{
    [Fact]
    public async Task SingleSource_ToMultiSource_ToSingleSource_DropoutRecovery_KeepsCanonicalKey()
    {
        var hwInfo = new SequenceProvider(TelemetrySourceKind.HwInfo,
        [
            Unavailable(TelemetrySourceKind.HwInfo),
            Result(TelemetrySourceKind.HwInfo,
                Gpu("sensor:00000001:0", "NVIDIA GeForce RTX 4080", 61)),
            Result(TelemetrySourceKind.HwInfo,
                Gpu("sensor:00000001:0", "NVIDIA GeForce RTX 4080", 62)),
            Unavailable(TelemetrySourceKind.HwInfo),
        ]);
        var lhm = new SequenceProvider(TelemetrySourceKind.LibreHardwareMonitor,
        [
            Result(TelemetrySourceKind.LibreHardwareMonitor,
                Gpu("lhm:/gpu-nvidia/0", "NVIDIA GeForce RTX 4080", 60)),
            Result(TelemetrySourceKind.LibreHardwareMonitor,
                Gpu("lhm:/gpu-nvidia/0", "NVIDIA GeForce RTX 4080", 60)),
            Unavailable(TelemetrySourceKind.LibreHardwareMonitor),
            Result(TelemetrySourceKind.LibreHardwareMonitor,
                Gpu("lhm:/gpu-nvidia/0", "NVIDIA GeForce RTX 4080", 63)),
        ]);
        var hub = new TelemetryHub([lhm, hwInfo]);

        var snapshots = new[]
        {
            await hub.ReadAsync(),
            await hub.ReadAsync(),
            await hub.ReadAsync(),
            await hub.ReadAsync(),
        };

        var keys = snapshots
            .Select(snapshot => Assert.Single(snapshot.CanonicalReadings).Device.DeviceKey)
            .ToArray();
        Assert.All(keys, key => Assert.Equal(keys[0], key));
        Assert.Equal(TelemetrySourceKind.LibreHardwareMonitor,
            snapshots[0].CanonicalReadings[0].Source);
        Assert.Equal(TelemetrySourceKind.HwInfo,
            snapshots[1].CanonicalReadings[0].Source);
        Assert.Equal(TelemetrySourceKind.HwInfo,
            snapshots[2].CanonicalReadings[0].Source);
        Assert.Equal(TelemetrySourceKind.LibreHardwareMonitor,
            snapshots[3].CanonicalReadings[0].Source);
    }

    [Fact]
    public async Task SameModelDisks_ReversedEnumeration_DoNotSwapCanonicalIdentity()
    {
        var provider = new SequenceProvider(TelemetrySourceKind.LibreHardwareMonitor,
        [
            Result(TelemetrySourceKind.LibreHardwareMonitor,
                Storage("lhm:/nvme/0", "Same NVMe", 40),
                Storage("lhm:/nvme/1", "Same NVMe", 70)),
            Result(TelemetrySourceKind.LibreHardwareMonitor,
                Storage("lhm:/nvme/1", "Same NVMe", 71),
                Storage("lhm:/nvme/0", "Same NVMe", 41)),
        ]);
        var hub = new TelemetryHub([provider]);

        var first = await hub.ReadAsync();
        var second = await hub.ReadAsync();
        var firstKeyByValue = first.CanonicalReadings.ToDictionary(r => r.Value, r => r.Device.DeviceKey);
        var secondKeyByValue = second.CanonicalReadings.ToDictionary(r => r.Value, r => r.Device.DeviceKey);

        Assert.Equal(firstKeyByValue[40], secondKeyByValue[41]);
        Assert.Equal(firstKeyByValue[70], secondKeyByValue[71]);
        Assert.NotEqual(firstKeyByValue[40], firstKeyByValue[70]);
    }

    [Fact]
    public async Task PartialProviderVisibility_ForSameModelGpus_DoesNotGuessMerge()
    {
        var hwInfo = new SequenceProvider(
            TelemetrySourceKind.HwInfo,
            [Result(TelemetrySourceKind.HwInfo,
                Gpu("sensor:7:0", "NVIDIA GeForce RTX 4090", 55))]);
        var lhm = new SequenceProvider(
            TelemetrySourceKind.LibreHardwareMonitor,
            [Result(TelemetrySourceKind.LibreHardwareMonitor,
                Gpu("lhm:/gpu/0", "NVIDIA GeForce RTX 4090", 48),
                Gpu("lhm:/gpu/1", "NVIDIA GeForce RTX 4090", 82))]);
        var hub = new TelemetryHub([hwInfo, lhm]);

        var snapshot = await hub.ReadAsync();

        Assert.Equal(3, snapshot.CanonicalReadings.Count);
        Assert.Equal(3, snapshot.CanonicalReadings
            .Select(reading => reading.Device.DeviceKey).Distinct().Count());
    }

    [Fact]
    public async Task ProviderRegistrationOrder_DoesNotChangeReliableCanonicalGroups()
    {
        static ITelemetryProvider HwInfo() => new SequenceProvider(
            TelemetrySourceKind.HwInfo,
            [Result(TelemetrySourceKind.HwInfo,
                Gpu("sensor:1:0", "NVIDIA RTX 4080", 60),
                Gpu("sensor:2:0", "AMD Radeon 780M", 50))]);
        static ITelemetryProvider Lhm() => new SequenceProvider(
            TelemetrySourceKind.LibreHardwareMonitor,
            [Result(TelemetrySourceKind.LibreHardwareMonitor,
                Gpu("lhm:/gpu/amd0", "AMD Radeon 780M", 51),
                Gpu("lhm:/gpu/nv0", "NVIDIA RTX 4080", 61))]);

        var first = await new TelemetryHub([HwInfo(), Lhm()]).ReadAsync();
        var reversed = await new TelemetryHub([Lhm(), HwInfo()]).ReadAsync();

        Assert.Equal(
            first.CanonicalReadings.Select(r => r.Device.DeviceKey).Order().ToArray(),
            reversed.CanonicalReadings.Select(r => r.Device.DeviceKey).Order().ToArray());
    }

    [Fact]
    public async Task DualSameNvidia_ProviderThroughHubRecordingAnalyzerAndAiEvidence_StaysSeparated()
    {
        var provider = new SequenceProvider(TelemetrySourceKind.LibreHardwareMonitor,
        [
            Result(TelemetrySourceKind.LibreHardwareMonitor,
                Gpu("lhm:/gpu/0", "NVIDIA GeForce RTX 4090", 50, 10),
                Gpu("lhm:/gpu/1", "NVIDIA GeForce RTX 4090", 80, 90)),
            Result(TelemetrySourceKind.LibreHardwareMonitor,
                Gpu("lhm:/gpu/1", "NVIDIA GeForce RTX 4090", 82, 92),
                Gpu("lhm:/gpu/0", "NVIDIA GeForce RTX 4090", 52, 12)),
        ]);
        var hub = new TelemetryHub([provider]);
        var recorder = new TelemetryRecordingService(hub);
        var session = TelemetryRecordingSession.Start(1000, DateTimeOffset.UtcNow);

        session.AddSample(await recorder.CaptureOnceAsync(session, CancellationToken.None));
        session.AddSample(await recorder.CaptureOnceAsync(session, CancellationToken.None));

        var summary = TelemetrySessionAnalyzer.Analyze(session);
        var temperatureSeries = summary.Statistics
            .Where(stat => stat.MetricKey == TelemetryMetricKey.GpuCoreTemperature.Value)
            .OrderBy(stat => stat.Average)
            .ToArray();
        Assert.Equal(2, temperatureSeries.Length);
        Assert.Equal(51, temperatureSeries[0].Average);
        Assert.Equal(81, temperatureSeries[1].Average);
        Assert.NotEqual(temperatureSeries[0].DeviceKey, temperatureSeries[1].DeviceKey);

        var aiContext = TelemetrySessionAnalyzer.BuildAnalysisContext(session);
        var evidence = DiagnosticEvidenceContextBuilder.Build(aiContext, incidents: null);
        var evidenceTemperatures = evidence.Telemetry.Statistics
            .Where(stat => stat.MetricKey == TelemetryMetricKey.GpuCoreTemperature.Value)
            .ToArray();
        Assert.Equal(2, evidenceTemperatures.Length);
        Assert.Equal(2, evidenceTemperatures.Select(stat => stat.DeviceKey).Distinct().Count());
        Assert.Contains(evidenceTemperatures, stat => stat.Avg == 51);
        Assert.Contains(evidenceTemperatures, stat => stat.Avg == 81);
    }

    private static DeviceSample Gpu(
        string nativeId,
        string name,
        double temperature,
        double? utilization = null) =>
        new(TelemetryDeviceKind.Gpu, nativeId, name,
            TelemetryMetricKey.GpuCoreTemperature, temperature,
            utilization is null
                ? []
                : [(TelemetryMetricKey.GpuCoreUtilization, utilization.Value)]);

    private static DeviceSample Storage(string nativeId, string name, double temperature) =>
        new(TelemetryDeviceKind.Storage, nativeId, name,
            TelemetryMetricKey.StorageTemperature, temperature, []);

    private static TelemetryProviderResult Result(
        TelemetrySourceKind source,
        params DeviceSample[] devices)
    {
        var at = DateTimeOffset.UtcNow;
        var infos = new List<SourceDeviceInfo>();
        var readings = new List<TelemetryReading>();
        for (var ordinal = 0; ordinal < devices.Length; ordinal++)
        {
            var sample = devices[ordinal];
            var identity = new TelemetryDeviceIdentity(
                sample.Kind, sample.NativeId, sample.Name);
            infos.Add(new SourceDeviceInfo(
                source, sample.Kind, sample.NativeId, sample.Name, ordinal, []));
            readings.Add(ToReading(source, identity, sample.Metric, sample.Value, at));
            foreach (var extra in sample.ExtraMetrics)
            {
                readings.Add(ToReading(source, identity, extra.Metric, extra.Value, at));
            }
        }

        return new TelemetryProviderResult(
            TelemetrySourceStatus.Ready,
            "ready",
            [],
            readings,
            at,
            infos);
    }

    private static TelemetryReading ToReading(
        TelemetrySourceKind source,
        TelemetryDeviceIdentity device,
        TelemetryMetricKey metric,
        double value,
        DateTimeOffset at) =>
        new(
            metric,
            value,
            metric == TelemetryMetricKey.GpuCoreUtilization
                ? TelemetryUnit.Percent
                : TelemetryUnit.Celsius,
            device,
            source,
            metric.Value,
            metric.Value,
            at);

    private static TelemetryProviderResult Unavailable(TelemetrySourceKind source) =>
        TelemetryProviderResult.Empty(
            source,
            TelemetrySourceStatus.Unavailable,
            "unavailable",
            DateTimeOffset.UtcNow);

    private sealed record DeviceSample(
        TelemetryDeviceKind Kind,
        string NativeId,
        string Name,
        TelemetryMetricKey Metric,
        double Value,
        IReadOnlyList<(TelemetryMetricKey Metric, double Value)> ExtraMetrics);

    private sealed class SequenceProvider : ITelemetryProvider
    {
        private readonly Queue<TelemetryProviderResult> _results;
        private TelemetryProviderResult? _last;

        public SequenceProvider(
            TelemetrySourceKind sourceKind,
            IEnumerable<TelemetryProviderResult> results)
        {
            SourceKind = sourceKind;
            _results = new Queue<TelemetryProviderResult>(results);
        }

        public TelemetrySourceKind SourceKind { get; }

        public Task<TelemetryProviderResult> ReadSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_results.Count > 0)
            {
                _last = _results.Dequeue();
            }

            return Task.FromResult(_last ?? Unavailable(SourceKind));
        }
    }
}

using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry;
using AIGeekTuner.Services.Telemetry.Recording;

namespace AIGeekTuner.Tests.Services.Telemetry.Recording;

public sealed class TelemetryRecordingSnapshotTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LiveSnapshot_UsesIncrementalMetrics_AndFinalizeKeepsAllSamples()
    {
        var queue = new Queue<TelemetrySnapshot>([
            Snapshot(10),
            Snapshot(4),
            Snapshot(16),
        ]);
        var service = new TelemetryRecordingService(new QueueHub(queue));

        Assert.True(service.Start(200));
        await WaitUntilAsync(() => service.LiveSnapshot?.SampleCount == 3);

        var snapshot = service.LiveSnapshot;
        Assert.NotNull(snapshot);
        var metric = Assert.Single(snapshot!.Metrics);
        Assert.Equal(16, metric.Current);
        Assert.Equal(4, metric.Minimum);
        Assert.Equal(16, metric.Maximum);
        Assert.Equal(10, metric.Average);
        Assert.Equal(3, metric.SampleCount);
        Assert.Equal(16, snapshot.LatestReadings.Single().Value);
        Assert.Equal(TelemetrySourceStatus.Ready, snapshot.Sources.Single().Status);

        var finalized = await service.StopAsync();
        Assert.NotNull(finalized);
        Assert.Equal(3, finalized!.Samples.Count);
        Assert.Equal(3, finalized.Summary!.SampleCount);
    }

    [Fact]
    public async Task ConcurrentSnapshotReads_DoNotEnumerateMutableHistory()
    {
        var queue = new Queue<TelemetrySnapshot>(
            Enumerable.Range(0, 12).Select(value => Snapshot(value)));
        var service = new TelemetryRecordingService(new QueueHub(queue));
        Assert.True(service.Start(200));

        var reads = Task.Run(async () =>
        {
            for (var index = 0; index < 500; index++)
            {
                var snapshot = service.LiveSnapshot;
                _ = snapshot?.Metrics.Sum(metric => metric.SampleCount);
                await Task.Yield();
            }
        });

        await reads;
        await service.StopAsync();
    }

    private static TelemetrySnapshot Snapshot(double value) =>
        new(
            T0,
            [new TelemetryReading(
                new TelemetryMetricKey("cpu.package.temperature"),
                value,
                TelemetryUnit.Celsius,
                TelemetryDeviceIdentity.Cpu("cpu"),
                TelemetrySourceKind.HwInfo,
                "cpu.temp",
                null,
                T0)],
            [new TelemetrySourceReport(
                TelemetrySourceKind.HwInfo,
                TelemetrySourceStatus.Ready,
                "ok",
                1,
                T0)],
            []);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private sealed class QueueHub(Queue<TelemetrySnapshot> snapshots) : ITelemetryHub
    {
        public Task<TelemetrySnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (snapshots.Count == 0)
            {
                throw new InvalidOperationException("test queue exhausted");
            }

            return Task.FromResult(snapshots.Dequeue());
        }
    }
}

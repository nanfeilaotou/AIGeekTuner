using System.Diagnostics;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry;

namespace AIGeekTuner.Tests.Services.Telemetry;

public sealed class TelemetryHubLifecycleTests
{
    [Fact]
    public async Task IgnoredCancellation_DeadlineReturns_AndRepeatedReadsStaySingleFlight()
    {
        var provider = new BlockingProvider();
        var hub = new TelemetryHub(
            [provider],
            perProviderTimeout: TimeSpan.FromMilliseconds(80));
        var stopwatch = Stopwatch.StartNew();

        var timedOut = await hub.ReadAsync();

        stopwatch.Stop();
        Assert.Equal(
            TelemetrySourceStatus.Timeout,
            Assert.Single(timedOut.Sources).Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(1, provider.ReadCount);
        Assert.Equal(1, provider.ActiveReads);

        for (var i = 0; i < 5; i++)
        {
            var busy = await hub.ReadAsync();
            Assert.Equal(TelemetrySourceStatus.Busy, Assert.Single(busy.Sources).Status);
        }

        Assert.Equal(1, provider.ReadCount);
        Assert.Equal(1, provider.MaxConcurrentReads);

        provider.ReleaseFirstRead();
        await WaitUntilAsync(() => provider.ActiveReads == 0);

        var recovered = await hub.ReadAsync();

        Assert.Equal(TelemetrySourceStatus.Ready, Assert.Single(recovered.Sources).Status);
        Assert.Single(recovered.CanonicalReadings);
        Assert.Equal(2, provider.ReadCount);
        Assert.Equal(1, provider.MaxConcurrentReads);
    }

    [Fact]
    public async Task ProviderThrow_DoesNotPoisonSlot_AndNextReadRecovers()
    {
        var calls = 0;
        var provider = new DelegateProvider(async _ =>
        {
            await Task.Yield();
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("first read failed");
            }

            return ReadyResult(TelemetrySourceKind.HwInfo, 42);
        });
        var hub = new TelemetryHub([provider], TimeSpan.FromSeconds(1));

        var failed = await hub.ReadAsync();
        var recovered = await hub.ReadAsync();

        Assert.Equal(TelemetrySourceStatus.Error, Assert.Single(failed.Sources).Status);
        Assert.Equal(TelemetrySourceStatus.Ready, Assert.Single(recovered.Sources).Status);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FastProvider_HasNoBehaviorRegression()
    {
        var provider = new DelegateProvider(
            _ => Task.FromResult(ReadyResult(TelemetrySourceKind.HwInfo, 73)));
        var hub = new TelemetryHub([provider], TimeSpan.FromSeconds(1));

        var snapshot = await hub.ReadAsync();

        var reading = Assert.Single(snapshot.CanonicalReadings);
        Assert.Equal(73, reading.Value);
        Assert.Equal(TelemetrySourceStatus.Ready, Assert.Single(snapshot.Sources).Status);
    }

    private static TelemetryProviderResult ReadyResult(
        TelemetrySourceKind source,
        double value)
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var device = new TelemetryDeviceIdentity(
            TelemetryDeviceKind.Gpu,
            "native:gpu-a",
            "GPU A");
        var info = new SourceDeviceInfo(
            source,
            device.Kind,
            device.DeviceKey,
            device.DisplayName,
            0,
            []);
        var reading = new TelemetryReading(
            TelemetryMetricKey.GpuCoreTemperature,
            value,
            TelemetryUnit.Celsius,
            device,
            source,
            "temp",
            "GPU Core",
            capturedAt);
        return new TelemetryProviderResult(
            TelemetrySourceStatus.Ready,
            "ready",
            [],
            [reading],
            capturedAt,
            [info]);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(predicate());
    }

    private sealed class DelegateProvider(
        Func<CancellationToken, Task<TelemetryProviderResult>> read) : ITelemetryProvider
    {
        public TelemetrySourceKind SourceKind => TelemetrySourceKind.HwInfo;

        public Task<TelemetryProviderResult> ReadSnapshotAsync(
            CancellationToken cancellationToken = default) => read(cancellationToken);
    }

    private sealed class BlockingProvider : ITelemetryProvider
    {
        private readonly TaskCompletionSource _releaseFirst = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;
        private int _activeReads;
        private int _maxConcurrentReads;

        public TelemetrySourceKind SourceKind => TelemetrySourceKind.HwInfo;
        public int ReadCount => Volatile.Read(ref _readCount);
        public int ActiveReads => Volatile.Read(ref _activeReads);
        public int MaxConcurrentReads => Volatile.Read(ref _maxConcurrentReads);

        public async Task<TelemetryProviderResult> ReadSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _readCount);
            var active = Interlocked.Increment(ref _activeReads);
            UpdateMaximum(active);
            try
            {
                if (call == 1)
                {
                    // Deliberately ignores cancellationToken.
                    await _releaseFirst.Task.ConfigureAwait(false);
                }

                return ReadyResult(SourceKind, 60 + call);
            }
            finally
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }

        public void ReleaseFirstRead() => _releaseFirst.TrySetResult();

        private void UpdateMaximum(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxConcurrentReads);
                if (active <= current
                    || Interlocked.CompareExchange(
                        ref _maxConcurrentReads, active, current) == current)
                {
                    return;
                }
            }
        }
    }
}

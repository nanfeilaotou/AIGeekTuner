using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry;
using AIGeekTuner.Services.Telemetry.Recording;
using Xunit;

namespace AIGeekTuner.Tests.Services.Telemetry
{
    /// <summary>V2-M3.2：LiveTelemetryCoordinator 的共享采样/独立轮询语义回归。</summary>
    public sealed class LiveTelemetryCoordinatorTests
    {
        [Fact]
        public async Task Start_NotRecording_PollsHub_AndPublishes()
        {
            var hub = new FakeHub();
            var coordinator = new LiveTelemetryCoordinator(hub);
            var published = SubscribeOnce(coordinator);

            coordinator.Start(200);

            var finished = await Task.WhenAny(published.Task, Task.Delay(3000));
            Assert.Same(published.Task, finished);
            Assert.True(coordinator.IsRunning);
            Assert.Equal(LiveTelemetrySourceMode.Coordinator, coordinator.Mode);
            Assert.True(hub.ReadCount >= 1);
            coordinator.Stop();
        }

        [Fact]
        public void NotStarted_RecorderSample_IsIgnored()
        {
            var recorder = new FakeRecorder();
            var coordinator = new LiveTelemetryCoordinator(new FakeHub(), recorder);
            var seen = new List<TelemetrySnapshot>();
            coordinator.SnapshotUpdated += s => seen.Add(s);

            recorder.PublishSample(CreateSample(1));

            Assert.False(coordinator.IsRunning);
            Assert.Empty(seen);
            Assert.Null(coordinator.LatestSnapshot);
        }

        [Fact]
        public async Task Running_WhileRecording_MirrorsSample_WithoutExtraHubReads()
        {
            var hub = new FakeHub();
            var recorder = new FakeRecorder();
            var coordinator = new LiveTelemetryCoordinator(hub, recorder);
            var firstHubPoll = SubscribeOnce(coordinator);

            coordinator.Start(200);
            var finished = await Task.WhenAny(firstHubPoll.Task, Task.Delay(3000));
            Assert.Same(firstHubPoll.Task, finished);
            var readsBeforeRecording = hub.ReadCount;

            // 录制开始：镜像推送必须到达，且模式切到 Recorder（§22 禁止双轮询）。
            recorder.IsRecording = true;
            recorder.StartSession(intervalMs: 1000);
            var mirrored = SubscribeOnce(coordinator);
            var sample = CreateSample(7);
            recorder.AddToSession(sample);
            recorder.PublishSample(sample);

            finished = await Task.WhenAny(mirrored.Task, Task.Delay(3000));
            Assert.Same(mirrored.Task, finished);
            Assert.Equal(LiveTelemetrySourceMode.Recorder, coordinator.Mode);
            Assert.Equal(sample.CapturedAtUtc, coordinator.LatestSnapshot!.CapturedAtUtc);

            // 短窗口内 Hub 不得因录制期间出现新的第二套读取。
            await Task.Delay(450);
            Assert.True(hub.ReadCount <= readsBeforeRecording + 3,
                $"Hub reads grew during mirror mode: {readsBeforeRecording} -> {hub.ReadCount}");
            coordinator.Stop();
        }

        [Fact]
        public async Task Stop_AfterRunning_PublishesNoMore()
        {
            var hub = new FakeHub();
            var coordinator = new LiveTelemetryCoordinator(hub);
            var first = SubscribeOnce(coordinator);
            coordinator.Start(200);
            await Task.WhenAny(first.Task, Task.Delay(3000));

            coordinator.Stop();
            var countAfterStop = hub.ReadCount;
            await Task.Delay(450);

            Assert.False(coordinator.IsRunning);
            Assert.Equal(countAfterStop, hub.ReadCount);
        }

        [Fact]
        public async Task DelayedOldGeneration_StopPreventsPublish_ThenRestartPublishesNewRead()
        {
            var hub = new SequencedHub();
            await using var coordinator = new LiveTelemetryCoordinator(hub);
            var seen = new List<DateTimeOffset>();
            coordinator.SnapshotUpdated += snapshot => seen.Add(snapshot.CapturedAtUtc);

            coordinator.Start(200);
            await hub.FirstReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

            var stopping = coordinator.StopAsync();
            await Task.Delay(50);
            Assert.False(stopping.IsCompleted);
            hub.ReleaseFirstRead();
            await stopping;

            Assert.Empty(seen);

            var next = SubscribeOnce(coordinator);
            coordinator.Start(200);
            var published = await next.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(SequencedHub.NewGenerationTimestamp, published.CapturedAtUtc);
            Assert.DoesNotContain(SequencedHub.OldGenerationTimestamp, seen);
        }

        [Fact]
        public async Task LiveRead_ToRecorder_ThenRecorderStop_UsesOneProviderFlightAndResumes()
        {
            var provider = new OverlapTrackingProvider();
            var hub = new TelemetryHub([provider], TimeSpan.FromSeconds(2));
            var recorder = new TelemetryRecordingService(hub);
            await using var coordinator = new LiveTelemetryCoordinator(hub, recorder);
            var publishedValues = new List<double>();
            coordinator.SnapshotUpdated += snapshot =>
            {
                foreach (var reading in snapshot.CanonicalReadings)
                {
                    publishedValues.Add(reading.Value);
                }
            };

            coordinator.Start(200);
            await provider.FirstReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.True(recorder.Start(200));
            await Task.Delay(250);
            Assert.Equal(1, provider.MaxConcurrentReads);

            provider.ReleaseFirstRead();
            await WaitUntilAsync(() => provider.ReadCount >= 2);
            Assert.Equal(1, provider.MaxConcurrentReads);
            Assert.DoesNotContain(101d, publishedValues);

            await recorder.StopAsync();
            await WaitUntilAsync(() => provider.ReadCount >= 3);

            Assert.Equal(LiveTelemetrySourceMode.Coordinator, coordinator.Mode);
            Assert.Equal(1, provider.MaxConcurrentReads);
        }

        [Fact]
        public void Dispose_UnsubscribesRecorderEvent()
        {
            var recorder = new FakeRecorder();
            var coordinator = new LiveTelemetryCoordinator(new FakeHub(), recorder);

            Assert.Equal(1, recorder.SubscriberCount);
            coordinator.Dispose();
            Assert.Equal(0, recorder.SubscriberCount);
            Assert.Throws<ObjectDisposedException>(() => coordinator.Start(200));
        }

        private static TaskCompletionSource<TelemetrySnapshot> SubscribeOnce(
            ILiveTelemetrySource source)
        {
            var tcs = new TaskCompletionSource<TelemetrySnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            source.SnapshotUpdated += s => tcs.TrySetResult(s);
            return tcs;
        }

        private static TelemetrySample CreateSample(int sequence) => new(
            sequence,
            DateTimeOffset.UtcNow,
            ReadDurationMs: 1,
            Readings: []);

        private sealed class FakeHub : ITelemetryHub
        {
            private int _count;
            public int ReadCount => Volatile.Read(ref _count);

            public Task<TelemetrySnapshot> ReadAsync(CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _count);
                return Task.FromResult(TelemetrySnapshot.Empty(DateTimeOffset.UtcNow));
            }
        }

        private sealed class FakeRecorder : ITelemetryRecordingService
        {
            private TelemetryRecordingSession? _session;
            private Action<TelemetrySample>? _sampleCaptured;

            public bool IsRecording { get; set; }

            public TelemetryRecordingSession? CurrentSession => _session;

            public TelemetrySample? LatestSample { get; private set; }

            public string? LastError => null;

            public string? LastSavedPath => null;

            public int SubscriberCount => _sampleCaptured?.GetInvocationList().Length ?? 0;

            public event Action<TelemetrySample>? SampleCaptured
            {
                add => _sampleCaptured += value;
                remove => _sampleCaptured -= value;
            }

            public void StartSession(int intervalMs)
            {
                _session = TelemetryRecordingSession.Start(intervalMs, DateTimeOffset.UtcNow);
            }

            public void AddToSession(TelemetrySample sample)
            {
                _session?.AddSample(sample);
            }

            public void PublishSample(TelemetrySample sample)
            {
                LatestSample = sample;
                _sampleCaptured?.Invoke(sample);
            }

            public bool Start(int intervalMs) => false;

            public Task<TelemetryRecordingSession?> StopAsync() =>
                Task.FromResult<TelemetryRecordingSession?>(null);

            public Task FinalizeIfRecordingAsync(TimeSpan timeout) => Task.CompletedTask;
        }

        private sealed class SequencedHub : ITelemetryHub
        {
            public static readonly DateTimeOffset OldGenerationTimestamp =
                new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
            public static readonly DateTimeOffset NewGenerationTimestamp =
                OldGenerationTimestamp.AddSeconds(1);
            private readonly TaskCompletionSource _releaseFirst = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            private int _calls;

            public TaskCompletionSource FirstReadEntered { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<TelemetrySnapshot> ReadAsync(
                CancellationToken cancellationToken = default)
            {
                if (Interlocked.Increment(ref _calls) == 1)
                {
                    FirstReadEntered.TrySetResult();
                    // Deliberately ignore cancellation to exercise generation guard.
                    await _releaseFirst.Task.ConfigureAwait(false);
                    return TelemetrySnapshot.Empty(OldGenerationTimestamp);
                }

                return TelemetrySnapshot.Empty(NewGenerationTimestamp);
            }

            public void ReleaseFirstRead() => _releaseFirst.TrySetResult();
        }

        private sealed class OverlapTrackingProvider : ITelemetryProvider
        {
            private readonly TaskCompletionSource _releaseFirst = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            private int _readCount;
            private int _active;
            private int _maximum;

            public TelemetrySourceKind SourceKind => TelemetrySourceKind.HwInfo;
            public int ReadCount => Volatile.Read(ref _readCount);
            public int MaxConcurrentReads => Volatile.Read(ref _maximum);
            public TaskCompletionSource FirstReadEntered { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<TelemetryProviderResult> ReadSnapshotAsync(
                CancellationToken cancellationToken = default)
            {
                var call = Interlocked.Increment(ref _readCount);
                var active = Interlocked.Increment(ref _active);
                UpdateMaximum(active);
                try
                {
                    if (call == 1)
                    {
                        FirstReadEntered.TrySetResult();
                        await _releaseFirst.Task.ConfigureAwait(false);
                    }

                    return Ready(call);
                }
                finally
                {
                    Interlocked.Decrement(ref _active);
                }
            }

            public void ReleaseFirstRead() => _releaseFirst.TrySetResult();

            private static TelemetryProviderResult Ready(int call)
            {
                var at = DateTimeOffset.UtcNow;
                var device = new TelemetryDeviceIdentity(
                    TelemetryDeviceKind.Gpu, "native:gpu", "Test GPU");
                var info = new SourceDeviceInfo(
                    TelemetrySourceKind.HwInfo,
                    device.Kind,
                    device.DeviceKey,
                    device.DisplayName,
                    0,
                    []);
                var reading = new TelemetryReading(
                    TelemetryMetricKey.GpuCoreTemperature,
                    100 + call,
                    TelemetryUnit.Celsius,
                    device,
                    TelemetrySourceKind.HwInfo,
                    "temp",
                    "GPU Core",
                    at);
                return new TelemetryProviderResult(
                    TelemetrySourceStatus.Ready,
                    "ready",
                    [],
                    [reading],
                    at,
                    [info]);
            }

            private void UpdateMaximum(int active)
            {
                while (true)
                {
                    var current = Volatile.Read(ref _maximum);
                    if (active <= current
                        || Interlocked.CompareExchange(ref _maximum, active, current) == current)
                    {
                        return;
                    }
                }
            }
        }

        private static async Task WaitUntilAsync(Func<bool> predicate)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
            while (!predicate() && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.True(predicate());
        }
    }
}

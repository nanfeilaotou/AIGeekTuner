using AIGeekTuner.Services.Telemetry.Recording;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Diagnostics;

namespace AIGeekTuner.Services.Telemetry
{
    /// <summary>数据来源模式：跟随录制器或自行采样。</summary>
    public enum LiveTelemetrySourceMode
    {
        Coordinator,
        Recorder,
    }

    public interface ILiveTelemetrySource
    {
        bool IsRunning { get; }

        LiveTelemetrySourceMode Mode { get; }

        int IntervalMs { get; }

        TelemetrySnapshot? LatestSnapshot { get; }

        /// <summary>新快照到达（后台线程）。订阅方自行调度到 UI 线程。</summary>
        event Action<TelemetrySnapshot>? SnapshotUpdated;

        void Start(int intervalMs);

        void Stop();

        Task StopAsync();
    }

    /// <summary>
    /// Hardware 页实时刷新协调器（§18-§23）。
    /// 录制中：不启动第二套 Hub 轮询，由 SampleCaptured 镜像推送（§22）；
    /// 否则：PeriodicTimer 串行循环（采样完成→下一拍），绝不重叠。
    /// 模式切换是自愈的：轮询循环每拍检查 IsRecording，
    /// 录制开始自动跳过 Hub 读取，录制结束自动恢复。
    /// </summary>
    public sealed class LiveTelemetryCoordinator : ILiveTelemetrySource, IDisposable, IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly ITelemetryHub _hub;
        private readonly ITelemetryRecordingService? _recorder;

        private CancellationTokenSource? _cts;
        private Task? _loop;
        private int _firstSnapshotLogged;
        private long _generation;
        private int _disposed;
        private bool _isRunning;
        private LiveTelemetrySourceMode _mode;
        private int _intervalMs;
        private TelemetrySnapshot? _latestSnapshot;

        public LiveTelemetryCoordinator(ITelemetryHub hub, ITelemetryRecordingService? recorder = null)
        {
            _hub = hub ?? throw new ArgumentNullException(nameof(hub));
            _recorder = recorder;

            // §22：镜像采样订阅挂接一次即可。是否真正发布由 IsRunning 把关——
            // 协调器未启动时，录制数据不会泄漏到硬件页。
            if (_recorder is not null)
            {
                _recorder.SampleCaptured += OnRecorderSample;
            }
        }

        public bool IsRunning
        {
            get { lock (_gate) return _isRunning; }
        }

        public LiveTelemetrySourceMode Mode
        {
            get { lock (_gate) return _mode; }
        }

        public int IntervalMs
        {
            get { lock (_gate) return _intervalMs; }
        }

        public TelemetrySnapshot? LatestSnapshot
        {
            get { lock (_gate) return _latestSnapshot; }
        }

        public event Action<TelemetrySnapshot>? SnapshotUpdated;

        public void Start(int intervalMs)
        {
            ThrowIfDisposed();
            // Start/Restart has an explicit hand-off: the old loop has observed
            // cancellation and exited before a new generation is installed.
            Stop();

            long generation;
            bool mirrorRecorder;
            lock (_gate)
            {
                ThrowIfDisposed();
                _intervalMs = Math.Max(200, intervalMs);
                // 录制中启动时不额外轮询 Hub；循环内每拍检测并跳过读取。
                _mode = IsRecording
                    ? LiveTelemetrySourceMode.Recorder
                    : LiveTelemetrySourceMode.Coordinator;
                _isRunning = true;
                mirrorRecorder = _mode == LiveTelemetrySourceMode.Recorder;

                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                var capturedInterval = _intervalMs;
                generation = ++_generation;
                _loop = Task.Run(
                    () => RunLoopAsync(generation, capturedInterval, ct),
                    CancellationToken.None);
            }

            if (mirrorRecorder)
            {
                MirrorRecorderLatest(generation);
            }
        }

        public void Stop()
        {
            StopAsync().GetAwaiter().GetResult();
        }

        public async Task StopAsync()
        {
            CancellationTokenSource? cancellation;
            Task? loop;
            lock (_gate)
            {
                _isRunning = false;
                _generation++;
                cancellation = _cts;
                loop = _loop;
                _cts = null;
                _loop = null;
            }

            if (cancellation is null)
            {
                return;
            }

            try
            {
                cancellation.Cancel();
                if (loop is not null)
                {
                    try
                    {
                        await loop.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected stop path.
                    }
                }
            }
            finally
            {
                cancellation.Dispose();
            }
        }

        private bool IsRecording => _recorder is { IsRecording: true };

        private async Task RunLoopAsync(
            long generation,
            int capturedInterval,
            CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(
                TimeSpan.FromMilliseconds(capturedInterval));
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var mode = ModeForNextCapture(generation, cancellationToken);
                    if (mode is null)
                    {
                        break;
                    }

                    if (mode == LiveTelemetrySourceMode.Coordinator)
                    {
                        var snapshot = await _hub
                            .ReadAsync(cancellationToken)
                            .ConfigureAwait(false);
                        TryPublish(snapshot, generation, LiveTelemetrySourceMode.Coordinator);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    ExceptionLogWriter.Write(exception, "LiveTelemetry capture");
                }

                try
                {
                    if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private LiveTelemetrySourceMode? ModeForNextCapture(
            long generation,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (!_isRunning
                    || generation != _generation
                    || cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                _mode = IsRecording
                    ? LiveTelemetrySourceMode.Recorder
                    : LiveTelemetrySourceMode.Coordinator;
                return _mode;
            }
        }

        private void MirrorRecorderLatest(long generation)
        {
            var recording = _recorder?.LiveSnapshot;
            if (recording is null || recording.LatestCapturedAtUtc is null)
            {
                return;
            }

            TryPublish(new TelemetrySnapshot(
                recording.LatestCapturedAtUtc.Value,
                recording.LatestReadings,
                recording.Sources,
                System.Array.Empty<RawTelemetryReading>()),
                generation,
                LiveTelemetrySourceMode.Recorder);
        }

        private bool TryPublish(
            TelemetrySnapshot snapshot,
            long generation,
            LiveTelemetrySourceMode sourceMode)
        {
            Action<TelemetrySnapshot>? subscribers;
            lock (_gate)
            {
                if (!_isRunning || generation != _generation || _cts?.IsCancellationRequested != false)
                {
                    return false;
                }

                var recording = IsRecording;
                if ((sourceMode == LiveTelemetrySourceMode.Coordinator && recording)
                    || (sourceMode == LiveTelemetrySourceMode.Recorder && !recording))
                {
                    return false;
                }

                _mode = sourceMode;
                _latestSnapshot = snapshot;
                subscribers = SnapshotUpdated;
            }

            if (Interlocked.Exchange(ref _firstSnapshotLogged, 1) == 0)
            {
                StartupBreadcrumbLogger.Write("FIRST_TELEMETRY_READY");
            }
            subscribers?.Invoke(snapshot);
            return true;
        }

        private void OnRecorderSample(TelemetrySample sample)
        {
            long generation;
            lock (_gate)
            {
                // 只有已启动（无论 Coordinator 还是 Recorder 模式）才镜像推送；
                // 未启动时静默丢弃，避免录制数据绕过硬件页的显示开关。
                if (!_isRunning || !IsRecording)
                {
                    return;
                }

                _mode = LiveTelemetrySourceMode.Recorder;
                _intervalMs = _recorder?.LiveSnapshot?.RequestedIntervalMs ?? _intervalMs;
                generation = _generation;
            }

            var liveSources = _recorder?.LiveSnapshot?.Sources;
            var sourceList = liveSources.HasValue
                ? liveSources.Value
                : System.Collections.Immutable.ImmutableArray<TelemetrySourceReport>.Empty;
            TryPublish(new TelemetrySnapshot(
                sample.CapturedAtUtc,
                sample.Readings,
                sourceList,
                Array.Empty<RawTelemetryReading>()),
                generation,
                LiveTelemetrySourceMode.Recorder);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Stop();
            if (_recorder is not null)
            {
                _recorder.SampleCaptured -= OnRecorderSample;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await StopAsync().ConfigureAwait(false);
            if (_recorder is not null)
            {
                _recorder.SampleCaptured -= OnRecorderSample;
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
        }
    }
}

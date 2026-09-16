using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Diagnostics;
using System.Collections.Immutable;

namespace AIGeekTuner.Services.Telemetry.Recording
{
    public interface ITelemetryRecordingService
    {
        bool IsRecording { get; }

        /// <summary>
        /// 活动会话的实时引用，仅供持久化/分析边界使用；UI 不得枚举其中的 Samples。
        /// </summary>
        TelemetryRecordingSession? CurrentSession { get; }

        /// <summary>只含展示指标的不可变增量快照；UI 刷新成本与指标数相关。</summary>
        TelemetryRecordingSnapshot? LiveSnapshot => null;

        TelemetrySample? LatestSample { get; }

        string? LastError { get; }

        /// <summary>最近一次 finalize 的保存路径（store 未配置或失败时为 null）。</summary>
        string? LastSavedPath { get; }

        /// <summary>每个新采样追加后触发（后台线程）——供实时视图共享采样（§22）。</summary>
        event Action<TelemetrySample>? SampleCaptured;

        /// <summary>开始录制；已有活动会话或间隔非法时返回 false（§35）。</summary>
        bool Start(int intervalMs);

        /// <summary>停止并 finalize（分析 + 原子保存）。未在录制时返回当前/最后会话。</summary>
        Task<TelemetryRecordingSession?> StopAsync();

        /// <summary>应用退出时的 best-effort 收尾；不阻塞超过 timeout。</summary>
        Task FinalizeIfRecordingAsync(TimeSpan timeout);
    }

    /// <summary>
    /// 手动录制服务（composition root 单实例，生命周期独立于页面，§33）。
    ///
    /// 数据原则（§1/§2）：只记录 Hub 已选择的 canonical 读数——目标是趋势、统计、
    /// 异常时间点与来源切换，而不是复制 HWiNFO/AIDA 的完整数据库。
    ///
    /// 调度规则（§4）：PeriodicTimer 串行循环，“采样完成→等待下一拍”，
    /// 读取慢于间隔也不会产生并发 telemetry read。每个 sample 记录真实 CapturedAtUtc（§5）。
    /// </summary>
    public sealed class TelemetryRecordingService : ITelemetryRecordingService
    {
        /// <summary>6 小时 × 1 秒 = 21600：达到上限自动正常收尾，绝不 OOM（§14）。</summary>
        public const int MaxSamples = 21_600;

        private readonly object _gate = new();
        private readonly ITelemetryHub _hub;
        private readonly ITelemetrySessionStore? _store;
        private readonly int _maxSamples;

        private TelemetryRecordingSession? _session;
        private CancellationTokenSource? _loopCts;
        private Task? _loopTask;

        // 事件检测状态（跨采样）
        private readonly Dictionary<TelemetrySourceKind, TelemetrySourceStatus> _lastSourceStatus = new();
        private readonly Dictionary<(TelemetryDeviceKind, string, string), TelemetrySourceKind> _lastMetricSource = new();
        private DateTimeOffset? _lastSuccessfulCaptureAtUtc;
        private int _sequence;
        private TelemetrySample? _latestSample;
        private readonly Dictionary<string, LiveMetricAccumulator> _liveMetrics = new(StringComparer.Ordinal);
        private TelemetryRecordingSnapshot? _liveSnapshot;

        public TelemetryRecordingService(
            ITelemetryHub hub,
            ITelemetrySessionStore? store = null,
            int? maxSamplesOverride = null)
        {
            _hub = hub ?? throw new ArgumentNullException(nameof(hub));
            _store = store;
            _maxSamples = maxSamplesOverride ?? MaxSamples;
        }

        /// <summary>最近一次 finalize 的保存路径；保存失败或未配置 store 时为 null。</summary>
        public string? LastSavedPath { get; private set; }
        public event Action<TelemetrySample>? SampleCaptured;

        public bool IsRecording
        {
            get
            {
                lock (_gate)
                {
                    return _session is not null && _session.Status == RecordingStatus.Recording;
                }
            }
        }

        public TelemetryRecordingSession? CurrentSession
        {
            get
            {
                lock (_gate)
                {
                    return _session;
                }
            }
        }

        public TelemetrySample? LatestSample
        {
            get
            {
                lock (_gate)
                {
                    return _latestSample;
                }
            }
        }

        public TelemetryRecordingSnapshot? LiveSnapshot
        {
            get
            {
                lock (_gate)
                {
                    return _liveSnapshot;
                }
            }
        }

        public string? LastError { get; private set; }

        public bool Start(int intervalMs)
        {
            if (intervalMs is < 200 or > 5000)
            {
                return false;
            }

            lock (_gate)
            {
                if (IsRecording || _loopTask is { IsCompleted: false })
                {
                    return false; // 单活动会话（§35）
                }

                _sequence = 0;
                _lastSourceStatus.Clear();
                _lastMetricSource.Clear();
                _lastSuccessfulCaptureAtUtc = null;
                _latestSample = null;
                _liveMetrics.Clear();
                LastError = null;
                var session = TelemetryRecordingSession.Start(intervalMs, DateTimeOffset.UtcNow);
                var loopCts = new CancellationTokenSource();
                _session = session;
                _loopCts = loopCts;
                _liveSnapshot = TelemetryRecordingSnapshot.Empty(
                    session.Id,
                    session.StartedAtUtc,
                    session.RequestedIntervalMs);
                var ct = loopCts.Token;
                var capturedInterval = intervalMs; // 间隔快照：录制期间不受设置变化影响（§39）

                _loopTask = Task.Run(async () =>
                {
                    try
                    {
                        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(capturedInterval));
                        while (!ct.IsCancellationRequested)
                        {
                            try
                            {
                                var sample = await CaptureOnceAsync(session, ct);
                                var reachedLimit = false;
                                lock (_gate)
                                {
                                    if (!ReferenceEquals(_session, session)
                                        || session.Status != RecordingStatus.Recording)
                                    {
                                        break;
                                    }

                                    session.AddSample(sample);
                                    _latestSample = sample;
                                    UpdateLiveSnapshotLocked(session, sample);
                                    reachedLimit = session.Samples.Count >= _maxSamples;
                                }

                                SampleCaptured?.Invoke(sample);
                                if (reachedLimit)
                                {
                                    TelemetryRecordingSession? finalized;
                                    lock (_gate)
                                    {
                                        // 上限保护：自动正常收尾（§14），包括分析与落盘。
                                        finalized = ReferenceEquals(_session, session)
                                            ? FinalizeCoreLocked(RecordingStatus.Completed)
                                            : null;
                                    }

                                    SaveIfPossible(finalized);
                                    break;
                                }
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested)
                            {
                                break;
                            }
                            catch (Exception exception)
                            {
                                // 单轮失败不终止会话：记 SampleGap 继续下一轮（§37）。
                                ExceptionLogWriter.Write(exception, "Telemetry/recorder capture");
                                session.AddEvent(TelemetrySessionEvent.Simple(
                                    TelemetrySessionEventType.SampleGap,
                                    DateTimeOffset.UtcNow,
                                    $"capture failed: {exception.Message}"));
                            }

                            try
                            {
                                if (!await timer.WaitForNextTickAsync(ct))
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
                    finally
                    {
                        lock (_gate)
                        {
                            if (ReferenceEquals(_loopCts, loopCts))
                            {
                                _loopCts = null;
                                _loopTask = null;
                            }
                        }

                        loopCts.Dispose();
                    }
                }, CancellationToken.None);

                return true;
            }
        }

        public async Task<TelemetryRecordingSession?> StopAsync()
        {
            Task? loop;
            lock (_gate)
            {
                if (!IsRecording && _loopTask is null)
                {
                    return _session;
                }

                _loopCts?.Cancel();
                loop = _loopTask;
            }

            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    ExceptionLogWriter.Write(exception, "Telemetry/recorder loop");
                }
            }

            TelemetryRecordingSession? finalized;
            lock (_gate)
            {
                finalized = FinalizeCoreLocked(RecordingStatus.Completed);
            }

            SaveIfPossible(finalized);
            return finalized;
        }

        public async Task FinalizeIfRecordingAsync(TimeSpan timeout)
        {
            if (!IsRecording)
            {
                return;
            }

            var finalize = StopAsync();
            await Task.WhenAny(finalize, Task.Delay(timeout)).ConfigureAwait(false);
        }

        private TelemetryRecordingSession? FinalizeCoreLocked(RecordingStatus status)
        {
            var session = _session;
            if (session is null || session.Status != RecordingStatus.Recording)
            {
                return session;
            }

            var finalized = session with
            {
                Status = status,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Summary = null,
            };
            // Analyze only after the immutable completed state exists so the
            // summary duration observes the same deterministic end timestamp.
            finalized = finalized with
            {
                Summary = TelemetrySessionAnalyzer.Analyze(finalized)
            };
            _session = finalized;
            if (_liveSnapshot is not null)
            {
                _liveSnapshot = _liveSnapshot with
                {
                    Status = status,
                    SampleCount = finalized.Samples.Count,
                    LatestCapturedAtUtc = _latestSample?.CapturedAtUtc,
                    Sources = GetSnapshotSources(finalized),
                };
            }
            return finalized;
        }

        private void SaveIfPossible(TelemetryRecordingSession? session)
        {
            if (session is null || _store is null)
            {
                return;
            }

            try
            {
                LastSavedPath = _store.Save(session);
            }
            catch (Exception exception)
            {
                LastSavedPath = null;
                ExceptionLogWriter.Write(exception, "Telemetry/session save");
            }
        }

        // ---- 内部：单次捕获（internal 供确定性测试直接驱动，§42） ----

        public async Task<TelemetrySample> CaptureOnceAsync(
            TelemetryRecordingSession session,
            CancellationToken cancellationToken)
        {
            var startedAt = DateTimeOffset.UtcNow;
            var snapshot = await _hub.ReadAsync(cancellationToken).ConfigureAwait(false);
            var completedAt = DateTimeOffset.UtcNow;

            var sequence = Interlocked.Increment(ref _sequence);
            var sample = new TelemetrySample(
                sequence,
                completedAt,
                (long)(completedAt - startedAt).TotalMilliseconds,
                snapshot.CanonicalReadings);

            RecordSourceEvents(session, snapshot.Sources, completedAt);
            RecordMetricSourceEvents(session, snapshot.CanonicalReadings, completedAt);
            session.SetLatestSources(snapshot.Sources);

            if (snapshot.CanonicalReadings.Count == 0)
            {
                session.AddEvent(TelemetrySessionEvent.Simple(
                    TelemetrySessionEventType.SampleGap,
                    completedAt,
                    "no canonical data this round"));
            }
            else
            {
                _lastSuccessfulCaptureAtUtc = completedAt;
                session.SetInitialSources(snapshot.Sources);
            }

            return sample;
        }

        private void UpdateLiveSnapshotLocked(
            TelemetryRecordingSession session,
            TelemetrySample sample)
        {
            foreach (var reading in sample.Readings)
            {
                var label = TelemetryLiveMetricLabel.For(reading);
                if (!_liveMetrics.TryGetValue(label, out var accumulator))
                {
                    accumulator = new LiveMetricAccumulator(reading.Unit);
                    _liveMetrics.Add(label, accumulator);
                }

                accumulator.Add(reading.Value, sample.CapturedAtUtc);
            }

            _liveSnapshot = new TelemetryRecordingSnapshot(
                session.Id,
                session.StartedAtUtc,
                session.RequestedIntervalMs,
                session.Status,
                session.Samples.Count,
                sample.CapturedAtUtc,
                sample.Readings.ToImmutableArray(),
                _liveMetrics.Select(pair => pair.Value.ToSnapshot(pair.Key)).ToImmutableArray(),
                GetSnapshotSources(session));
        }

        private static ImmutableArray<TelemetrySourceReport> GetSnapshotSources(
            TelemetryRecordingSession session) =>
            (session.LatestSources.Count > 0
                ? session.LatestSources
                : session.InitialSources)
            .ToImmutableArray();

        private sealed class LiveMetricAccumulator
        {
            private double _sum;

            public LiveMetricAccumulator(TelemetryUnit unit)
            {
                Unit = unit;
                Minimum = double.PositiveInfinity;
                Maximum = double.NegativeInfinity;
            }

            public TelemetryUnit Unit { get; }
            public double Current { get; private set; }
            public double Minimum { get; private set; }
            public double Maximum { get; private set; }
            public int Count { get; private set; }
            public DateTimeOffset LastAtUtc { get; private set; }

            public void Add(double value, DateTimeOffset atUtc)
            {
                Current = value;
                Minimum = Math.Min(Minimum, value);
                Maximum = Math.Max(Maximum, value);
                _sum += value;
                Count++;
                LastAtUtc = atUtc;
            }

            public TelemetryLiveMetricSnapshot ToSnapshot(string label) =>
                new(label, Unit, Current, Minimum, Maximum, _sum / Count, Count, LastAtUtc);
        }

        private void RecordSourceEvents(
            TelemetryRecordingSession session,
            IReadOnlyList<TelemetrySourceReport> reports,
            DateTimeOffset at)
        {
            foreach (var report in reports)
            {
                if (!_lastSourceStatus.TryGetValue(report.Source, out var previous))
                {
                    _lastSourceStatus[report.Source] = report.Status;
                    continue;
                }

                if (previous != report.Status)
                {
                    var type = report.Status == TelemetrySourceStatus.Unavailable
                        ? TelemetrySessionEventType.SourceUnavailable
                        : TelemetrySessionEventType.SourceStateChanged;
                    session.AddEvent(new TelemetrySessionEvent(
                        type,
                        at,
                        report.Source.ToString(),
                        null,
                        null,
                        previous.ToString(),
                        report.Status.ToString(),
                        report.Message));
                    _lastSourceStatus[report.Source] = report.Status;
                }
            }
        }

        private void RecordMetricSourceEvents(
            TelemetryRecordingSession session,
            IReadOnlyList<TelemetryReading> readings,
            DateTimeOffset at)
        {
            foreach (var reading in readings)
            {
                var key = (reading.Device.Kind, reading.Device.DeviceKey, reading.MetricKey.Value);
                if (!_lastMetricSource.TryGetValue(key, out var previous))
                {
                    _lastMetricSource[key] = reading.Source;
                    continue;
                }

                if (previous != reading.Source)
                {
                    session.AddEvent(new TelemetrySessionEvent(
                        TelemetrySessionEventType.MetricSourceChanged,
                        at,
                        reading.Source.ToString(),
                        reading.MetricKey.Value,
                        reading.Device.DeviceKey,
                        previous.ToString(),
                        reading.Source.ToString(),
                        reading.Device.DisplayName + " · " + reading.MetricKey.Value));
                    _lastMetricSource[key] = reading.Source;
                }
            }
        }
    }
}


using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Diagnostics;
using System.Diagnostics;

namespace AIGeekTuner.Services.Telemetry
{
    /// <summary>
    /// 遥测聚合实现：
    /// 1) 并行读取全部 Provider，单源失败被完全隔离（一个 Provider 崩不拖垮整体）；
    /// 2) 每个 Provider 有独立超时，慢源不会让页面永远 Loading；
    /// 3) 对“同设备 + 同规范指标”按固定优先级 HWiNFO → AIDA64 → LHM 选择，
    ///    绝不对多个来源的数值做平均或混合；
    /// 4) 输出统一快照 + 各来源状态报告。
    /// </summary>
    public sealed class TelemetryHub : ITelemetryHub
    {
        /// <summary>第一版固定来源优先级（§12）；数值即选择顺序。</summary>
        public static readonly IReadOnlyList<TelemetrySourceKind> FixedPriorityOrder =
        [
            TelemetrySourceKind.HwInfo,
            TelemetrySourceKind.Aida64,
            TelemetrySourceKind.LibreHardwareMonitor
        ];

        private static readonly TimeSpan DefaultPerProviderTimeout = TimeSpan.FromSeconds(8);

        private readonly ProviderReadSlot[] _providerSlots;
        private readonly TelemetryDeviceLifetimeMap _deviceLifetimeMap = new();

        public TelemetryHub(
            IEnumerable<ITelemetryProvider> providers,
            TimeSpan? perProviderTimeout = null)
        {
            ArgumentNullException.ThrowIfNull(providers);

            var materialized = providers.ToArray();
            var duplicates = materialized
                .GroupBy(provider => provider.SourceKind)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicates is not null)
            {
                throw new ArgumentException(
                    $"Duplicate provider for source '{duplicates.Key}'.",
                    nameof(providers));
            }

            var orderedProviders = FixedPriorityOrder
                .Select(kind => materialized
                    .FirstOrDefault(provider => provider.SourceKind == kind))
                .Where(provider => provider is not null)
                .Cast<ITelemetryProvider>()
                .ToArray();
            var timeout = perProviderTimeout ?? DefaultPerProviderTimeout;
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(perProviderTimeout));
            }

            _providerSlots = orderedProviders
                .Select(provider => new ProviderReadSlot(provider, timeout))
                .ToArray();
        }

        public async Task<TelemetrySnapshot> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tasks = _providerSlots
                .Select(slot => slot.ReadWithinDeadlineAsync(cancellationToken))
                .ToArray();
            // Provider 槽只在“外层取消”时抛出，业务失败一律降级为来源报告。
            await Task.WhenAll(tasks);

            var outcomes = new Dictionary<TelemetrySourceKind, ProviderOutcome>();
            for (var i = 0; i < _providerSlots.Length; i++)
            {
                outcomes[_providerSlots[i].SourceKind] = await tasks[i];
            }

            // 设备 Reconciliation（§14）：跨源设备合并只依据证据；
            // 未合并的源本地设备保留独立命名空间，绝不因 ordinal 相同而互通。
            var reconciliation = _deviceLifetimeMap.Resolve(
                ReconcileDevices(outcomes));

            var canonicalReadings =
                SelectCanonicalReadings(outcomes, reconciliation);
            var sourceReports = BuildSourceReports(outcomes);

            var rawReadings = outcomes.Values
                .Where(outcome => outcome.HasUsableData)
                .Select(outcome => outcome.Result!)
                .SelectMany(result => result.RawReadings)
                .ToArray();

            return new TelemetrySnapshot(
                DateTimeOffset.UtcNow,
                canonicalReadings,
                sourceReports,
                rawReadings);
        }

        private static IReadOnlyList<CanonicalDeviceGroup> ReconcileDevices(
            IReadOnlyDictionary<TelemetrySourceKind, ProviderOutcome> outcomes)
        {
            var devices = outcomes.Values
                .Where(outcome => outcome.HasUsableData)
                .Select(outcome => outcome.Result!)
                .ToArray();
            var deviceInfos = devices.SelectMany(result => result.Devices).ToArray();
            return TelemetryDeviceReconciler.Reconcile(deviceInfos);
        }

        private static List<TelemetryReading> SelectCanonicalReadings(
            IReadOnlyDictionary<TelemetrySourceKind, ProviderOutcome> outcomes,
            DeviceReconciliationLookup lookup)
        {
            var selected = new List<TelemetryReading>();
            var claimedKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var kind in FixedPriorityOrder)
            {
                if (!outcomes.TryGetValue(kind, out var outcome))
                {
                    continue;
                }

                var sourceResult = outcome.Result;
                if (sourceResult is null || !outcome.HasUsableData)
                {
                    continue;
                }

                foreach (var reading in sourceResult.CanonicalReadings)
                {
                    // 把源侧本地设备映射到 canonical 身份；未合并的设备落在
                    // src:{source}:{nativeId} 命名空间中，天然与其他来源隔离。
                    if (!lookup.TryResolve(kind, reading.Device.DeviceKey, out var canonical))
                    {
                        canonical = reading.Device;
                    }

                    // 选择键 = canonical 设备 + 指标（绝不含来源）：
                    // 同一物理设备的同指标只允许最高优先级来源占位。
                    var claimKey = string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"{(int)canonical.Kind}{canonical.DeviceKey}{reading.MetricKey.Value}");
                    if (claimedKeys.Add(claimKey))
                    {
                        selected.Add(new TelemetryReading(
                            reading.MetricKey,
                            reading.Value,
                            reading.Unit,
                            canonical,
                            reading.Source,
                            reading.SourceMetricId,
                            reading.SourceLabel,
                            reading.CapturedAtUtc));
                    }
                }
            }

            return selected;
        }

        private static IReadOnlyList<TelemetrySourceReport> BuildSourceReports(
            IReadOnlyDictionary<TelemetrySourceKind, ProviderOutcome> outcomes)
        {
            var reports = new List<TelemetrySourceReport>();
            foreach (var kind in FixedPriorityOrder)
            {
                if (!outcomes.TryGetValue(kind, out var outcome))
                {
                    continue;
                }

                DateTimeOffset? lastReadAtUtc = null;
                int canonicalCount = 0;
                string? sourceVersion = null;
                var result = outcome.Result;
                if (result is not null)
                {
                    sourceVersion = result.SourceVersion;
                    if (result.Status is TelemetrySourceStatus.Ready
                        or TelemetrySourceStatus.Degraded)
                    {
                        lastReadAtUtc = result.CapturedAtUtc;
                        canonicalCount = result.CanonicalReadings.Count;
                    }
                }

                reports.Add(new TelemetrySourceReport(
                    kind,
                    outcome.Status,
                    outcome.Message,
                    outcome.RawReadingCount,
                    lastReadAtUtc,
                    canonicalCount,
                    outcome.ReadDurationMs,
                    sourceVersion));
            }

            return reports;
        }

        private static void Log(
            ITelemetryProvider provider,
            Exception exception,
            string verb) =>
            ExceptionLogWriter.Write(
                exception,
                $"Telemetry/{provider.SourceKind} {verb}");

        /// <summary>
        /// Provider-scoped single flight. A deadline ends only the caller's wait;
        /// an uncooperative native read remains the sole occupant until it really
        /// completes, at which point the continuation observes and releases it.
        /// </summary>
        private sealed class ProviderReadSlot
        {
            private readonly object _gate = new();
            private readonly ITelemetryProvider _provider;
            private readonly TimeSpan _timeout;
            private ProviderOperation? _inFlight;

            public ProviderReadSlot(ITelemetryProvider provider, TimeSpan timeout)
            {
                _provider = provider;
                _timeout = timeout;
            }

            public TelemetrySourceKind SourceKind => _provider.SourceKind;

            public async Task<ProviderOutcome> ReadWithinDeadlineAsync(
                CancellationToken outerToken)
            {
                outerToken.ThrowIfCancellationRequested();

                ProviderOperation operation;
                lock (_gate)
                {
                    if (_inFlight is { Task.IsCompleted: false } current)
                    {
                        outerToken.ThrowIfCancellationRequested();
                        return ProviderOutcome.Busy(
                            "上一次底层读取仍在进行；本轮未启动重复读取。",
                            ElapsedMilliseconds(current.StartTimestamp));
                    }

                    operation = StartOperation();
                    _inFlight = operation;
                }

                ObserveAndRelease(operation);

                try
                {
                    var result = await operation.Task
                        .WaitAsync(_timeout, outerToken)
                        .ConfigureAwait(false);
                    return ProviderOutcome.From(
                        result,
                        ElapsedMilliseconds(operation.StartTimestamp));
                }
                catch (OperationCanceledException) when (outerToken.IsCancellationRequested)
                {
                    TryCancel(operation.Cancellation);
                    throw;
                }
                catch (TimeoutException exception)
                {
                    TryCancel(operation.Cancellation);
                    Log(_provider, exception, "wait timed out");
                    return ProviderOutcome.TimeoutFailure(
                        $"读取等待超时（>{_timeout.TotalSeconds:0.#} 秒）；底层任务可能仍在结束。",
                        ElapsedMilliseconds(operation.StartTimestamp));
                }
                catch (OperationCanceledException exception)
                {
                    Log(_provider, exception, "timed out");
                    return ProviderOutcome.TimeoutFailure(
                        $"读取超时（>{_timeout.TotalSeconds:0.#} 秒）。",
                        ElapsedMilliseconds(operation.StartTimestamp));
                }
                catch (Exception exception)
                {
                    Log(_provider, exception, "failed");
                    return ProviderOutcome.RuntimeFailure(
                        "读取失败。",
                        ElapsedMilliseconds(operation.StartTimestamp));
                }
            }

            private ProviderOperation StartOperation()
            {
                var cancellation = new CancellationTokenSource();
                cancellation.CancelAfter(_timeout);
                var started = Stopwatch.GetTimestamp();
                var task = Task.Run(
                    async () => await _provider
                        .ReadSnapshotAsync(cancellation.Token)
                        .ConfigureAwait(false),
                    CancellationToken.None);
                return new ProviderOperation(task, cancellation, started);
            }

            private void ObserveAndRelease(ProviderOperation operation)
            {
                _ = operation.Task.ContinueWith(
                    completed =>
                    {
                        // Observe late faults after a caller-side timeout.
                        _ = completed.Exception;
                        lock (_gate)
                        {
                            if (ReferenceEquals(_inFlight, operation))
                            {
                                _inFlight = null;
                            }
                        }

                        operation.Cancellation.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            private static void TryCancel(CancellationTokenSource cancellation)
            {
                try
                {
                    cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Completion may win the race and dispose the slot CTS.
                }
            }

            private static long ElapsedMilliseconds(long startedTimestamp) =>
                (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;

            private sealed record ProviderOperation(
                Task<TelemetryProviderResult> Task,
                CancellationTokenSource Cancellation,
                long StartTimestamp);
        }

        private sealed record ProviderOutcome(
            TelemetrySourceStatus Status,
            string Message,
            int RawReadingCount,
            TelemetryProviderResult? Result,
            long ReadDurationMs)
        {
            public bool HasUsableData =>
                Result is not null
                && Status is TelemetrySourceStatus.Ready or TelemetrySourceStatus.Degraded;

            public static ProviderOutcome From(TelemetryProviderResult result, long durationMs) =>
                new(result.Status, result.Message, result.RawReadings.Count, result, durationMs);

            public static ProviderOutcome TimeoutFailure(string message, long durationMs) =>
                new(TelemetrySourceStatus.Timeout, message, 0, null, durationMs);

            public static ProviderOutcome Busy(string message, long durationMs) =>
                new(TelemetrySourceStatus.Busy, message, 0, null, durationMs);

            public static ProviderOutcome RuntimeFailure(string message, long durationMs) =>
                new(TelemetrySourceStatus.Error, message, 0, null, durationMs);
        }
    }
}

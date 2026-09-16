using System.Globalization;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Diagnostics;
using LibreHardwareMonitor.Hardware;

namespace AIGeekTuner.Services.Telemetry.LibreHardwareMonitor
{
    /// <summary>
    /// 把现有 LibreHardwareMonitor 能力接入统一遥测层的 Provider。
    /// 本类型独立遍历硬件树并输出 Raw 读数；V1 展示路径使用同一安全的
    /// CPU/GPU 分离配置，保持兼容并避免触发 LHM Intel GCL。
    /// 业务性失败以状态表达；意外异常向上抛出，由 Hub 统一隔离与留痕。
    /// </summary>
    public sealed class LibreHardwareMonitorTelemetryProvider : ITelemetryProvider
    {
        public TelemetrySourceKind SourceKind => TelemetrySourceKind.LibreHardwareMonitor;

        public async Task<TelemetryProviderResult> ReadSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() => ReadCore(cancellationToken), cancellationToken);
        }

        private static TelemetryProviderResult ReadCore(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var computers = new[]
            {
                (Scope: "CORE", Computer: LibreHardwareMonitorComputerFactory.CreateCoreComputer()),
                (Scope: "GPU_SAFE", Computer: LibreHardwareMonitorComputerFactory.CreateGpuComputer())
            };
            var capturedAtUtc = DateTimeOffset.UtcNow;
            var nodes = new List<HardwareNode>();

            foreach (var (scope, computer) in computers)
            {
                    try
                    {
                        StartupBreadcrumbLogger.WriteOnce($"LHM_{scope}_OPEN_BEGIN");
                        computer.Open();
                        StartupBreadcrumbLogger.WriteOnce($"LHM_{scope}_OPEN_OK");
                        foreach (var hardware in computer.Hardware)
                        {
                            Visit(hardware, nodes, cancellationToken);
                        }
                    }
                    finally
                    {
                        try
                        {
                            computer.Close();
                        }
                        catch
                        {
                            // Closing a partially opened provider cannot mask
                            // the read result or turn an optional source into a
                            // process-level failure.
                        }
                    }
            }

            var devices = AssignDeviceIdentities(nodes);
            var rawReadings = MaterializeRawReadings(nodes, devices.Identities, devices.Infos, capturedAtUtc);
            var canonicalReadings = LibreHardwareMonitorCanonicalMapper.Map(rawReadings);

            return new TelemetryProviderResult(
                    TelemetrySourceStatus.Ready,
                    $"内置传感器读取成功（{rawReadings.Count} 项）。",
                    rawReadings,
                    canonicalReadings,
                    capturedAtUtc,
                    devices.Infos
                        .GroupBy(info => info.NativeDeviceId)
                        .Select(group => group.First())
                        .ToArray());
            }

        private static void Visit(
            IHardware hardware,
            ICollection<HardwareNode> nodes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hardware.Update();

            var node = new HardwareNode(
                hardware.HardwareType,
                hardware.Name,
                hardware.Identifier.ToString());
            foreach (var sensor in hardware.Sensors)
            {
                if (!sensor.Value.HasValue
                    || !float.IsFinite(sensor.Value.Value))
                {
                    continue;
                }

                node.Sensors.Add(new NativeSensor(
                    sensor.SensorType,
                    sensor.Name ?? string.Empty,
                    sensor.Value.Value));
            }

            nodes.Add(node);

            foreach (var subHardware in hardware.SubHardware)
            {
                Visit(subHardware, nodes, cancellationToken);
            }
        }

        private static IReadOnlyList<RawTelemetryReading> MaterializeRawReadings(
            IReadOnlyList<HardwareNode> nodes,
            Dictionary<HardwareNode, TelemetryDeviceIdentity> identities,
            IReadOnlyList<SourceDeviceInfo> deviceInfos,
            DateTimeOffset capturedAtUtc)
        {
            // 同一源本地键可能对应多个节点（如双路 CPU / 多内存组）：
            // 取第一个作为该设备的代表事实，其余节点共享同一身份。
            var infoByKey = new Dictionary<string, SourceDeviceInfo>(StringComparer.Ordinal);
            foreach (var info in deviceInfos)
            {
                infoByKey.TryAdd(info.NativeDeviceId, info);
            }
            var readings = new List<RawTelemetryReading>();

            foreach (var node in nodes)
            {
                var device = identities[node];
                var info = infoByKey[device.DeviceKey];
                foreach (var sensor in node.Sensors)
                {
                    readings.Add(new RawTelemetryReading(
                        TelemetrySourceKind.LibreHardwareMonitor,
                        SourceMetricId(sensor),
                        sensor.Name,
                        sensor.Value,
                        ToNativeUnit(sensor.Type),
                        device,
                        info,
                        capturedAtUtc));
                }
            }

            return readings;
        }

        private static string SourceMetricId(NativeSensor sensor) =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"{sensor.Type}:{sensor.Name}");

        private sealed record NodeDevices(
            Dictionary<HardwareNode, TelemetryDeviceIdentity> Identities,
            List<SourceDeviceInfo> Infos);

        private static NodeDevices AssignDeviceIdentities(
            IReadOnlyList<HardwareNode> nodes)
        {
            var assignments = new Dictionary<HardwareNode, TelemetryDeviceIdentity>();
            var infos = new List<SourceDeviceInfo>();

            var gpuGroups = nodes
                .Where(node => IsGpu(node.Type))
                .GroupBy(node => node.Identifier, StringComparer.Ordinal)
                .Select((group, order) => new
                {
                    Identifier = group.Key,
                    Type = group.First().Type,
                    Name = group.First().Name,
                    FirstOrder = group.Min(node => node.Order)
                })
                .OrderBy(group => GpuPriority(group.Type))
                .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => group.Identifier, StringComparer.Ordinal)
                .ThenBy(group => group.FirstOrder)
                .ToArray();
            var gpuIndexByIdentifier = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < gpuGroups.Length; i++)
            {
                gpuIndexByIdentifier[gpuGroups[i].Identifier] = i;
            }

            foreach (var node in nodes)
            {
                TelemetryDeviceIdentity identity;
                string nativeId;
                int ordinal = 0;
                switch (node.Type)
                {
                    case HardwareType.Cpu:
                        nativeId = NativeDeviceKey(node.Identifier);
                        identity = new TelemetryDeviceIdentity(
                            TelemetryDeviceKind.Cpu, nativeId, node.Name);
                        break;

                    default:
                        if (IsGpu(node.Type))
                        {
                            ordinal = gpuIndexByIdentifier[node.Identifier];
                            nativeId = NativeDeviceKey(node.Identifier);
                            identity = new TelemetryDeviceIdentity(
                                TelemetryDeviceKind.Gpu, nativeId, node.Name);
                        }
                        else if (node.Type == HardwareType.Memory
                            && MemoryModuleSensorNames.TryGetModuleIndex(node.Name, out var moduleIndex))
                        {
                            // V2-M4.5B Gate F：LHM 的每模块硬件（RAM Module #N）独立于
                            // 聚合 Memory 硬件，明确模块 parent 才给 MemoryModule 身份。
                            nativeId = NativeDeviceKey(node.Identifier);
                            identity = new TelemetryDeviceIdentity(
                                TelemetryDeviceKind.MemoryModule, nativeId, node.Name);
                            ordinal = moduleIndex;
                        }
                        else if (node.Type == HardwareType.Memory)
                        {
                            nativeId = NativeDeviceKey(node.Identifier);
                            identity = new TelemetryDeviceIdentity(
                                TelemetryDeviceKind.Memory, nativeId, node.Name);
                        }
                        else if (node.Type == HardwareType.Storage)
                        {
                            nativeId = NativeDeviceKey(node.Identifier);
                            identity = new TelemetryDeviceIdentity(
                                TelemetryDeviceKind.Storage, nativeId, node.Name);
                        }
                        else
                        {
                            nativeId = NativeDeviceKey(node.Identifier);
                            identity = new TelemetryDeviceIdentity(
                                TelemetryDeviceKind.System, nativeId, node.Name);
                        }

                        break;
                }

                assignments[node] = identity;
                infos.Add(new SourceDeviceInfo(
                    TelemetrySourceKind.LibreHardwareMonitor,
                    identity.Kind,
                    nativeId,
                    node.Name,
                    ordinal,
                    []));
            }

            return new NodeDevices(assignments, infos);
        }

        private static string NativeDeviceKey(string identifier) => $"lhm:{identifier}";

        internal static IReadOnlyList<SourceDeviceInfo> DescribeDevicesForTest(
            IEnumerable<(HardwareType Type, string Name, string Identifier)> hardware)
        {
            var nodes = hardware
                .Select(item => new HardwareNode(item.Type, item.Name, item.Identifier))
                .ToArray();
            return AssignDeviceIdentities(nodes).Infos
                .GroupBy(info => info.NativeDeviceId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
        }

        // LibreHardwareMonitorLib 0.9.6 的 HardwareType 仅含这三类 GPU。
        private static bool IsGpu(HardwareType type) =>
            type is HardwareType.GpuNvidia
                or HardwareType.GpuAmd
                or HardwareType.GpuIntel;

        private static int GpuPriority(HardwareType type) =>
            type switch
            {
                HardwareType.GpuNvidia => 0,
                HardwareType.GpuAmd => 1,
                HardwareType.GpuIntel => 2,
                _ => 3
            };

        private static TelemetryUnit ToNativeUnit(SensorType type) =>
            type switch
            {
                SensorType.Temperature => TelemetryUnit.Celsius,
                SensorType.Power => TelemetryUnit.Watt,
                SensorType.Clock => TelemetryUnit.Megahertz,
                SensorType.Load => TelemetryUnit.Percent,
                SensorType.Voltage => TelemetryUnit.Volt,
                SensorType.Data => TelemetryUnit.Gigabyte,
                SensorType.SmallData => TelemetryUnit.Megabyte,
                _ => TelemetryUnit.None
            };

        private sealed record NativeSensor(SensorType Type, string Name, double Value);

        private sealed class HardwareNode
        {
            public HardwareNode(HardwareType type, string name, string identifier)
            {
                Type = type;
                Name = name;
                Identifier = identifier;
                Order = NextOrder();
                Sensors = [];
            }

            public HardwareType Type { get; }

            public string Name { get; }

            public string Identifier { get; }

            public int Order { get; }

            public List<NativeSensor> Sensors { get; }

            private static int NextOrder() =>
                Interlocked.Increment(ref OrderSequence);

            private static int OrderSequence;
        }
    }
}

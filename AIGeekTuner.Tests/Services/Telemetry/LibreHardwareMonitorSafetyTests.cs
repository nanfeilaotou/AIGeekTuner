using AIGeekTuner.Services.Telemetry.LibreHardwareMonitor;
using AIGeekTuner.Models.Telemetry;
using LibreHardwareMonitor.Hardware;
using Xunit;

namespace AIGeekTuner.Tests.Services.Telemetry;

public sealed class LibreHardwareMonitorSafetyTests
{
    [Fact]
    public void CoreComputer_DisablesGpuEnumeration()
    {
        var computer = LibreHardwareMonitorComputerFactory.CreateCoreComputer();

        Assert.True(computer.IsCpuEnabled);
        Assert.False(computer.IsGpuEnabled);
        Assert.True(computer.IsMemoryEnabled);
        Assert.True(computer.IsStorageEnabled);
    }

    [Fact]
    public void GpuComputer_DisablesCpuAndNonGpuEnumeration()
    {
        var computer = LibreHardwareMonitorComputerFactory.CreateGpuComputer();

        Assert.False(computer.IsCpuEnabled);
        Assert.True(computer.IsGpuEnabled);
        Assert.False(computer.IsMemoryEnabled);
        Assert.False(computer.IsStorageEnabled);
    }

    [Fact]
    public void ProductionScopes_CannotEnableCpuAndGpuTogether()
    {
        var core = LibreHardwareMonitorComputerFactory.CreateCoreComputer();
        var gpu = LibreHardwareMonitorComputerFactory.CreateGpuComputer();

        Assert.False(core.IsCpuEnabled && core.IsGpuEnabled);
        Assert.False(gpu.IsCpuEnabled && gpu.IsGpuEnabled);
    }

    [Fact]
    public void SameModelGpus_KeepDistinctHardwareIdentifiers()
    {
        var devices = LibreHardwareMonitorTelemetryProvider.DescribeDevicesForTest(
        [
            (HardwareType.GpuNvidia, "NVIDIA GeForce RTX 4090", "/gpu-nvidia/0"),
            (HardwareType.GpuNvidia, "NVIDIA GeForce RTX 4090", "/gpu-nvidia/1"),
        ]);

        Assert.Equal(2, devices.Count);
        Assert.All(devices, device => Assert.Equal(TelemetryDeviceKind.Gpu, device.Kind));
        Assert.Equal(2, devices.Select(device => device.NativeDeviceId).Distinct().Count());
        Assert.Contains(devices, device => device.NativeDeviceId == "lhm:/gpu-nvidia/0");
        Assert.Contains(devices, device => device.NativeDeviceId == "lhm:/gpu-nvidia/1");
    }

    [Fact]
    public void SameModelStorage_ReversedEnumeration_KeepsIdentifierKeys()
    {
        var forward = LibreHardwareMonitorTelemetryProvider.DescribeDevicesForTest(
        [
            (HardwareType.Storage, "Same SSD", "/nvme/0"),
            (HardwareType.Storage, "Same SSD", "/nvme/1"),
        ]);
        var reversed = LibreHardwareMonitorTelemetryProvider.DescribeDevicesForTest(
        [
            (HardwareType.Storage, "Same SSD", "/nvme/1"),
            (HardwareType.Storage, "Same SSD", "/nvme/0"),
        ]);

        Assert.Equal(
            forward.Select(device => device.NativeDeviceId).Order().ToArray(),
            reversed.Select(device => device.NativeDeviceId).Order().ToArray());
    }
}

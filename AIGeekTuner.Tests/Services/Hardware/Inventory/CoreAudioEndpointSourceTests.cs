using System.Runtime.InteropServices;
using AIGeekTuner.Models.Hardware.Inventory;
using AIGeekTuner.Services.Hardware.Inventory;

namespace AIGeekTuner.Tests.Services.Hardware.Inventory;

public sealed class CoreAudioEndpointSourceTests
{
    private const ushort VtEmpty = 0;
    private const ushort VtI4 = 3;
    private const ushort VtLpwstr = 31;

    [Fact]
    public void PropVariant_LayoutMatchesCurrentWindowsAbi()
    {
        Assert.Equal(IntPtr.Size == 8 ? 24 : 16, Marshal.SizeOf<PropVariant>());
        Assert.Equal(0, Marshal.OffsetOf<PropVariant>(nameof(PropVariant.VariantType)).ToInt32());
        Assert.Equal(2, Marshal.OffsetOf<PropVariant>(nameof(PropVariant.Reserved1)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<PropVariant>(nameof(PropVariant.Reserved2)).ToInt32());
        Assert.Equal(6, Marshal.OffsetOf<PropVariant>(nameof(PropVariant.Reserved3)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<PropVariant>(nameof(PropVariant.Value)).ToInt32());
        Assert.Equal(0, Marshal.OffsetOf<PropVariant>(nameof(PropVariant.DecimalValue)).ToInt32());
        Assert.Equal(16, Marshal.SizeOf<PropVariantDecimal>());
        Assert.Equal(0, Marshal.OffsetOf<PropVariantValue>(nameof(PropVariantValue.Pointer)).ToInt32());
        Assert.Equal(
            IntPtr.Size == 8 ? 8 : 4,
            Marshal.OffsetOf<PropVariantCountedPointer>(
                nameof(PropVariantCountedPointer.Pointer)).ToInt32());
    }

    [Fact]
    public void PropVariant_ArchitectureContractsAreExplicit()
    {
        Assert.Equal(16, CoreAudioEndpointSource.ExpectedPropVariantSize(4));
        Assert.Equal(24, CoreAudioEndpointSource.ExpectedPropVariantSize(8));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CoreAudioEndpointSource.ExpectedPropVariantSize(16));
    }

    [Fact]
    public void ReadProperty_VtEmpty_ReturnsNullAndClearsOnce()
    {
        var clears = 0;

        var result = CoreAudioEndpointSource.ReadPropertyValue(
            GetValue,
            14,
            _ => throw new InvalidOperationException("converter must not run"),
            Clear);

        Assert.Null(result);
        Assert.Equal(1, clears);

        static int GetValue(ref PropertyKey _, out PropVariant value)
        {
            value = CreateVariant(VtEmpty);
            return 0;
        }

        int Clear(ref PropVariant _) => ++clears;
    }

    [Fact]
    public void ReadProperty_VtLpwstr_CopiesBeforeNativeClear()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pointer = Marshal.StringToCoTaskMemUni("  USB Audio  ");
        var clears = 0;

        var result = CoreAudioEndpointSource.ReadPropertyValue(
            GetValue,
            14,
            Marshal.PtrToStringUni,
            Clear);

        Assert.Equal("USB Audio", result);
        Assert.Equal(1, clears);

        int GetValue(ref PropertyKey _, out PropVariant value)
        {
            value = CreateVariant(VtLpwstr, pointer);
            return 0;
        }

        int Clear(ref PropVariant value)
        {
            clears++;
            return CoreAudioEndpointSource.ClearPropVariant(ref value);
        }
    }

    [Fact]
    public void ReadProperty_UnsupportedType_ReturnsNullAndClearsOnce()
    {
        var clears = 0;

        var result = CoreAudioEndpointSource.ReadPropertyValue(
            GetValue,
            14,
            _ => throw new InvalidOperationException("converter must not run"),
            Clear);

        Assert.Null(result);
        Assert.Equal(1, clears);

        static int GetValue(ref PropertyKey _, out PropVariant value)
        {
            value = CreateVariant(VtI4);
            value.Value.Int32 = 42;
            return 0;
        }

        int Clear(ref PropVariant _) => ++clears;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ReadProperty_NonNegativeHresult_IsSuccessAndClears(int hresult)
    {
        var clears = 0;

        var result = CoreAudioEndpointSource.ReadPropertyValue(
            GetValue,
            14,
            _ => "Endpoint",
            Clear);

        Assert.Equal("Endpoint", result);
        Assert.Equal(1, clears);

        int GetValue(ref PropertyKey _, out PropVariant value)
        {
            value = CreateVariant(VtLpwstr, new IntPtr(1));
            return hresult;
        }

        int Clear(ref PropVariant _) => ++clears;
    }

    [Fact]
    public void ReadProperty_FailedHresult_DoesNotInterpretOrClear()
    {
        var clears = 0;
        var converts = 0;

        var result = CoreAudioEndpointSource.ReadPropertyValue(
            GetValue,
            14,
            _ =>
            {
                converts++;
                return "unexpected";
            },
            Clear);

        Assert.Null(result);
        Assert.Equal(0, converts);
        Assert.Equal(0, clears);

        static int GetValue(ref PropertyKey _, out PropVariant value)
        {
            value = default;
            return unchecked((int)0x80004005);
        }

        int Clear(ref PropVariant _) => ++clears;
    }

    [Fact]
    public void ReadProperty_ConversionThrows_ClearsExactlyOnce()
    {
        var clears = 0;

        Assert.Throws<FormatException>(() =>
            CoreAudioEndpointSource.ReadPropertyValue(
                GetValue,
                14,
                _ => throw new FormatException("bad native text"),
                Clear));

        Assert.Equal(1, clears);

        static int GetValue(ref PropertyKey _, out PropVariant value)
        {
            value = CreateVariant(VtLpwstr, new IntPtr(1));
            return 0;
        }

        int Clear(ref PropVariant _) => ++clears;
    }

    [Fact]
    public void GetEndpoints_OnWindows_IsNonFatalWithoutAudioHardware()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        IReadOnlyList<AudioInventoryMapper.EndpointDescriptor>? endpoints = null;
        var exception = Record.Exception(() =>
            endpoints = new CoreAudioEndpointSource().GetEndpoints());
        Assert.Null(exception);
        Assert.NotNull(endpoints);
        Assert.All(endpoints!, endpoint => Assert.False(string.IsNullOrWhiteSpace(endpoint.Id)));
    }

    private static PropVariant CreateVariant(ushort variantType, IntPtr pointer = default)
    {
        var value = new PropVariant { VariantType = variantType };
        value.Value.Pointer = pointer;
        return value;
    }
}

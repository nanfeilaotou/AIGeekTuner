using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AIGeekTuner.Models.Hardware.Inventory;
using AIGeekTuner.Services.Diagnostics;

namespace AIGeekTuner.Services.Hardware.Inventory
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    /// <summary>
    /// Native PROPVARIANT payload members whose ABI size depends on pointer size.
    /// The leading uint plus natural pointer alignment is 8 bytes on x86 and
    /// 16 bytes on x64, matching BLOB/CA* payloads in propidl.h.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariantCountedPointer
    {
        public uint Count;
        public IntPtr Pointer;
    }

    /// <summary>Native DECIMAL overlay used by the outer PROPVARIANT union.</summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariantDecimal
    {
        [FieldOffset(0)] public ushort Reserved;
        [FieldOffset(2)] public byte Scale;
        [FieldOffset(3)] public byte Sign;
        [FieldOffset(4)] public uint High;
        [FieldOffset(8)] public ulong Low;
    }

    /// <summary>
    /// Native PROPVARIANT value union. It includes the scalar, pointer and
    /// counted-pointer shapes needed to give the union its complete ABI extent.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariantValue
    {
        [FieldOffset(0)] public sbyte SignedByte;
        [FieldOffset(0)] public byte Byte;
        [FieldOffset(0)] public short Int16;
        [FieldOffset(0)] public ushort UInt16;
        [FieldOffset(0)] public int Int32;
        [FieldOffset(0)] public uint UInt32;
        [FieldOffset(0)] public long Int64;
        [FieldOffset(0)] public ulong UInt64;
        [FieldOffset(0)] public float Single;
        [FieldOffset(0)] public double Double;
        [FieldOffset(0)] public IntPtr Pointer;
        [FieldOffset(0)] public PropVariantCountedPointer CountedPointer;
    }

    /// <summary>
    /// ABI-faithful PROPVARIANT header plus value union. Natural layout yields
    /// 16 bytes on x86 and 24 bytes on x64; the value union always starts at 8.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(2)] public ushort Reserved1;
        [FieldOffset(4)] public ushort Reserved2;
        [FieldOffset(6)] public ushort Reserved3;
        [FieldOffset(8)] public PropVariantValue Value;
        [FieldOffset(0)] public PropVariantDecimal DecimalValue;

        public IntPtr PointerValue => Value.Pointer;
    }

    internal delegate int PropertyValueGetter(ref PropertyKey key, out PropVariant value);
    internal delegate string? PropVariantStringConverter(IntPtr pointer);
    internal delegate int PropVariantClearer(ref PropVariant value);

    /// <summary>
    /// Gate I：Core Audio MMDevice 枚举（playback/capture endpoint + default 标记）。
    /// 只读枚举；不做音量/切换/测试。失败返回空集（Gate M）。
    /// </summary>
    public interface IAudioEndpointSource
    {
        IReadOnlyList<AudioInventoryMapper.EndpointDescriptor> GetEndpoints();
    }

    public sealed class CoreAudioEndpointSource : IAudioEndpointSource
    {
        private enum EDataFlow
        {
            eRender = 0,
            eCapture = 1,
        }

        private const int DeviceStateActive = 0x1;
        private const int ClsCtxInProcServer = 0x1;
        private static readonly Guid DeviceFriendlyNameKey =
            new("a45c254e-df1c-4efd-8020-67d146a850e0"); // pid 14

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private sealed class MMDeviceEnumeratorCom { }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport]
        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig]
            int GetCount(out int count);
            [PreserveSig]
            int Item(int index, out IMMDevice device);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iId, int clsCtx, IntPtr activationParams, out IntPtr instance);
            [PreserveSig]
            int OpenPropertyStore(int access, out IPropertyStore properties);
            [PreserveSig]
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig]
            int GetState(out int state);
        }

        [ComImport]
        [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig]
            int GetCount(out int count);
            [PreserveSig]
            int GetAt(int index, out PropertyKey key);
            [PreserveSig]
            int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig]
            int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig]
            int Commit();
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant variant);

        public IReadOnlyList<AudioInventoryMapper.EndpointDescriptor> GetEndpoints()
        {
            var results = new List<AudioInventoryMapper.EndpointDescriptor>();
            IMMDeviceEnumerator? enumerator = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorCom();
                var defaultIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectDefaultIds(enumerator, defaultIds);

                AppendFlow(enumerator, EDataFlow.eRender, results, defaultIds);
                AppendFlow(enumerator, EDataFlow.eCapture, results, defaultIds);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ExceptionLogWriter.Write(exception, "Inventory/CoreAudio");
            }
            finally
            {
                ReleaseComObject(enumerator);
            }

            return results;
        }

        private static void CollectDefaultIds(IMMDeviceEnumerator enumerator, HashSet<string> ids)
        {
            IMMDevice? render = null;
            try
            {
                if (HResultSucceeded(enumerator.GetDefaultAudioEndpoint(
                        (int)EDataFlow.eRender, 0, out render))
                    && render is not null)
                {
                    AddDefaultId(render, ids);
                }
            }
            finally
            {
                ReleaseComObject(render);
            }

            IMMDevice? capture = null;
            try
            {
                if (HResultSucceeded(enumerator.GetDefaultAudioEndpoint(
                        (int)EDataFlow.eCapture, 0, out capture))
                    && capture is not null)
                {
                    AddDefaultId(capture, ids);
                }
            }
            finally
            {
                ReleaseComObject(capture);
            }
        }

        private static void AddDefaultId(IMMDevice device, HashSet<string> ids)
        {
            try
            {
                if (HResultSucceeded(device.GetId(out var id)) && id is not null)
                {
                    ids.Add(id);
                }
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "Inventory/CoreAudio/default-id");
            }
        }

        private static void AppendFlow(
            IMMDeviceEnumerator enumerator,
            EDataFlow flow,
            List<AudioInventoryMapper.EndpointDescriptor> results,
            HashSet<string> defaultIds)
        {
            if (HResultFailed(enumerator.EnumAudioEndpoints((int)flow, DeviceStateActive, out var collection))
                || collection is null)
            {
                return;
            }

            try
            {
                if (HResultFailed(collection.GetCount(out var count)))
                {
                    return;
                }

                for (var index = 0; index < count; index++)
                {
                    if (HResultFailed(collection.Item(index, out var device)) || device is null)
                    {
                        continue;
                    }

                    try
                    {
                        if (HResultFailed(device.GetId(out var id)) || id is null)
                        {
                            continue;
                        }

                        device.GetState(out var state);
                        var friendlyName = ReadFriendlyName(device);
                        results.Add(new AudioInventoryMapper.EndpointDescriptor(
                            Id: id,
                            FriendlyName: friendlyName,
                            Direction: flow == EDataFlow.eRender
                                ? AudioEndpointDirection.Playback
                                : AudioEndpointDirection.Capture,
                            State: MapState(state),
                            IsDefault: defaultIds.Contains(id)));
                    }
                    catch (Exception exception)
                    {
                        // 单个 endpoint 异常不拖垮音频 inventory（Gate M）。
                        ExceptionLogWriter.Write(exception, "Inventory/CoreAudio/endpoint");
                    }
                    finally
                    {
                        ReleaseComObject(device);
                    }
                }
            }
            finally
            {
                ReleaseComObject(collection);
            }
        }

        private static string? ReadFriendlyName(IMMDevice device)
        {
            if (HResultFailed(device.OpenPropertyStore(0 /* STGM_READ */, out var properties))
                || properties is null)
            {
                return null;
            }

            try
            {
                var friendly = ReadProperty(properties, 14);
                return friendly ?? ReadProperty(properties, 2);
            }
            finally
            {
                ReleaseComObject(properties);
            }
        }

        private static string? ReadProperty(IPropertyStore properties, int propertyId)
        {
            return ReadPropertyValue(
                properties.GetValue,
                propertyId,
                Marshal.PtrToStringUni,
                ClearPropVariant);
        }

        internal static int ClearPropVariant(ref PropVariant value) =>
            PropVariantClear(ref value);

        internal static string? ReadPropertyValue(
            PropertyValueGetter getValue,
            int propertyId,
            PropVariantStringConverter convertString,
            PropVariantClearer clear)
        {
            ArgumentNullException.ThrowIfNull(getValue);
            ArgumentNullException.ThrowIfNull(convertString);
            ArgumentNullException.ThrowIfNull(clear);

            var key = new PropertyKey
            {
                FormatId = DeviceFriendlyNameKey,
                PropertyId = propertyId,
            };
            if (HResultFailed(getValue(ref key, out var variant)))
            {
                return null;
            }

            try
            {
                // VT_LPWSTR = 31
                return variant.VariantType == 31 && variant.PointerValue != IntPtr.Zero
                    ? HardwarePlaceholderFilter.Sanitize(convertString(variant.PointerValue))
                    : null;
            }
            finally
            {
                clear(ref variant);
            }
        }

        internal static int ExpectedPropVariantSize(int pointerSize) => pointerSize switch
        {
            4 => 16,
            8 => 24,
            _ => throw new ArgumentOutOfRangeException(nameof(pointerSize)),
        };

        private static bool HResultSucceeded(int hresult) => hresult >= 0;
        private static bool HResultFailed(int hresult) => hresult < 0;

        private static void ReleaseComObject(object? value)
        {
            if (value is not null && Marshal.IsComObject(value))
            {
                Marshal.ReleaseComObject(value);
            }
        }

        private static string? MapState(int state) => state switch
        {
            0x1 => "Active",
            0x2 => "Disabled",
            0x4 => "NotPresent",
            0x8 => "Unplugged",
            _ => null,
        };
    }
}

using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;

namespace LoadView
{
    // User-mode GPU temperature readers for AMD (ADL) and Intel (IGCL). Both talk to the
    // vendor's own driver library (no kernel driver, no admin, nothing bundled). Each holds a
    // context created once and reused; a missing DLL / unsupported call just yields "no value".
    // (NVIDIA is handled directly in TempProvider via nvml.dll.)
    //
    // Signatures/units verified against LibreHardwareMonitor's interop and the AMD ADL / Intel
    // IGCL headers. On x64 all calling conventions collapse to one, so the remaining risk is
    // struct layout — the readers are wrapped so even a corrupted-state exception degrades to
    // "no value" rather than crashing the app.

    // ======================================================================================
    //  AMD — atiadlxx.dll
    // ======================================================================================
    internal static class AtiAdl
    {
        private const string Dll = "atiadlxx.dll";

        public const int ADL_MAX_PATH = 256;
        public const int ATI_VENDOR_ID = 0x1002;
        public const int ADL_OK = 0;
        public const int ADL_PMLOG_MAX_SENSORS = 256;

        // PMLog sensor ids (values in whole degrees C).
        public const int ADL_PMLOG_TEMPERATURE_EDGE = 8;
        public const int ADL_PMLOG_TEMPERATURE_HOTSPOT = 27;

        // The ADL memory-allocation callback is __stdcall (default marshalling is correct).
        public delegate IntPtr ADL_Main_Memory_AllocDelegate(int size);

        // Kept alive for the process so the GC can't collect the thunk.
        public static readonly ADL_Main_Memory_AllocDelegate Main_Memory_Alloc =
            new ADL_Main_Memory_AllocDelegate(Marshal.AllocHGlobal);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ADL2_Main_Control_Create(
            ADL_Main_Memory_AllocDelegate callback, int connectedAdapters, ref IntPtr context);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ADL2_Main_Control_Destroy(IntPtr context);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr context, ref int numAdapters);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ADL2_Adapter_AdapterInfo_Get(IntPtr context, IntPtr adapterInfo, int size);

        // ADLTemperature.iTemperature is in millidegrees C. thermalControllerIndex = 0.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ADL2_Overdrive5_Temperature_Get(
            IntPtr context, int adapterIndex, int thermalControllerIndex, ref ADLTemperature temperature);

        // Modern cards (Vega20 / Navi / RDNA2-3). PMLog temperature values are whole degrees C.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ADL2_New_QueryPMLogData_Get(
            IntPtr context, int adapterIndex, ref ADLPMLogDataOutput data);

        [StructLayout(LayoutKind.Sequential)]
        public struct ADLTemperature
        {
            public int iSize;
            public int iTemperature; // millidegrees C
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ADLSingleSensorData
        {
            public int supported;
            public int value;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ADLPMLogDataOutput
        {
            public int size;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = ADL_PMLOG_MAX_SENSORS)]
            public ADLSingleSensorData[] sensors;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct ADLAdapterInfo
        {
            public int Size;
            public int AdapterIndex;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string UDID;
            public int BusNumber;
            public int DeviceNumber;
            public int FunctionNumber;
            public int VendorID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string AdapterName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string DisplayName;
            public int Present;
            public int Exist;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string DriverPath;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string DriverPathExt;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string PNPString;
            public int OSDisplayIndex;
        }

        // Marshal the adapter-info array (raw import takes an unmanaged buffer + byte size).
        public static int AdapterInfoGet(IntPtr context, ADLAdapterInfo[] info)
        {
            int elementSize = Marshal.SizeOf(typeof(ADLAdapterInfo));
            int size = info.Length * elementSize;
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                int result = ADL2_Adapter_AdapterInfo_Get(context, ptr, size);
                for (int i = 0; i < info.Length; i++)
                {
                    IntPtr elem = (IntPtr)((long)ptr + (i * elementSize));
                    info[i] = (ADLAdapterInfo)Marshal.PtrToStructure(elem, typeof(ADLAdapterInfo));
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
    }

    internal sealed class AmdGpuTemp : IDisposable
    {
        private bool _tried;
        private IntPtr _ctx = IntPtr.Zero;
        private bool _pmLogMissing;

        private bool Ensure()
        {
            if (_tried) return _ctx != IntPtr.Zero;
            _tried = true;
            try
            {
                IntPtr ctx = IntPtr.Zero;
                if (AtiAdl.ADL2_Main_Control_Create(AtiAdl.Main_Memory_Alloc, 1, ref ctx) == AtiAdl.ADL_OK)
                    _ctx = ctx;
            }
            catch (Exception ex) { Log.Write("ADL init (no AMD driver?)", ex); }
            return _ctx != IntPtr.Zero;
        }

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        public bool TryRead(out double celsius)
        {
            celsius = 0;
            if (!Ensure()) return false;
            try
            {
                int num = 0;
                if (AtiAdl.ADL2_Adapter_NumberOfAdapters_Get(_ctx, ref num) != AtiAdl.ADL_OK || num <= 0)
                    return false;

                AtiAdl.ADLAdapterInfo[] adapters = new AtiAdl.ADLAdapterInfo[num];
                if (AtiAdl.AdapterInfoGet(_ctx, adapters) != AtiAdl.ADL_OK) return false;

                double best = double.MinValue;
                for (int i = 0; i < num; i++)
                {
                    if (adapters[i].VendorID != AtiAdl.ATI_VENDOR_ID) continue;
                    int idx = adapters[i].AdapterIndex;
                    double t;
                    if ((TryPmLog(idx, out t) || TryOd5(idx, out t)) && t > best) best = t;
                }
                if (best > -50 && best < 150) { celsius = best; return true; }
            }
            catch (Exception ex) { Log.Write("ADL temp", ex); }
            return false;
        }

        private bool TryPmLog(int idx, out double celsius)
        {
            celsius = 0;
            if (_pmLogMissing) return false;
            try
            {
                AtiAdl.ADLPMLogDataOutput log = new AtiAdl.ADLPMLogDataOutput();
                if (AtiAdl.ADL2_New_QueryPMLogData_Get(_ctx, idx, ref log) != AtiAdl.ADL_OK) return false;
                if (log.sensors == null) return false;
                AtiAdl.ADLSingleSensorData edge = log.sensors[AtiAdl.ADL_PMLOG_TEMPERATURE_EDGE];
                if (edge.supported != 0 && edge.value > 0 && edge.value < 150) { celsius = edge.value; return true; }
                AtiAdl.ADLSingleSensorData hot = log.sensors[AtiAdl.ADL_PMLOG_TEMPERATURE_HOTSPOT];
                if (hot.supported != 0 && hot.value > 0 && hot.value < 150) { celsius = hot.value; return true; }
            }
            catch (EntryPointNotFoundException) { _pmLogMissing = true; }
            catch { }
            return false;
        }

        private bool TryOd5(int idx, out double celsius)
        {
            celsius = 0;
            try
            {
                AtiAdl.ADLTemperature t = new AtiAdl.ADLTemperature();
                t.iSize = Marshal.SizeOf(typeof(AtiAdl.ADLTemperature));
                if (AtiAdl.ADL2_Overdrive5_Temperature_Get(_ctx, idx, 0, ref t) != AtiAdl.ADL_OK) return false;
                double c = t.iTemperature / 1000.0;
                if (c > 0 && c < 150) { celsius = c; return true; }
            }
            catch { }
            return false;
        }

        public void Dispose()
        {
            if (_ctx != IntPtr.Zero)
            {
                try { AtiAdl.ADL2_Main_Control_Destroy(_ctx); } catch { }
                _ctx = IntPtr.Zero;
            }
        }
    }

    // ======================================================================================
    //  Intel — ControlLib.dll (IGCL)
    // ======================================================================================
    internal static class IntelGcl
    {
        private const string Dll = "ControlLib.dll";

        public const uint CTL_IMPL_VERSION = (1u << 16) | 1u; // major<<16 | minor
        public const int CTL_RESULT_SUCCESS = 0;
        public const uint CTL_INIT_FLAG_USE_LEVEL_ZERO = 1u << 0;

        // ctl_temp_sensors_t
        public const int CTL_TEMP_SENSORS_GLOBAL = 0;
        public const int CTL_TEMP_SENSORS_GPU = 1;

        public const int CTL_PSU_COUNT = 5;
        public const int CTL_FAN_COUNT = 5;

        public const int CTL_DATA_TYPE_INT32 = 4;
        public const int CTL_DATA_TYPE_UINT32 = 5;
        public const int CTL_DATA_TYPE_INT64 = 6;
        public const int CTL_DATA_TYPE_UINT64 = 7;
        public const int CTL_DATA_TYPE_FLOAT = 8;
        public const int CTL_DATA_TYPE_DOUBLE = 9;

        [StructLayout(LayoutKind.Sequential)]
        public struct ctl_application_id_t
        {
            public uint Data1;
            public ushort Data2;
            public ushort Data3;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] Data4;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ctl_init_args_t
        {
            public uint Size;
            public byte Version;
            public uint AppVersion;
            public uint flags;
            public uint SupportedVersion;
            public ctl_application_id_t ApplicationUID;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ctl_temp_properties_t
        {
            public uint Size;
            public byte Version;
            public int type;             // ctl_temp_sensors_t
            public double maxTemperature; // degrees C
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct ctl_data_value_t
        {
            [FieldOffset(0)] public int data32;
            [FieldOffset(0)] public uint datau32;
            [FieldOffset(0)] public long data64;
            [FieldOffset(0)] public ulong datau64;
            [FieldOffset(0)] public float datafloat;
            [FieldOffset(0)] public double datadouble;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ctl_oc_telemetry_item_t
        {
            public bool bSupported;   // marshalled as 4-byte BOOL (matches C++ bool + 3 pad)
            public int units;
            public int type;
            public ctl_data_value_t value;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ctl_psu_info_t
        {
            public bool bSupported;
            public int psuType;
            public ctl_oc_telemetry_item_t energyCounter;
            public ctl_oc_telemetry_item_t voltage;
        }

        // Only the leading fields up to gpuCurrentTemperature are relied upon (it sits before any
        // standalone-bool block, so its offset is exact); the tail is included for correct Size.
        [StructLayout(LayoutKind.Sequential)]
        public struct ctl_power_telemetry_t
        {
            public uint Size;
            public byte Version;
            public ctl_oc_telemetry_item_t timeStamp;
            public ctl_oc_telemetry_item_t gpuEnergyCounter;
            public ctl_oc_telemetry_item_t gpuVoltage;
            public ctl_oc_telemetry_item_t gpuCurrentClockFrequency;
            public ctl_oc_telemetry_item_t gpuCurrentTemperature;   // degrees C
            public ctl_oc_telemetry_item_t globalActivityCounter;
            public ctl_oc_telemetry_item_t renderComputeActivityCounter;
            public ctl_oc_telemetry_item_t mediaActivityCounter;
            public bool gpuPowerLimited;
            public bool gpuTemperatureLimited;
            public bool gpuCurrentLimited;
            public bool gpuVoltageLimited;
            public bool gpuUtilizationLimited;
            public ctl_oc_telemetry_item_t vramEnergyCounter;
            public ctl_oc_telemetry_item_t vramVoltage;
            public ctl_oc_telemetry_item_t vramCurrentClockFrequency;
            public ctl_oc_telemetry_item_t vramCurrentEffectiveFrequency;
            public ctl_oc_telemetry_item_t vramReadBandwidthCounter;
            public ctl_oc_telemetry_item_t vramWriteBandwidthCounter;
            public ctl_oc_telemetry_item_t vramCurrentTemperature;
            public bool vramPowerLimited;
            public bool vramTemperatureLimited;
            public bool vramCurrentLimited;
            public bool vramVoltageLimited;
            public bool vramUtilizationLimited;
            public ctl_oc_telemetry_item_t totalCardEnergyCounter;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = CTL_PSU_COUNT)]
            public ctl_psu_info_t[] psu;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = CTL_FAN_COUNT)]
            public ctl_oc_telemetry_item_t[] fanSpeed;
            public ctl_oc_telemetry_item_t gpuVrTemp;
            public ctl_oc_telemetry_item_t vramVrTemp;
            public ctl_oc_telemetry_item_t saVrTemp;
            public ctl_oc_telemetry_item_t gpuEffectiveClock;
            public ctl_oc_telemetry_item_t gpuOverVoltagePercent;
            public ctl_oc_telemetry_item_t gpuPowerPercent;
            public ctl_oc_telemetry_item_t gpuTemperaturePercent;
            public ctl_oc_telemetry_item_t vramReadBandwidth;
            public ctl_oc_telemetry_item_t vramWriteBandwidth;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ctlInit(ref ctl_init_args_t pInitDesc, ref IntPtr phAPIHandle);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ctlEnumerateDevices(IntPtr hAPIHandle, ref uint pCount, [In, Out] IntPtr[] phDevices);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ctlEnumTemperatureSensors(IntPtr hDAhandle, ref uint pCount, [In, Out] IntPtr[] phTemperature);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ctlTemperatureGetProperties(IntPtr hTemperature, ref ctl_temp_properties_t pProperties);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ctlTemperatureGetState(IntPtr hTemperature, ref double pTemperature);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ctlPowerTelemetryGet(IntPtr hDeviceHandle, ref ctl_power_telemetry_t pTelemetryInfo);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ctlClose(IntPtr hAPIHandle);

        public static double TelemetryValue(ctl_oc_telemetry_item_t item)
        {
            switch (item.type)
            {
                case CTL_DATA_TYPE_DOUBLE: return item.value.datadouble;
                case CTL_DATA_TYPE_FLOAT: return item.value.datafloat;
                case CTL_DATA_TYPE_UINT32: return item.value.datau32;
                case CTL_DATA_TYPE_INT32: return item.value.data32;
                case CTL_DATA_TYPE_UINT64: return item.value.datau64;
                case CTL_DATA_TYPE_INT64: return item.value.data64;
                default: return double.NaN;
            }
        }
    }

    internal sealed class IntelGpuTemp : IDisposable
    {
        private bool _tried;
        private bool _ready;
        private IntPtr _api = IntPtr.Zero;
        private IntPtr[] _devices;

        private bool Ensure()
        {
            if (_tried) return _ready;
            _tried = true;
            try
            {
                if (!Init(IntelGcl.CTL_INIT_FLAG_USE_LEVEL_ZERO) && !Init(0)) return false;
                uint n = 0;
                IntelGcl.ctlEnumerateDevices(_api, ref n, null);
                if (n == 0) return false;
                IntPtr[] devs = new IntPtr[n];
                if (IntelGcl.ctlEnumerateDevices(_api, ref n, devs) != IntelGcl.CTL_RESULT_SUCCESS) return false;
                _devices = devs;
                _ready = true;
            }
            catch (Exception ex) { Log.Write("IGCL init (no Intel driver?)", ex); _ready = false; }
            return _ready;
        }

        private bool Init(uint flags)
        {
            try
            {
                IntelGcl.ctl_init_args_t a = new IntelGcl.ctl_init_args_t();
                a.Size = (uint)Marshal.SizeOf(typeof(IntelGcl.ctl_init_args_t));
                a.Version = 0;
                a.AppVersion = IntelGcl.CTL_IMPL_VERSION;
                a.flags = flags;
                a.ApplicationUID = new IntelGcl.ctl_application_id_t();
                a.ApplicationUID.Data4 = new byte[8];
                IntPtr api = IntPtr.Zero;
                if (IntelGcl.ctlInit(ref a, ref api) == IntelGcl.CTL_RESULT_SUCCESS && api != IntPtr.Zero)
                { _api = api; return true; }
            }
            catch { }
            return false;
        }

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        public bool TryRead(out double celsius)
        {
            celsius = 0;
            if (!Ensure()) return false;
            try
            {
                double best = double.MinValue;
                for (int i = 0; i < _devices.Length; i++)
                {
                    IntPtr dev = _devices[i];
                    double t;
                    if ((TryB1(dev, out t) || TryB2(dev, out t)) && t > best) best = t;
                }
                if (best > -50 && best < 150) { celsius = best; return true; }
            }
            catch (Exception ex) { Log.Write("IGCL temp", ex); }
            return false;
        }

        // Dedicated temperature-sensor API (clean, layout-safe).
        private static bool TryB1(IntPtr dev, out double celsius)
        {
            celsius = 0;
            try
            {
                uint n = 0;
                if (IntelGcl.ctlEnumTemperatureSensors(dev, ref n, null) != IntelGcl.CTL_RESULT_SUCCESS || n == 0)
                    return false;
                IntPtr[] sensors = new IntPtr[n];
                if (IntelGcl.ctlEnumTemperatureSensors(dev, ref n, sensors) != IntelGcl.CTL_RESULT_SUCCESS)
                    return false;

                double best = double.MinValue;
                for (int i = 0; i < sensors.Length; i++)
                {
                    IntelGcl.ctl_temp_properties_t props = new IntelGcl.ctl_temp_properties_t();
                    props.Size = (uint)Marshal.SizeOf(typeof(IntelGcl.ctl_temp_properties_t));
                    props.Version = 0;
                    if (IntelGcl.ctlTemperatureGetProperties(sensors[i], ref props) != IntelGcl.CTL_RESULT_SUCCESS)
                        continue;
                    if (props.type != IntelGcl.CTL_TEMP_SENSORS_GPU && props.type != IntelGcl.CTL_TEMP_SENSORS_GLOBAL)
                        continue;
                    double c = 0.0;
                    if (IntelGcl.ctlTemperatureGetState(sensors[i], ref c) == IntelGcl.CTL_RESULT_SUCCESS
                        && c > 0 && c < 150 && c > best) best = c;
                }
                if (best > double.MinValue) { celsius = best; return true; }
            }
            catch (EntryPointNotFoundException) { }
            catch { }
            return false;
        }

        // Telemetry path (what LibreHardwareMonitor ships; proven on Arc).
        private static bool TryB2(IntPtr dev, out double celsius)
        {
            celsius = 0;
            try
            {
                IntelGcl.ctl_power_telemetry_t tel = new IntelGcl.ctl_power_telemetry_t();
                tel.Size = (uint)Marshal.SizeOf(typeof(IntelGcl.ctl_power_telemetry_t));
                tel.Version = 1;
                tel.psu = new IntelGcl.ctl_psu_info_t[IntelGcl.CTL_PSU_COUNT];
                tel.fanSpeed = new IntelGcl.ctl_oc_telemetry_item_t[IntelGcl.CTL_FAN_COUNT];
                if (IntelGcl.ctlPowerTelemetryGet(dev, ref tel) != IntelGcl.CTL_RESULT_SUCCESS) return false;
                if (tel.gpuCurrentTemperature.bSupported)
                {
                    double c = IntelGcl.TelemetryValue(tel.gpuCurrentTemperature);
                    if (c > 0 && c < 150) { celsius = c; return true; }
                }
            }
            catch (EntryPointNotFoundException) { }
            catch { }
            return false;
        }

        public void Dispose()
        {
            if (_api != IntPtr.Zero)
            {
                try { IntelGcl.ctlClose(_api); } catch { }
                _api = IntPtr.Zero;
            }
        }
    }
}

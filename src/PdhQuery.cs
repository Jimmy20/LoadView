using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LoadView
{
    // One sampled instance of a wildcard counter (e.g. a NIC, or a GPU engine instance).
    internal struct NamedValue
    {
        public string Name;
        public double Value;
    }

    // Thin wrapper over the Windows PDH API. It deliberately uses the *English* counter
    // API (PdhAddEnglishCounter), so counter paths work regardless of the OS display
    // language (this machine is Czech). Counters that don't exist on a given machine
    // simply return IntPtr.Zero from AddCounter, so callers can degrade gracefully.
    internal sealed class PdhQuery : IDisposable
    {
        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_MORE_DATA = 0x800007D2;
        private const uint ERROR_SUCCESS = 0;

        [StructLayout(LayoutKind.Explicit)]
        private struct PDH_FMT_COUNTERVALUE
        {
            [FieldOffset(0)] public uint CStatus;
            [FieldOffset(8)] public double doubleValue; // x64 union alignment
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE_ITEM
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string szName;
            public PDH_FMT_COUNTERVALUE FmtValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
        private static extern uint PdhOpenQuery(string szDataSource, IntPtr dwUserData, out IntPtr phQuery);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
        private static extern uint PdhAddEnglishCounter(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr hQuery);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr hQuery);

        [DllImport("pdh.dll")]
        private static extern uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, IntPtr lpdwType, out PDH_FMT_COUNTERVALUE pValue);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
        private static extern uint PdhGetFormattedCounterArray(IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, out uint lpdwItemCount, IntPtr itemBuffer);

        private IntPtr _query;

        // Reused across ticks to avoid per-call allocations (GPU/network are read every second).
        private IntPtr _arrBuf = IntPtr.Zero;
        private int _arrBufSize;
        private int _itemSize = -1;
        private readonly List<NamedValue> _arrList = new List<NamedValue>();

        public PdhQuery()
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != ERROR_SUCCESS)
                _query = IntPtr.Zero;
        }

        public bool IsValid { get { return _query != IntPtr.Zero; } }

        // Returns a counter handle, or IntPtr.Zero if the counter isn't available here.
        public IntPtr AddCounter(string path)
        {
            if (_query == IntPtr.Zero) return IntPtr.Zero;
            IntPtr h;
            if (PdhAddEnglishCounter(_query, path, IntPtr.Zero, out h) != ERROR_SUCCESS)
                return IntPtr.Zero;
            return h;
        }

        public bool Collect()
        {
            if (_query == IntPtr.Zero) return false;
            return PdhCollectQueryData(_query) == ERROR_SUCCESS;
        }

        public double ReadDouble(IntPtr counter)
        {
            if (counter == IntPtr.Zero) return 0;
            PDH_FMT_COUNTERVALUE val;
            if (PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE, IntPtr.Zero, out val) != ERROR_SUCCESS)
                return 0;
            double d = val.doubleValue;
            if (double.IsNaN(d) || double.IsInfinity(d)) return 0;
            return d;
        }

        // NOTE: returns a reused list backed by a reused buffer to keep GC pressure low.
        // The caller must consume the result before the next ReadArray call (the metrics
        // sampler reads each wildcard counter and aggregates it immediately).
        public List<NamedValue> ReadArray(IntPtr counter)
        {
            _arrList.Clear();
            if (counter == IntPtr.Zero) return _arrList;
            if (_itemSize < 0) _itemSize = Marshal.SizeOf(typeof(PDH_FMT_COUNTERVALUE_ITEM));

            uint size = (uint)_arrBufSize;
            uint count;
            uint r = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out count, _arrBuf);
            if (r == PDH_MORE_DATA)
            {
                if (_arrBuf != IntPtr.Zero) Marshal.FreeHGlobal(_arrBuf);
                _arrBuf = Marshal.AllocHGlobal((int)size);
                _arrBufSize = (int)size;
                r = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out count, _arrBuf);
            }
            if (r != ERROR_SUCCESS) return _arrList;

            for (int i = 0; i < count; i++)
            {
                IntPtr p = new IntPtr(_arrBuf.ToInt64() + (long)i * _itemSize);
                PDH_FMT_COUNTERVALUE_ITEM item = (PDH_FMT_COUNTERVALUE_ITEM)Marshal.PtrToStructure(p, typeof(PDH_FMT_COUNTERVALUE_ITEM));
                double d = item.FmtValue.doubleValue;
                if (double.IsNaN(d) || double.IsInfinity(d)) d = 0;

                NamedValue nv;
                nv.Name = item.szName == null ? "" : item.szName;
                nv.Value = d;
                _arrList.Add(nv);
            }
            return _arrList;
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace LoadView
{
    // Disk temperatures, and deliberately **without** the elevated helper.
    //
    // The usual way to read a drive's temperature is SMART, which needs a handle opened for read
    // access and therefore administrator rights. This does not: IOCTL_STORAGE_QUERY_PROPERTY is
    // declared FILE_ANY_ACCESS, so a handle opened with *zero* desired access is enough, and that
    // succeeds for a standard user. Measured on this machine: the query returned
    // "Version=40 Size=56 Crit=85 Warn=83 InfoCount=2" with both entries at 46 C, and the NVMe
    // SMART/health log read through the same IOCTL agreed to the degree.
    //
    // Two details worth keeping, both learned the hard way:
    //  * The entry array does NOT start at offset 16 the way the documented field list implies — on
    //    this machine it starts at 24. The header length is therefore derived as
    //    Size - InfoCount * sizeof(entry) rather than hard-coded.
    //  * Model and serial come from the same IOCTL (StorageDeviceProperty), so no WMI is involved
    //    and enumeration costs a handful of handle opens.
    internal sealed class DiskTemp
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string path, uint access, uint share, IntPtr sa,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, int inSize,
            byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
        private const int StorageDeviceProperty = 0;
        private const int StorageDeviceTemperatureProperty = 52;
        private const int TempInfoSize = 16;          // sizeof(STORAGE_TEMPERATURE_INFO)
        private const int MaxDrives = 16;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_SHARE_RW = 3;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        private sealed class Disk
        {
            public int Index;
            public string Id;
            public string Label;
            public string Detail;
        }

        private List<Disk> _disks;
        private readonly List<SensorReading> _readings = new List<SensorReading>();

        // Enumerate once and then only occasionally: drives rarely come and go, and each probe is a
        // handle open. The caller decides how often (see TempProvider.Loop).
        public void Rescan()
        {
            List<Disk> found = new List<Disk>();
            Dictionary<int, string> letters = DriveLetters();

            for (int i = 0; i < MaxDrives; i++)
            {
                IntPtr h = Open(@"\\.\PhysicalDrive" + i.ToString(CultureInfo.InvariantCulture));
                if (h == InvalidHandle) continue;
                try
                {
                    double t;
                    if (!TryTemperature(h, out t)) continue;   // no sensor: not worth a tile

                    string model, serial;
                    Describe(h, out model, out serial);

                    Disk d = new Disk();
                    d.Index = i;
                    d.Id = "disk:" + (serial.Length > 0 ? serial : Sanitise(model) + ":" + i);
                    d.Detail = model.Length > 0 ? model : "PhysicalDrive" + i;
                    string letter;
                    d.Label = letters.TryGetValue(i, out letter) ? letter : "Disk " + i;
                    found.Add(d);
                }
                finally { CloseHandle(h); }
            }
            _disks = found;
        }

        // Current readings for the disks found by the last Rescan.
        public SensorReading[] Read()
        {
            if (_disks == null) Rescan();
            _readings.Clear();
            for (int i = 0; i < _disks.Count; i++)
            {
                Disk d = _disks[i];
                IntPtr h = Open(@"\\.\PhysicalDrive" + d.Index.ToString(CultureInfo.InvariantCulture));
                if (h == InvalidHandle) continue;
                try
                {
                    double t;
                    if (!TryTemperature(h, out t)) continue;
                    SensorReading r;
                    r.Id = d.Id; r.Label = d.Label; r.Detail = d.Detail;
                    r.Kind = SensorKind.Temperature; r.Value = t;
                    _readings.Add(r);
                }
                finally { CloseHandle(h); }
            }
            return _readings.ToArray();
        }

        private static IntPtr Open(string path)
        {
            // access = 0: enough for a query IOCTL, and the reason no elevation is needed.
            try { return CreateFileW(path, 0, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero); }
            catch { return InvalidHandle; }
        }

        private static bool TryTemperature(IntPtr h, out double celsius)
        {
            celsius = 0;
            try
            {
                byte[] inb = new byte[12];
                WriteInt(inb, 0, StorageDeviceTemperatureProperty);
                WriteInt(inb, 4, 0);                      // PropertyStandardQuery
                byte[] o = new byte[512];
                int ret;
                if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, inb, inb.Length, o, o.Length,
                        out ret, IntPtr.Zero) || ret < 16)
                    return false;

                int size = (int)ReadUInt(o, 4);
                int count = ReadUShort(o, 12);
                if (count < 1 || size < 16 || size > ret) return false;

                // The array's offset is whatever is left once the entries are accounted for; the
                // documented 16-byte header is not what the driver actually returns.
                int header = size - count * TempInfoSize;
                if (header < 16 || header > 64) return false;

                double best = double.MinValue;
                for (int i = 0; i < count; i++)
                {
                    int at = header + i * TempInfoSize;
                    if (at + 4 > ret) break;
                    double t = ReadShort(o, at + 2);
                    if (t > 0 && t < 120 && t > best) best = t;   // 0 = "this sensor has nothing"
                }
                if (best == double.MinValue) return false;
                celsius = best;
                return true;
            }
            catch { return false; }
        }

        // Model + serial from STORAGE_DEVICE_DESCRIPTOR: offsets into the same buffer, ASCII, NUL
        // terminated. Both may be absent, which the caller handles.
        private static void Describe(IntPtr h, out string model, out string serial)
        {
            model = ""; serial = "";
            try
            {
                byte[] inb = new byte[12];
                WriteInt(inb, 0, StorageDeviceProperty);
                WriteInt(inb, 4, 0);
                byte[] o = new byte[1024];
                int ret;
                if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, inb, inb.Length, o, o.Length,
                        out ret, IntPtr.Zero) || ret < 36)
                    return;

                string vendor = AsciiAt(o, (int)ReadUInt(o, 12), ret);
                string product = AsciiAt(o, (int)ReadUInt(o, 16), ret);
                serial = Sanitise(AsciiAt(o, (int)ReadUInt(o, 24), ret));
                model = (vendor + " " + product).Trim();
            }
            catch { }
        }

        // Physical drive number -> the drive letters living on it, so a tile can say "C:" instead of
        // "Disk 0". Uses IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, which also needs no elevation.
        private static Dictionary<int, string> DriveLetters()
        {
            Dictionary<int, string> map = new Dictionary<int, string>();
            try
            {
                foreach (System.IO.DriveInfo di in System.IO.DriveInfo.GetDrives())
                {
                    if (di.DriveType != System.IO.DriveType.Fixed) continue;
                    string root = di.Name.TrimEnd('\\');           // "C:"
                    IntPtr h = Open(@"\\.\" + root);
                    if (h == InvalidHandle) continue;
                    try
                    {
                        byte[] o = new byte[1024];
                        int ret;
                        if (!DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0,
                                o, o.Length, out ret, IntPtr.Zero) || ret < 12)
                            continue;
                        int extents = (int)ReadUInt(o, 0);
                        if (extents < 1) continue;
                        int disk = (int)ReadUInt(o, 8);            // first extent's DiskNumber
                        string cur;
                        map[disk] = map.TryGetValue(disk, out cur) ? cur + " " + root : root;
                    }
                    finally { CloseHandle(h); }
                }
            }
            catch { }
            return map;
        }

        private static string AsciiAt(byte[] b, int offset, int limit)
        {
            if (offset <= 0 || offset >= limit) return "";
            int end = offset;
            while (end < limit && b[end] != 0) end++;
            try { return Encoding.ASCII.GetString(b, offset, end - offset).Trim(); }
            catch { return ""; }
        }

        // Serials arrive padded, spaced and occasionally with punctuation; keep it to something that
        // is safe in an ini value and stable across reboots.
        private static string Sanitise(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private static void WriteInt(byte[] b, int at, int v)
        {
            b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24);
        }
        private static uint ReadUInt(byte[] b, int at)
        {
            return (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));
        }
        private static int ReadUShort(byte[] b, int at) { return b[at] | (b[at + 1] << 8); }
        private static int ReadShort(byte[] b, int at)
        {
            int v = b[at] | (b[at + 1] << 8);
            return v > 32767 ? v - 65536 : v;
        }
    }
}

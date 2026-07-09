using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace LoadView
{
    // Background provider for things that can be slow or block: drive enumeration
    // (incl. mapped network drives), the internal LAN IP, and the external/public IP.
    // Kept off the UI thread so a disconnected share or a slow web lookup never stalls
    // the overlay.
    internal sealed class SystemInfoProvider : IDisposable
    {
        private const double GiB = 1024.0 * 1024.0 * 1024.0;

        private readonly object _lock = new object();
        private DriveLine[] _drives = new DriveLine[0];
        private string _internalIp = "";
        private string _externalIp = "";

        public volatile bool ExternalIpEnabled = true;
        public volatile int LanRefreshSec = 10;
        public volatile int WanRefreshSec = 600;

        private readonly Thread _thread;
        private volatile bool _stop;
        private DateTime _lastExtAttempt = DateTime.MinValue;

        public SystemInfoProvider()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "LoadView.SysInfo";
            _thread.Start();
        }

        public DriveLine[] Drives() { lock (_lock) { return _drives; } }
        public string InternalIp() { lock (_lock) { return _internalIp; } }
        public string ExternalIp() { lock (_lock) { return _externalIp; } }

        private void Loop()
        {
            long tick = 0;
            DateTime lastLan = DateTime.MinValue;
            while (!_stop)
            {
                try { if (tick % 3 == 0) RefreshDrives(); } catch { }
                try
                {
                    int lan = LanRefreshSec; if (lan < 1) lan = 1;
                    if ((DateTime.UtcNow - lastLan).TotalSeconds >= lan)
                    { RefreshInternalIp(); lastLan = DateTime.UtcNow; }
                }
                catch { }
                try { RefreshExternalIp(); } catch { }
                tick++;
                for (int i = 0; i < 10 && !_stop; i++) Thread.Sleep(100); // ~1 s base
            }
        }

        private void RefreshDrives()
        {
            List<DriveLine> list = new List<DriveLine>();
            foreach (DriveInfo di in DriveInfo.GetDrives())
            {
                try
                {
                    if (!di.IsReady) continue;
                    if (di.DriveType != DriveType.Fixed &&
                        di.DriveType != DriveType.Removable &&
                        di.DriveType != DriveType.Network) continue;

                    double total = di.TotalSize / GiB;
                    if (total <= 0) continue;
                    double free = di.TotalFreeSpace / GiB;
                    double used = total - free;

                    DriveLine dl;
                    dl.Label = di.Name.TrimEnd('\\');
                    dl.UsedGB = used;
                    dl.TotalGB = total;
                    dl.FreeGB = free;
                    dl.Pct = total > 0 ? 100.0 * used / total : 0;
                    list.Add(dl);
                }
                catch { }
            }
            DriveLine[] arr = list.ToArray();
            lock (_lock) { _drives = arr; }
        }

        private void RefreshInternalIp()
        {
            string best = "";
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    IPInterfaceProperties props = ni.GetIPProperties();
                    bool hasGateway = false;
                    foreach (GatewayIPAddressInformation gw in props.GatewayAddresses)
                        if (gw.Address != null && gw.Address.AddressFamily == AddressFamily.InterNetwork)
                        { hasGateway = true; break; }

                    foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(ua.Address)) continue;
                        string ip = ua.Address.ToString();
                        if (hasGateway) { best = ip; break; }   // prefer an adapter with a gateway
                        if (best.Length == 0) best = ip;
                    }
                    if (hasGateway && best.Length > 0) break;
                }
            }
            catch { }
            lock (_lock) { _internalIp = best.Length > 0 ? best : "—"; }
        }

        private void RefreshExternalIp()
        {
            if (!ExternalIpEnabled) { lock (_lock) { _externalIp = ""; } return; }

            string cur; lock (_lock) { cur = _externalIp; }
            bool have = cur.Length > 0 && cur != "—";
            double since = (DateTime.UtcNow - _lastExtAttempt).TotalSeconds;
            int wan = WanRefreshSec; if (wan < 5) wan = 5;
            int retry = wan < 30 ? wan : 30;
            // refresh every WanRefreshSec once known; retry sooner until we have one
            if (have && since < wan) return;
            if (!have && since < retry) return;

            _lastExtAttempt = DateTime.UtcNow;
            string ip = "—";
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://api.ipify.org");
                req.Timeout = 5000;
                req.UserAgent = "LoadView";
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                {
                    string txt = sr.ReadToEnd().Trim();
                    if (txt.Length > 0 && txt.Length <= 45) ip = txt;
                }
            }
            catch { }
            lock (_lock) { _externalIp = ip; }
        }

        public void Dispose()
        {
            _stop = true;
            try { if (_thread != null) _thread.Join(1500); } catch { }
        }
    }
}

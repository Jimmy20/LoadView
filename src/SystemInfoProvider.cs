using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
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
        private string _wanCountry = "";
        private string _wanCc = "";

        public volatile bool ExternalIpEnabled = true;
        public volatile int LanRefreshSec = 10;
        public volatile int WanRefreshSec = 600;
        public volatile bool GeoEnabled = false;   // resolve the country of the WAN IP
        public volatile bool FlagEnabled = false;  // also download the country flag image

        private readonly Thread _thread;
        private volatile bool _stop;
        private DateTime _lastExtAttempt = DateTime.MinValue;
        private string _lastGeoIp = "";
        private DateTime _lastFlagAttempt = DateTime.MinValue;

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
        public string WanCountry() { lock (_lock) { return _wanCountry; } }
        public string WanCc() { lock (_lock) { return _wanCc; } }

        // Force an immediate WAN IP + geo refresh on the next loop tick (~1 s).
        public void RefreshWanNow()
        {
            lock (_lock) { _lastExtAttempt = DateTime.MinValue; _lastGeoIp = ""; _lastFlagAttempt = DateTime.MinValue; }
        }

        // Local path where a country flag PNG is cached (downloaded on demand). cc is ISO-2, lowercase.
        public static string FlagPath(string cc)
        {
            if (string.IsNullOrEmpty(cc)) return null;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LoadView", "flags", cc + ".png");
        }

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
                try { RefreshWanGeo(); } catch { }
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

            string cur; DateTime last;
            lock (_lock) { cur = _externalIp; last = _lastExtAttempt; }
            bool have = cur.Length > 0 && cur != "—";
            double since = (DateTime.UtcNow - last).TotalSeconds;
            int wan = WanRefreshSec; if (wan < 5) wan = 5;
            int retry = wan < 30 ? wan : 30;
            // refresh every WanRefreshSec once known; retry sooner until we have one
            if (have && since < wan) return;
            if (!have && since < retry) return;

            lock (_lock) { _lastExtAttempt = DateTime.UtcNow; }
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

        // Resolve the WAN IP's country (and optionally download its flag). Runs only when enabled and
        // only when the IP has changed since the last lookup — the country rarely changes.
        private void RefreshWanGeo()
        {
            if (!GeoEnabled)
            {
                lock (_lock) { _wanCountry = ""; _wanCc = ""; _lastGeoIp = ""; }
                return;
            }

            string ip, lastGeo, cc;
            lock (_lock) { ip = _externalIp; lastGeo = _lastGeoIp; cc = _wanCc; }
            if (ip.Length == 0 || ip == "—") return;

            // Resolve the country only when the IP changed (it rarely changes).
            if (ip != lastGeo)
            {
                string country = "";
                cc = "";
                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://ipwho.is/" + ip);
                    req.Timeout = 5000;
                    req.UserAgent = "LoadView";
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                    {
                        string json = sr.ReadToEnd();
                        country = Extract(json, "\"country\"\\s*:\\s*\"([^\"]*)\"");
                        cc = Extract(json, "\"country_code\"\\s*:\\s*\"([^\"]*)\"").ToLowerInvariant();
                    }
                }
                catch { }
                lock (_lock) { _wanCountry = country; _wanCc = cc; _lastGeoIp = ip; }
            }

            // Ensure the flag image exists whenever it's enabled — independent of the geo gate above, so
            // it also downloads when the flag is switched on after the country was already resolved (or a
            // previous download failed). Throttled so a failing flagcdn isn't hammered.
            if (FlagEnabled && cc != null && cc.Length == 2)
            {
                string fp = FlagPath(cc);
                if (fp != null && !File.Exists(fp) && (DateTime.UtcNow - _lastFlagAttempt).TotalSeconds >= 8)
                {
                    _lastFlagAttempt = DateTime.UtcNow;
                    try { EnsureFlag(cc); } catch (Exception ex) { Log.Write("flag download failed (" + cc + ")", ex); }
                }
            }
        }

        private static string Extract(string s, string pattern)
        {
            try { Match m = Regex.Match(s, pattern); return m.Success ? m.Groups[1].Value : ""; }
            catch { return ""; }
        }

        // Download the small country flag PNG once and cache it locally.
        private static void EnsureFlag(string cc)
        {
            string path = FlagPath(cc);
            if (path == null || File.Exists(path)) return;
            string dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://flagcdn.com/w40/" + cc + ".png");
            req.Timeout = 5000;
            req.UserAgent = "LoadView";
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream rs = resp.GetResponseStream())
            using (FileStream fs = File.Create(path))
                rs.CopyTo(fs);
        }

        public void Dispose()
        {
            _stop = true;
            try { if (_thread != null) _thread.Join(1500); } catch { }
        }
    }
}

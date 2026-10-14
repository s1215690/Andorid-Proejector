// 自帶 mDNS (DNS-SD) 查詢器 —— 唔靠 adb 內建 mDNS（本機 adb 嘅 mDNS 揀錯網卡，永遠搵唔到）
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace AndroidCast
{
    internal class MdnsRec
    {
        public string Name;
        public int Type;
        public string PtrTarget;
        public string SrvTarget;
        public int SrvPort;
        public string Addr;
    }

    internal static class MiniMdns
    {
        private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
        private const int Mport = 5353;

        public static List<IPAddress> LocalAddresses()
        {
            List<IPAddress> list = new List<IPAddress>();
            try
            {
                NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                foreach (NetworkInterface ni in nics)
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string s = ua.Address.ToString();
                        if (s.StartsWith("169.254.")) continue;
                        if (!list.Contains(ua.Address)) list.Add(ua.Address);
                    }
                }
            }
            catch { }
            if (list.Count == 0) list.Add(IPAddress.Loopback);
            return list;
        }

        private static byte[] EncName(string name)
        {
            List<byte> b = new List<byte>();
            string[] parts = name.Split('.');
            foreach (string p in parts)
            {
                if (p.Length == 0) continue;
                b.Add((byte)p.Length);
                b.AddRange(Encoding.ASCII.GetBytes(p));
            }
            b.Add(0);
            return b.ToArray();
        }

        private static byte[] BuildQuery(string[] types)
        {
            List<byte> b = new List<byte>();
            b.AddRange(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
            foreach (string t in types)
            {
                b.AddRange(EncName(t));
                b.AddRange(new byte[] { 0, 12, 0, 1 }); // PTR IN
            }
            // 題目數要正確
            b[5] = (byte)types.Length;
            return b.ToArray();
        }

        private static string ReadName(byte[] d, ref int off)
        {
            List<string> labels = new List<string>();
            int hops = 0;
            int save = -1;
            while (true)
            {
                if (off >= d.Length) break;
                byte len = d[off];
                if (len == 0) { off++; break; }
                if (len >= 0xC0)
                {
                    if (off + 1 >= d.Length) break;
                    int ptr = ((len & 0x3F) << 8) | d[off + 1];
                    if (save < 0) save = off + 2;
                    off = ptr;
                    hops++;
                    if (hops > 8) break;
                    continue;
                }
                if (off + 1 + len > d.Length) break;
                labels.Add(Encoding.ASCII.GetString(d, off + 1, len));
                off += 1 + len;
            }
            if (save >= 0) off = save;
            return string.Join(".", labels.ToArray());
        }

        private static List<MdnsRec> Parse(byte[] d, int len, string fromIp, out int recCount)
        {
            List<MdnsRec> recs = new List<MdnsRec>();
            recCount = 0;
            if (len < 12) return recs;
            int qd = (d[4] << 8) | d[5];
            int an = (d[6] << 8) | d[7];
            int ns = (d[8] << 8) | d[9];
            int ar = (d[10] << 8) | d[11];
            int off = 12;
            for (int i = 0; i < qd && off < len; i++) { ReadName(d, ref off); off += 4; }
            int total = an + ns + ar;
            for (int i = 0; i < total && off + 10 <= len; i++)
            {
                MdnsRec r = new MdnsRec();
                r.Name = ReadName(d, ref off);
                if (off + 10 > len) break;
                int type = (d[off] << 8) | d[off + 1];
                int rdlen = (d[off + 8] << 8) | d[off + 9];
                off += 10;
                if (off + rdlen > len) break;
                r.Type = type;
                switch (type)
                {
                    case 12: // PTR
                        r.PtrTarget = ReadName(d, ref off);
                        break;
                    case 33: // SRV
                        if (rdlen >= 6)
                        {
                            r.SrvPort = (d[off + 4] << 8) | d[off + 5];
                            int tmp = off + 6;
                            r.SrvTarget = ReadName(d, ref tmp);
                        }
                        off += rdlen;
                        break;
                    case 1: // A
                        if (rdlen == 4) r.Addr = d[off] + "." + d[off + 1] + "." + d[off + 2] + "." + d[off + 3];
                        off += rdlen;
                        break;
                    default:
                        off += rdlen;
                        break;
                }
                recs.Add(r);
                recCount++;
            }
            return recs;
        }

        public static List<Svc> Browse(string[] types, int millis)
        {
            List<Svc> outList = new List<Svc>();
            List<IPAddress> ifs = LocalAddresses();
            Socket sock = null;
            try
            {
                sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                sock.Bind(new IPEndPoint(IPAddress.Any, Mport));
                foreach (IPAddress a in ifs)
                {
                    try { sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, a)); }
                    catch { }
                }
                byte[] q = BuildQuery(types);
                foreach (IPAddress a in ifs)
                {
                    bool sent = false;
                    try
                    {
                        sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, a.GetAddressBytes());
                        sock.SendTo(q, new IPEndPoint(Group, Mport));
                        sent = true;
                    }
                    catch { }
                    if (!sent)
                    {
                        try
                        {
                            byte[] rev = a.GetAddressBytes();
                            Array.Reverse(rev);
                            sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, rev);
                            sock.SendTo(q, new IPEndPoint(Group, Mport));
                        }
                        catch { }
                    }
                }

                List<MdnsRec> all = new List<MdnsRec>();
                Dictionary<string, string> senders = new Dictionary<string, string>();
                DateTime end = DateTime.Now.AddMilliseconds(millis);
                byte[] buf = new byte[9000];
                while (DateTime.Now < end)
                {
                    int left = (int)(end - DateTime.Now).TotalMilliseconds;
                    if (left <= 0) break;
                    sock.ReceiveTimeout = left;
                    try
                    {
                        EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                        int n = sock.ReceiveFrom(buf, ref ep);
                        string from = ((IPEndPoint)ep).Address.ToString();
                        bool mine = false;
                        foreach (IPAddress a in ifs) if (a.ToString() == from) mine = true;
                        if (mine) continue;
                        int rc;
                        List<MdnsRec> recs = Parse(buf, n, from, out rc);
                        foreach (MdnsRec r in recs) { all.Add(r); senders[r.Name] = from; }
                    }
                    catch (SocketException) { break; }
                    catch (Exception) { break; }
                }

                // 砌出服務：用 SRV 記錄 + A 記錄（A 缺失就用發送者 IP）
                Dictionary<string, MdnsRec> srv = new Dictionary<string, MdnsRec>();
                Dictionary<string, string> addrs = new Dictionary<string, string>();
                foreach (MdnsRec r in all)
                {
                    if (r.Type == 33) srv[r.Name.ToLower()] = r;
                    if (r.Type == 1 && r.Addr != null) addrs[r.Name.ToLower()] = r.Addr;
                }
                foreach (string key in srv.Keys)
                {
                    MdnsRec r = srv[key];
                    string ip = null;
                    if (r.SrvTarget != null)
                    {
                        string t = r.SrvTarget.ToLower();
                        if (addrs.ContainsKey(t)) ip = addrs[t];
                        else
                        {
                            string t2 = t + ".";
                            if (addrs.ContainsKey(t2)) ip = addrs[t2];
                        }
                    }
                    if (ip == null && senders.ContainsKey(r.Name)) ip = senders[r.Name];
                    if (ip == null) continue;
                    Svc s = new Svc();
                    s.Name = r.Name.Split('.')[0];
                    s.Type = "";
                    foreach (string t in types) if (key.IndexOf(t) >= 0) s.Type = t.TrimEnd('.');
                    if (s.Type == "") s.Type = key;
                    s.Addr = ip;
                    s.Port = r.SrvPort;
                    outList.Add(s);
                }
            }
            catch { }
            finally { if (sock != null) { try { sock.Close(); } catch { } } }

            // 去重
            List<Svc> fin = new List<Svc>();
            foreach (Svc s in outList)
            {
                bool dup = false;
                foreach (Svc f in fin) if (f.End == s.End && f.Name == s.Name) dup = true;
                if (!dup) fin.Add(s);
            }
            return fin;
        }
    }
}

// 安卓無線投屏 App — scrcpy + adb mDNS 自動發現 / QR 配對
// 編譯：csc /target:winexe /out:無線投屏.exe /win32icon:icon.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll app.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AndroidCast
{
    // ============================================================ QR 編碼器
    // 只支援 byte mode、ECC L、version 1..10（本用途 payload 約 45 字元）
    internal class QrResult
    {
        public int Version;
        public int Mask;
        public int Size;
        public bool[,] Modules; // [x=col, y=row]

        public string Dump()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("VERSION " + Version + " MASK " + Mask + " SIZE " + Size);
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++) sb.Append(Modules[x, y] ? '1' : '0');
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }

    internal static class Qr
    {
        public static string LastCw = "";
        public static string Hex(byte[] b) { StringBuilder s = new StringBuilder(); for (int i = 0; i < b.Length; i++) s.Append(b[i].ToString("x2")); return s.ToString(); }
        private static readonly byte[] EXP = new byte[512];
        private static readonly byte[] LOG = new byte[256];

        static Qr()
        {
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                EXP[i] = (byte)x;
                LOG[x] = (byte)i;
                x <<= 1;
                if ((x & 0x100) != 0) x ^= 0x11D;
            }
            for (int i = 255; i < 512; i++) EXP[i] = EXP[i - 255];
        }

        private static byte Mul(byte a, byte b)
        {
            if (a == 0 || b == 0) return 0;
            return EXP[LOG[a] + LOG[b]];
        }

        // version 1..10, ECC L: { ecPerBlock, 組1塊數, 組1每塊data, 組2塊數, 組2每塊data }
        private static readonly int[][] TBL = new int[][]
        {
            null,
            new int[] { 7, 1, 19, 0, 0 },
            new int[] { 10, 1, 34, 0, 0 },
            new int[] { 15, 1, 55, 0, 0 },
            new int[] { 20, 1, 80, 0, 0 },
            new int[] { 26, 1, 108, 0, 0 },
            new int[] { 18, 2, 68, 0, 0 },
            new int[] { 20, 2, 78, 0, 0 },
            new int[] { 24, 2, 97, 0, 0 },
            new int[] { 30, 2, 116, 0, 0 },
            new int[] { 18, 2, 68, 2, 69 }
        };

        private static readonly int[][] ALIGN = new int[][]
        {
            null, new int[0], new int[] { 6, 18 }, new int[] { 6, 22 }, new int[] { 6, 26 },
            new int[] { 6, 30 }, new int[] { 6, 34 }, new int[] { 6, 22, 38 }, new int[] { 6, 24, 42 },
            new int[] { 6, 26, 46 }, new int[] { 6, 28, 50 }
        };

        public static QrResult Encode(string text, int forcedMask)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            int version = -1;
            for (int v = 1; v <= 10; v++)
            {
                int dataCw = TBL[v][1] * TBL[v][2] + TBL[v][3] * TBL[v][4];
                int need = 4 + (v <= 9 ? 8 : 16) + data.Length * 8;
                if (need <= dataCw * 8) { version = v; break; }
            }
            if (version < 0) throw new Exception("內容太長，QR 編碼器只支援 version 1..10");

            int totalDataCw = TBL[version][1] * TBL[version][2] + TBL[version][3] * TBL[version][4];

            // --- bit stream ---
            List<bool> bits = new List<bool>();
            Append(bits, 4, 4);
            Append(bits, data.Length, version <= 9 ? 8 : 16);
            for (int i = 0; i < data.Length; i++) Append(bits, data[i], 8);
            int cap = totalDataCw * 8;
            int term = Math.Min(4, cap - bits.Count);
            for (int i = 0; i < term; i++) bits.Add(false);
            while (bits.Count % 8 != 0) bits.Add(false);

            byte[] dcw = new byte[totalDataCw];
            int nb = bits.Count / 8;
            for (int i = 0; i < nb; i++)
            {
                int v = 0;
                for (int j = 0; j < 8; j++) if (bits[i * 8 + j]) v |= 1 << (7 - j);
                dcw[i] = (byte)v;
            }
            for (int i = nb, pad = 0; i < totalDataCw; i++, pad++) dcw[i] = (byte)(pad % 2 == 0 ? 0xEC : 0x11);

            // --- RS 分塊 + 交錯 ---
            int ecPer = TBL[version][0];
            List<byte[]> blocks = new List<byte[]>();
            List<byte[]> ecbs = new List<byte[]>();
            int pos = 0;
            for (int g = 0; g < 2; g++)
            {
                int nblk = TBL[version][g * 2 + 1];
                int ndata = TBL[version][g * 2 + 2];
                for (int b = 0; b < nblk; b++)
                {
                    byte[] blk = new byte[ndata];
                    Array.Copy(dcw, pos, blk, 0, ndata);
                    pos += ndata;
                    blocks.Add(blk);
                    ecbs.Add(Rs(blk, ecPer));
                }
            }
            List<byte> fin = new List<byte>();
            int maxData = 0;
            foreach (byte[] b in blocks) if (b.Length > maxData) maxData = b.Length;
            for (int i = 0; i < maxData; i++)
                foreach (byte[] b in blocks) if (i < b.Length) fin.Add(b[i]);
            for (int i = 0; i < ecPer; i++)
                foreach (byte[] b in ecbs) fin.Add(b[i]);
            byte[] cw = fin.ToArray();

            // --- 矩陣 ---
            int size = 17 + 4 * version;
            bool[,] mod = new bool[size, size];
            bool[,] fn = new bool[size, size];
            Action<int, int, bool> setFn = delegate(int x, int y, bool v) { mod[x, y] = v; fn[x, y] = true; };

            for (int i = 0; i < size; i++) { setFn(6, i, i % 2 == 0); setFn(i, 6, i % 2 == 0); }

            int[] fcx = new int[] { 3, size - 4, 3 };
            int[] fcy = new int[] { 3, 3, size - 4 };
            for (int k = 0; k < 3; k++)
                for (int dy = -4; dy <= 4; dy++)
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int x = fcx[k] + dx, y = fcy[k] + dy;
                        if (x < 0 || y < 0 || x >= size || y >= size) continue;
                        int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        setFn(x, y, dist != 2 && dist != 4);
                    }

            int[] ap = ALIGN[version];
            for (int i = 0; i < ap.Length; i++)
                for (int j = 0; j < ap.Length; j++)
                {
                    if ((i == 0 && j == 0) || (i == 0 && j == ap.Length - 1) || (i == ap.Length - 1 && j == 0)) continue;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                            setFn(ap[j] + dx, ap[i] + dy, dist != 1);
                        }
                }

            if (version >= 7)
            {
                int rem = version;
                for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
                int vbits = (version << 12) | rem;
                for (int i = 0; i < 18; i++)
                {
                    bool bit = ((vbits >> i) & 1) != 0;
                    int a = size - 11 + i % 3, b = i / 3;
                    setFn(a, b, bit);
                    setFn(b, a, bit);
                }
            }

            PlaceFormat(mod, fn, size, 0); // 先佔位

            // --- 放資料 ---
            int idx = 0;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < size; vert++)
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? size - 1 - vert : vert;
                        if (!fn[x, y] && idx < cw.Length * 8)
                        {
                            mod[x, y] = ((cw[idx >> 3] >> (7 - (idx & 7))) & 1) != 0;
                            idx++;
                        }
                    }
            }

            // --- 遮罩 ---
            int mask = forcedMask;
            if (mask < 0)
            {
                int best = int.MaxValue;
                mask = 0;
                for (int m = 0; m < 8; m++)
                {
                    bool[,] tmp = (bool[,])mod.Clone();
                    ApplyMask(tmp, fn, size, m);
                    int pen = Penalty(tmp, size);
                    if (pen < best) { best = pen; mask = m; }
                }
            }
            LastCw = "DATA " + Hex(dcw) + "|FINAL " + Hex(cw) + "|VER " + version + " EC " + ecPer;
            ApplyMask(mod, fn, size, mask);
            PlaceFormat(mod, fn, size, mask);

            QrResult r = new QrResult();
            r.Version = version;
            r.Mask = mask;
            r.Size = size;
            r.Modules = mod;
            return r;
        }

        private static void Append(List<bool> bits, int val, int len)
        {
            for (int i = len - 1; i >= 0; i--) bits.Add(((val >> i) & 1) != 0);
        }

        private static byte[] Rs(byte[] data, int ecLen)
        {
            byte[] gen = new byte[ecLen];
            gen[ecLen - 1] = 1;
            byte root = 1;
            for (int i = 0; i < ecLen; i++)
            {
                for (int j = 0; j < ecLen; j++)
                {
                    gen[j] = Mul(gen[j], root);
                    if (j + 1 < ecLen) gen[j] ^= gen[j + 1];
                }
                root = Mul(root, 2);
            }
            byte[] res = new byte[ecLen];
            for (int i = 0; i < data.Length; i++)
            {
                byte factor = (byte)(data[i] ^ res[0]);
                for (int j = 0; j < ecLen - 1; j++) res[j] = res[j + 1];
                res[ecLen - 1] = 0;
                for (int j = 0; j < ecLen; j++) res[j] ^= Mul(gen[j], factor);
            }
            return res;
        }

        private static void ApplyMask(bool[,] m, bool[,] fn, int size, int mask)
        {
            for (int x = 0; x < size; x++)
                for (int y = 0; y < size; y++)
                {
                    if (fn[x, y]) continue;
                    bool inv;
                    switch (mask)
                    {
                        case 0: inv = (x + y) % 2 == 0; break;
                        case 1: inv = y % 2 == 0; break;
                        case 2: inv = x % 3 == 0; break;
                        case 3: inv = (x + y) % 3 == 0; break;
                        case 4: inv = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: inv = (x * y) % 2 + (x * y) % 3 == 0; break;
                        case 6: inv = ((x * y) % 2 + (x * y) % 3) % 2 == 0; break;
                        default: inv = ((x + y) % 2 + (x * y) % 3) % 2 == 0; break;
                    }
                    if (inv) m[x, y] = !m[x, y];
                }
        }

        private static void PlaceFormat(bool[,] m, bool[,] fn, int size, int mask)
        {
            int data = (1 << 3) | mask; // ECC L = 01
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = ((data << 10) | rem) ^ 0x5412;
            Func<int, bool> gb = delegate(int i) { return ((bits >> i) & 1) != 0; };

            for (int i = 0; i <= 5; i++) Set(m, fn, 8, i, gb(i));
            Set(m, fn, 8, 7, gb(6));
            Set(m, fn, 8, 8, gb(7));
            Set(m, fn, 7, 8, gb(8));
            for (int i = 9; i < 15; i++) Set(m, fn, 14 - i, 8, gb(i));

            for (int i = 0; i < 8; i++) Set(m, fn, size - 1 - i, 8, gb(i));
            for (int i = 8; i < 15; i++) Set(m, fn, 8, size - 15 + i, gb(i));
            Set(m, fn, 8, size - 8, true);
        }

        private static void Set(bool[,] m, bool[,] fn, int x, int y, bool v)
        {
            m[x, y] = v;
            fn[x, y] = true;
        }

        private static int Penalty(bool[,] m, int size)
        {
            int pen = 0;
            // 規則 1：連續同色
            for (int y = 0; y < size; y++)
            {
                int run = 1;
                for (int x = 1; x < size; x++)
                {
                    if (m[x, y] == m[x - 1, y]) { run++; if (run == 5) pen += 3; else if (run > 5) pen += 1; }
                    else run = 1;
                }
            }
            for (int x = 0; x < size; x++)
            {
                int run = 1;
                for (int y = 1; y < size; y++)
                {
                    if (m[x, y] == m[x, y - 1]) { run++; if (run == 5) pen += 3; else if (run > 5) pen += 1; }
                    else run = 1;
                }
            }
            // 規則 2：2x2 同色
            for (int x = 0; x < size - 1; x++)
                for (int y = 0; y < size - 1; y++)
                    if (m[x, y] == m[x + 1, y] && m[x, y] == m[x, y + 1] && m[x, y] == m[x + 1, y + 1]) pen += 3;

            // 規則 3：偽 finder 樣式
            int[] p1 = new int[] { 1, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0 };
            int[] p2 = new int[] { 0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1 };
            for (int y = 0; y < size; y++)
                for (int x = 0; x + 11 <= size; x++)
                {
                    if (Match(m, x, y, p1, true) || Match(m, x, y, p2, true)) pen += 40;
                }
            for (int x = 0; x < size; x++)
                for (int y = 0; y + 11 <= size; y++)
                {
                    if (Match(m, x, y, p1, false) || Match(m, x, y, p2, false)) pen += 40;
                }
            // 規則 4：黑白比例
            int dark = 0, total = size * size;
            for (int x = 0; x < size; x++) for (int y = 0; y < size; y++) if (m[x, y]) dark++;
            int k = Math.Abs(dark * 100 / total - 50) / 5;
            pen += k * 10;
            return pen;
        }

        private static bool Match(bool[,] m, int x, int y, int[] pat, bool horizontal)
        {
            for (int i = 0; i < pat.Length; i++)
            {
                int xx = horizontal ? x + i : x;
                int yy = horizontal ? y : y + i;
                if ((m[xx, yy] ? 1 : 0) != pat[i]) return false;
            }
            return true;
        }

        public static Bitmap Render(QrResult r, int scale, int quiet)
        {
            int dim = (r.Size + quiet * 2) * scale;
            Bitmap bmp = new Bitmap(dim, dim);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                using (SolidBrush br = new SolidBrush(Color.Black))
                {
                    for (int y = 0; y < r.Size; y++)
                        for (int x = 0; x < r.Size; x++)
                            if (r.Modules[x, y]) g.FillRectangle(br, (x + quiet) * scale, (y + quiet) * scale, scale, scale);
                }
            }
            return bmp;
        }
    }

    // ============================================================ adb / scrcpy
    internal class Dev
    {
        public string Serial;
        public string State;
        public bool Wifi { get { return Serial != null && Serial.Contains(":"); } }
    }

    internal class Svc
    {
        public string Name;
        public string Type;
        public string Addr;
        public int Port;
        public string End { get { return Addr + ":" + Port; } }
        public bool Pairing { get { return Type != null && Type.StartsWith("_adb-tls-pairing"); } }
        public bool Connect { get { return Type != null && Type.StartsWith("_adb-tls-connect"); } }
    }

    internal static class Adb
    {
        public static string AdbPath = "adb.exe";
        public static string ScrcpyPath = "scrcpy.exe";
        public static string ScrcpyDir = null;
        public static volatile Signaler Log = new Signaler();
        public static bool Verbose = true;

        public static void Locate()
        {
            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] cand = new string[]
            {
                Path.Combine(la, @"Android\Sdk\platform-tools\adb.exe"),
                @"C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe",
                @"C:\Android\platform-tools\adb.exe"
            };
            for (int i = 0; i < cand.Length; i++) if (File.Exists(cand[i])) { AdbPath = cand[i]; break; }

            string pkgs = Path.Combine(la, @"Microsoft\WinGet\Packages");
            if (Directory.Exists(pkgs))
            {
                string[] dirs = Directory.GetDirectories(pkgs, "Genymobile.scrcpy*");
                foreach (string d in dirs)
                {
                    string[] ex = Directory.GetFiles(d, "scrcpy.exe", SearchOption.AllDirectories);
                    if (ex.Length > 0) { ScrcpyPath = ex[0]; ScrcpyDir = Path.GetDirectoryName(ex[0]); break; }
                }
            }
        }

        public static string Run(string exe, string args, int timeoutMs)
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) { lock (sb) sb.AppendLine(e.Data); } };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) { lock (sb) sb.AppendLine(e.Data); } };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        lock (sb) return sb.ToString() + "\n[命令逾時]";
                    }
                    p.WaitForExit();
                }
            }
            catch (Exception ex) { lock (sb) sb.AppendLine("[執行失敗] " + ex.Message); }
            lock (sb) return sb.ToString();
        }

        public static string A(string args, int timeoutMs)
        {
            return Run(AdbPath, args, timeoutMs);
        }

        public static List<Dev> Devices(int timeoutMs)
        {
            List<Dev> list = new List<Dev>();
            string outp = A("devices", timeoutMs);
            string[] lines = outp.Replace("\r", "").Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("List of devices")) continue;
                if (line.StartsWith("*")) continue;
                string[] parts = Regex.Split(line, "\\s+");
                if (parts.Length < 2) continue;
                Dev d = new Dev();
                d.Serial = parts[0];
                d.State = parts[1];
                if (d.Serial == AdbPath) continue;
                list.Add(d);
            }
            return list;
        }

        public static List<Svc> Mdns(int timeoutMs)
        {
            List<Svc> list = new List<Svc>();
            string outp = A("mdns services", timeoutMs);
            string[] lines = outp.Replace("\r", "").Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("List of discovered")) continue;
                string[] parts = Regex.Split(line, "\\s+");
                if (parts.Length < 3) continue;
                Svc s = new Svc();
                s.Name = parts[0];
                int ti = -1;
                for (int i = 1; i < parts.Length; i++) if (parts[i].StartsWith("_adb")) { ti = i; s.Type = parts[i]; break; }
                if (ti < 0) continue;
                for (int i = ti + 1; i < parts.Length; i++)
                {
                    Match m = Regex.Match(parts[i], @"^(\d+\.\d+\.\d+\.\d+):(\d+)$");
                    if (m.Success) { s.Addr = m.Groups[1].Value; s.Port = int.Parse(m.Groups[2].Value); break; }
                }
                if (s.Addr == null) continue;
                list.Add(s);
            }
            return list;
        }
    }

    // 簡單的執行緒安全 log 訊號
    internal class Signaler
    {
        public event Action<string> OnLine;
        public void Write(string s)
        {
            Action<string> h = OnLine;
            if (h != null) h(s);
        }
    }
}

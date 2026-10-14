// 後台常駐版核心：冇視窗、聽手機指令開 scrcpy、keepalive 自動連線
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace AndroidCast
{
    internal static class Core
    {
        public static string Bitrate = "8M";
        public static string MaxSize = null;
        public static bool StayAwake = false;
        public static bool FullscreenPortrait = true;   // 開喺豎屏副螢幕 + 全屏
        public static bool MouseUhid = false;           // true = 鼠標鎖定模式（UHID）
        public static bool FillScreen = true;           // 裁切到同螢幕同比例，填滿冇黑邊
        public static bool FitPhoneResolution = true;   // 叫手機用螢幕嘅比例排版（填滿＋完整）
        private static bool phoneResized = false;
        public static volatile bool QuitRequested = false;
        public static Process Scr;
        public static string KnownFile = null;
        public static readonly List<string> Known = new List<string>();

        private static string logFile = null;
        private static readonly object logLock = new object();

        public static void SetLogFile(string f) { logFile = f; }

        public static void Log(string s)
        {
            string line = DateTime.Now.ToString("MM-dd HH:mm:ss  ") + s;
            if (logFile == null) return;
            lock (logLock)
            {
                try
                {
                    if (File.Exists(logFile) && new FileInfo(logFile).Length > 300000)
                    {
                        string[] all = File.ReadAllLines(logFile);
                        int skip = Math.Max(0, all.Length - 800);
                        File.WriteAllLines(logFile, all.Skip(skip).ToArray());
                    }
                    File.AppendAllText(logFile, line + "\r\n");
                }
                catch { }
            }
        }

        public static void InitKnown(string file)
        {
            KnownFile = file;
            try
            {
                if (File.Exists(file))
                    foreach (string l in File.ReadAllLines(file))
                        if (l.Trim().Length > 0 && !Known.Contains(l.Trim())) Known.Add(l.Trim());
            }
            catch { }
        }

        public static void SaveKnown(string ep)
        {
            if (ep == null || ep.IndexOf(':') < 0 || Known.Contains(ep)) return;
            Known.Add(ep);
            try { if (KnownFile != null) File.WriteAllLines(KnownFile, Known.ToArray()); }
            catch { }
        }

        public static bool TcpAlive(string host, int port, int ms)
        {
            try
            {
                using (System.Net.Sockets.TcpClient c = new System.Net.Sockets.TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(ms)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        public static string EnsureDevice()
        {
            List<Dev> devs = new List<Dev>();
            try { devs = Adb.Devices(8000); } catch { }
            Dev d = devs.FirstOrDefault(delegate(Dev x) { return x.State == "device"; });
            if (d != null) return d.Serial;

            foreach (string ep in new List<string>(Known))
            {
                string host = ep.Split(':')[0];
                int port = 5555;
                try { port = int.Parse(ep.Split(':')[1]); } catch { }
                if (!TcpAlive(host, port, 350)) continue;
                Adb.A("connect " + ep, 6000);
                Thread.Sleep(400);
                try { devs = Adb.Devices(6000); } catch { }
                d = devs.FirstOrDefault(delegate(Dev x) { return x.State == "device"; });
                if (d != null) return d.Serial;
            }

            try
            {
                List<Svc> svcs = SvcScan.Scan(2500);
                foreach (Svc s in svcs)
                {
                    if (!s.Connect) continue;
                    Log("mDNS 見到 " + s.End + "，嘗試連線");
                    Adb.A("connect " + s.End, 6000);
                    Thread.Sleep(400);
                    devs = Adb.Devices(6000);
                    d = devs.FirstOrDefault(delegate(Dev x) { return x.State == "device"; });
                    if (d != null) return d.Serial;
                }
            }
            catch { }
            return null;
        }

        public static System.Windows.Forms.Screen PickScreen()
        {
            System.Windows.Forms.Screen[] ss = System.Windows.Forms.Screen.AllScreens;
            foreach (System.Windows.Forms.Screen s in ss)
                if (!s.Primary && s.Bounds.Height > s.Bounds.Width) return s;
            foreach (System.Windows.Forms.Screen s in ss)
                if (s.Bounds.Height > s.Bounds.Width) return s;
            return System.Windows.Forms.Screen.PrimaryScreen;
        }

        public static string PlacementArgs()
        {
            try
            {
                System.Windows.Forms.Screen pick = PickScreen();
                System.Drawing.Rectangle b = pick.Bounds;
                Log("投屏窗放喺 " + pick.DeviceName + "  " + b.X + "," + b.Y + "  " + b.Width + "x" + b.Height + "（全屏）");
                return " --window-x=" + b.X + " --window-y=" + b.Y + " --window-width=" + b.Width +
                       " --window-height=" + b.Height + " --fullscreen";
            }
            catch (Exception ex) { Log("螢幕偵測失敗：" + ex.Message); return ""; }
        }

        // 手機畫面比例同螢幕唔一樣 → 裁切（唔留黑邊）
        public static string CropArgs()
        {
            try
            {
                string sz = DeviceScreenSize();
                if (sz == null) return "";
                string[] p = sz.Split('x');
                int pw = int.Parse(p[0]), ph = int.Parse(p[1]);
                System.Drawing.Rectangle b = PickScreen().Bounds;
                double A = (double)b.Width / b.Height;
                int cw, ch, cx, cy;
                if ((double)pw / ph > A) { ch = ph; cw = (int)Math.Round(ph * A); cx = (pw - cw) / 2; cy = 0; }
                else { cw = pw; ch = (int)Math.Round(pw / A); cx = 0; cy = (ph - ch) / 2; }
                cw -= cw % 2; ch -= ch % 2; cx -= cx % 2; cy -= cy % 2;
                Log("裁切 " + cw + "x" + ch + " @ " + cx + "," + cy + "（手機 " + pw + "x" + ph + " → 螢幕 " + b.Width + "x" + b.Height + "）");
                return " --crop=" + cw + ":" + ch + ":" + cx + ":" + cy;
            }
            catch (Exception ex) { Log("算裁切失敗：" + ex.Message); return ""; }
        }

        public static string PhysicalScreenSize()
        {
            string dev = DeviceInfo();
            if (dev == "offline") return null;
            string o = Adb.A("-s " + dev + " shell wm size", 8000);
            foreach (string line in o.Split(new char[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.IndexOf("Physical", StringComparison.OrdinalIgnoreCase) < 0) continue;
                Match m = Regex.Match(line, @"(\d+)x(\d+)");
                if (m.Success) return m.Value;
            }
            return null;
        }

        // 叫手機用「同螢幕一樣嘅比例」去排版：填滿螢幕之餘仲睇得齊（唔會切頭切尾）
        public static void ResizePhoneForScreen(string target)
        {
            try
            {
                string phys = PhysicalScreenSize();
                string cur = DeviceScreenSize();
                if (phys == null || cur == null) return;
                if (cur != phys) phoneResized = true;      // 之前留低嘅 override，收工時要還原
                if (!FitPhoneResolution) return;
                string[] p = phys.Split('x');
                int pw = int.Parse(p[0]), ph = int.Parse(p[1]);
                System.Drawing.Rectangle b = PickScreen().Bounds;
                double A = (double)b.Width / b.Height;
                int nw = pw, nh = (int)Math.Round(pw / A);
                nh -= nh % 2;
                if (nw == pw && nh == ph) return;
                string want = nw + "x" + nh;
                if (cur == want) { phoneResized = true; return; }
                string o = Adb.A("-s " + target + " shell wm size " + want, 10000);
                Log("夾手機解析度 " + phys + " → " + want + "  " + o.Trim());
                phoneResized = true;
                Thread.Sleep(1800);
            }
            catch (Exception ex) { Log("夾解析度失敗：" + ex.Message); }
        }

        public static void ResetPhoneSize()
        {
            if (!phoneResized) return;
            phoneResized = false;
            try
            {
                string dev = DeviceInfo();
                if (dev != "offline")
                {
                    Adb.A("-s " + dev + " shell wm size reset", 10000);
                    Log("已還原手機原本解析度（wm size reset）");
                }
            }
            catch { }
        }

        public static string DeviceScreenSize()
        {
            string dev = DeviceInfo();
            if (dev == "offline") return null;
            string o = Adb.A("-s " + dev + " shell wm size", 8000);
            string first = null;
            string[] lines = o.Split(new char[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                Match m = Regex.Match(line, @"(\d+)x(\d+)");
                if (!m.Success) continue;
                if (line.IndexOf("Override", StringComparison.OrdinalIgnoreCase) >= 0) return m.Value;
                if (first == null) first = m.Value;
            }
            return first;
        }

        public static bool StartScrcpy(string target)
        {
            if (Adb.ScrcpyDir == null) return false;
            // 先叫醒手機（唔係投屏會係黑畫面），再按需要夾解析度
            try { Adb.A("-s " + target + " shell input keyevent KEYCODE_WAKEUP", 8000); } catch { }
            if (FitPhoneResolution) ResizePhoneForScreen(target);
            string args = "-s " + target;
            if (!string.IsNullOrEmpty(Bitrate)) args += " --video-bit-rate=" + Bitrate;
            if (!string.IsNullOrEmpty(MaxSize)) args += " --max-size=" + MaxSize;
            if (StayAwake) args += " --stay-awake";
            args += " --keep-active";
            if (FullscreenPortrait) args += PlacementArgs();
            if (FillScreen && FullscreenPortrait) args += CropArgs();
            if (MouseUhid) args += " --mouse=uhid --keyboard=uhid";
            args += " --window-title=\"無線投屏 " + target + "\"";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Adb.ScrcpyPath, args);
                psi.WorkingDirectory = Adb.ScrcpyDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Scr = new Process();
                Scr.StartInfo = psi;
                Scr.EnableRaisingEvents = true;
                Scr.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Log("scrcpy: " + e.Data); };
                Scr.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    Log("scrcpy: " + e.Data);
                    if (e.Data.IndexOf("INJECT_EVENTS") >= 0)
                        Log(">>> 手機未開「開發者選項 → USB 偵錯（安全設定）」：而家只可以睇，未可以用電腦鼠標／鍵盤控制。開咗再重啟手機即可以控制。");
                };
                Scr.Exited += delegate { Log("scrcpy 已結束"); ResetPhoneSize(); };
                Scr.Start();
                Scr.BeginOutputReadLine();
                Scr.BeginErrorReadLine();
                Log("投屏啟動 " + target + "  " + args);
                SaveKnown(target);
                return true;
            }
            catch (Exception ex)
            {
                Log("啟動 scrcpy 失敗: " + ex.Message);
                return false;
            }
        }

        public static void StopCast()
        {
            try
            {
                if (Scr != null && !Scr.HasExited)
                {
                    Scr.Kill();
                    Log("已停止投屏");
                }
            }
            catch { }
            ResetPhoneSize();   // 收工還原手機原本 9:20
        }

        public static string DeviceInfo()
        {
            try
            {
                List<Dev> devs = Adb.Devices(5000);
                Dev d = devs.FirstOrDefault(delegate(Dev x) { return x.State == "device"; });
                return d == null ? "offline" : d.Serial;
            }
            catch { return "offline"; }
        }

        public static string Status()
        {
            string sc = (Scr != null && !Scr.HasExited) ? "casting" : "idle";
            return "ok " + sc + " " + DeviceInfo();
        }

        public static string Cast()
        {
            try
            {
                if (Scr != null && !Scr.HasExited) return "already";
                string t = EnsureDevice();
                if (t == null) { Log("cast 要求：搵唔到手機（無線偵錯熄咗？）"); return "no_device"; }
                if (!StartScrcpy(t)) return "scrcpy_fail";
                return "ok " + t;
            }
            catch (Exception ex) { Log("cast 出錯: " + ex.Message); return "error " + ex.Message; }
        }

        // 手機 APK / 其他程式嘅指令入口
        public static string Handle(string path)
        {
            string p = path == null ? "/" : path.Trim();
            int q = p.IndexOf('?');
            if (q >= 0) p = p.Substring(0, q);
            p = p.TrimStart('/').ToLower();
            if (p == "ping") return "ok androidcast " + Agent.Version + " bg scrcpy=" + (Adb.ScrcpyDir != null ? "ready" : "no");
            if (p == "status") return Status();
            if (p == "cast") return Cast();
            if (p == "stop") { StopCast(); return "stopped"; }
            if (p == "quit") { QuitRequested = true; Log("收到 quit 指令，準備收工"); return "bye"; }
            return "unknown_command";
        }

        // 每 4 秒：冇裝置就試自動連線
        public static void Loop()
        {
            int tick = 0;
            while (!QuitRequested)
            {
                try
                {
                    tick++;
                    List<Dev> devs = Adb.Devices(6000);
                    Dev d = devs.FirstOrDefault(delegate(Dev x) { return x.State == "device"; });
                    if (d == null)
                    {
                        if (tick % 2 == 1)   // 大約每 8 秒掃一次 mDNS
                        {
                            List<Svc> svcs = SvcScan.Scan(1800);
                            foreach (Svc s in svcs)
                            {
                                if (!s.Connect) continue;
                                Adb.A("connect " + s.End, 6000);
                                SaveKnown(s.End);
                            }
                        }
                        foreach (string ep in new List<string>(Known))
                        {
                            string host = ep.Split(':')[0];
                            int port = 5555;
                            try { port = int.Parse(ep.Split(':')[1]); } catch { }
                            if (!TcpAlive(host, port, 300)) continue;
                            Adb.A("connect " + ep, 6000);
                        }
                    }
                }
                catch (Exception ex) { Log("keepalive 出錯: " + ex.Message); }
                for (int i = 0; i < 40 && !QuitRequested; i++) Thread.Sleep(100);
            }
        }
    }
}

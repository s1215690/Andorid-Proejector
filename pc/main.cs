// 主程式（GUI）+ CLI 測試模式
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
    internal static class Program
    {
        public static bool DemoQr = false;
        public static bool MinStart = false;
        private static System.Threading.Mutex BgMutex;

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length >= 3 && args[0] == "--qr-png")
            {
                int msk = -1;
                if (args.Length >= 4) int.TryParse(args[3], out msk);
                QrResult rp = Qr.Encode(args[1], msk);
                using (Bitmap bmp = Qr.Render(rp, 8, 4)) bmp.Save(args[2], System.Drawing.Imaging.ImageFormat.Png);
                Console.Out.WriteLine("ok ver=" + rp.Version + " mask=" + rp.Mask);
                return;
            }
            if (args.Length >= 2 && args[0] == "--qr-cw")
            {
                QrResult rr = Qr.Encode(args[1], -1);
                Console.Out.WriteLine(Qr.LastCw + "|MASK " + rr.Mask);
                return;
            }
            if (args.Length >= 2 && args[0] == "--qr-test")
            {
                int mask = -1;
                if (args.Length >= 3) int.TryParse(args[2], out mask);
                QrResult r = Qr.Encode(args[1], mask);
                Console.Out.Write(r.Dump());
                return;
            }
            for (int i = 0; i < args.Length; i++) if (args[i] == "--demo-qr") DemoQr = true;
            for (int i = 0; i < args.Length; i++) if (args[i] == "--min") Program.MinStart = true;
            for (int i = 0; i < args.Length; i++) if (args[i] == "--bg") { BgMain(); return; }
            for (int i = 0; i < args.Length; i++) if (args[i] == "--quit") { QuitMain(); return; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Adb.Locate(); } catch { }
            Application.Run(new MainForm());
        }

        // ---------- 後台常駐模式（--bg）：冇視窗、冇工作列 ----------
        private static void BgMain()
        {
            bool created;
            BgMutex = new System.Threading.Mutex(true, "Global\\AndroidCastBackground", out created);
            if (!created)
            {
                return;   // 已經有一個後台喺度行
            }
            Adb.Locate();
            string dir = Application.StartupPath;
            Core.SetLogFile(System.IO.Path.Combine(dir, "bg.log"));
            Core.InitKnown(System.IO.Path.Combine(dir, "known.txt"));
            Adb.Log.OnLine += delegate(string s) { Core.Log(s); };
            Core.Log("=== 後台啟動（無介面）adb=" + Adb.AdbPath + "  scrcpy=" + (Adb.ScrcpyDir == null ? "?" : Adb.ScrcpyDir));
            try { Adb.A("start-server", 15000); } catch { }
            try { Agent.Start(Core.Handle); }
            catch (Exception ex) { Core.Log("agent 起唔到：" + ex.Message); }
            Core.Log("聽手機指令中（TCP " + Agent.HttpPort + " / UDP " + Agent.UdpPort + "）");
            Core.Loop();
            Core.StopCast();
            Core.Log("=== 後台已結束 ===");
            GC.KeepAlive(BgMutex);
        }

        // ---------- 一鍵關掉後台（--quit）----------
        private static void QuitMain()
        {
            string msg;
            try
            {
                System.Net.HttpWebRequest req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("http://127.0.0.1:8765/quit");
                req.Timeout = 3000;
                req.Method = "GET";
                using (System.Net.WebResponse resp = req.GetResponse())
                using (System.IO.StreamReader sr = new System.IO.StreamReader(resp.GetResponseStream()))
                    msg = sr.ReadToEnd().Trim();
                msg = "後台已收到指令：" + msg + "（已收工）";
            }
            catch (Exception ex)
            {
                msg = "搵唔到後台服務（可能冇開）：" + ex.Message;
            }
            System.Windows.Forms.Application.EnableVisualStyles();
            QuitNotice f = new QuitNotice(msg);
            System.Windows.Forms.Application.Run(f);
        }
    }

    // 「關掉後台」嘅小通知視窗（6 秒自動關，唔會阻住）
    internal class QuitNotice : Form
    {
        public QuitNotice(string msg)
        {
            Text = "無線投屏 後台";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(440, 132);
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Microsoft JhengHei UI", 9F);
            Label l = new Label();
            l.Text = msg;
            l.Location = new Point(16, 16);
            l.Size = new Size(408, 62);
            Controls.Add(l);
            Label l2 = new Label();
            l2.Text = "此視窗 6 秒後自動關閉（按一下即關）";
            l2.ForeColor = Color.Gray;
            l2.Location = new Point(16, 92);
            l2.Size = new Size(408, 22);
            Controls.Add(l2);
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 6000;
            t.Tick += delegate { t.Stop(); Close(); };
            t.Start();
            Click += delegate { Close(); };
        }
    }

    internal class QrSession
    {
        public string Name;
        public string Pass;
        public DateTime Deadline;
        public DateTime Started;
        public bool Paired;
    }

    internal class MainForm : Form
    {
        private ListView lv;
        private Button btnScan, btnUsb, btnCast, btnStop, btnQr, btnFindPair, btnPairCode;
        private CheckBox chkAutoScan, chkStay, chkAutoConn, chkFs, chkUhid, chkFill;
        private ComboBox cmbBitrate, cmbMax;
        private PictureBox pbQr;
        private Label lblQrHint, lblQrState, lblPairSvc, lblStatus, lblPaths, lblCodeLabel;
        private TextBox txtCode, txtLog;
        private System.Windows.Forms.Timer tmr;
        private bool busy;
        private QrSession sess;
        private Process scr;
        private string lastTarget;
        private Dictionary<string, DateTime> tried = new Dictionary<string, DateTime>();
        private List<string> known = new List<string>();
        private string knownFile;

        public MainForm()
        {
            Text = "安卓無線投屏 — scrcpy";
            ClientSize = new Size(764, 764);
            MinimumSize = new Size(700, 600);
            Font = new Font("Microsoft JhengHei UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            knownFile = Path.Combine(Application.StartupPath, "known.txt");

            // ---------- 裝置 ----------
            GroupBox gbDev = new GroupBox();
            gbDev.Text = "裝置";
            gbDev.Location = new Point(10, 6);
            gbDev.Size = new Size(744, 214);
            gbDev.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(gbDev);

            lv = new ListView();
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.MultiSelect = false;
            lv.HideSelection = false;
            lv.Location = new Point(12, 22);
            lv.Size = new Size(720, 138);
            lv.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            lv.Columns.Add("裝置（序號 / IP:埠）", 300);
            lv.Columns.Add("類型", 90);
            lv.Columns.Add("狀態", 130);
            lv.Columns.Add("來源", 180);
            lv.DoubleClick += delegate { Cast(); };
            gbDev.Controls.Add(lv);

            btnScan = MakeButton(gbDev, "重新掃描", 12, 168, 100);
            btnScan.Click += delegate { DoScan(true); };
            btnUsb = MakeButton(gbDev, "USB 轉無線", 118, 168, 110);
            btnUsb.Click += delegate { UsbToWireless(); };
            btnCast = MakeButton(gbDev, "開始投屏", 234, 168, 100);
            btnCast.Click += delegate { Cast(); };
            btnStop = MakeButton(gbDev, "停止投屏", 340, 168, 100);
            btnStop.Click += delegate { StopCast(); };

            chkAutoScan = new CheckBox();
            chkAutoScan.Text = "自動掃描（每 3 秒）";
            chkAutoScan.Checked = true;
            chkAutoScan.Location = new Point(452, 172);
            chkAutoScan.Size = new Size(170, 24);
            gbDev.Controls.Add(chkAutoScan);

            chkAutoConn = new CheckBox();
            chkAutoConn.Text = "自動連接上次裝置";
            chkAutoConn.Checked = true;
            chkAutoConn.Location = new Point(600, 172);
            chkAutoConn.Size = new Size(140, 24);
            chkAutoConn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            gbDev.Controls.Add(chkAutoConn);

            // ---------- 配對 ----------
            GroupBox gbPair = new GroupBox();
            gbPair.Text = "首次配對（唔使打 IP）";
            gbPair.Location = new Point(10, 226);
            gbPair.Size = new Size(744, 302);
            gbPair.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(gbPair);

            pbQr = new PictureBox();
            pbQr.Location = new Point(14, 24);
            pbQr.Size = new Size(232, 232);
            pbQr.BorderStyle = BorderStyle.FixedSingle;
            pbQr.SizeMode = PictureBoxSizeMode.CenterImage;
            gbPair.Controls.Add(pbQr);

            lblQrHint = new Label();
            lblQrHint.Text = "未產生 QR 碼";
            lblQrHint.Location = new Point(14, 262);
            lblQrHint.Size = new Size(232, 32);
            lblQrHint.ForeColor = Color.DimGray;
            gbPair.Controls.Add(lblQrHint);

            btnQr = MakeButton(gbPair, "① 產生 QR 碼（免打碼）", 258, 24, 210);
            btnQr.Click += delegate { StartQr(); };

            lblQrState = new Label();
            lblQrState.Text = "手機：無線偵錯 → 使用 QR 碼配對裝置 → 掃這個碼，完全唔使輸入。";
            lblQrState.Location = new Point(258, 62);
            lblQrState.Size = new Size(470, 62);
            gbPair.Controls.Add(lblQrState);

            Label sep = new Label();
            sep.Text = "———————————————————————————";
            sep.Location = new Point(258, 128);
            sep.Size = new Size(470, 14);
            sep.ForeColor = Color.Silver;
            gbPair.Controls.Add(sep);

            Label lblAlt = new Label();
            lblAlt.Text = "或者用 6 位配對碼（位址自動搵，唔使打 IP）：";
            lblAlt.Location = new Point(258, 146);
            lblAlt.Size = new Size(470, 20);
            gbPair.Controls.Add(lblAlt);

            btnFindPair = MakeButton(gbPair, "② 搵手機配對服務", 258, 172, 160);
            btnFindPair.Click += delegate { PairByCode(); };

            lblPairSvc = new Label();
            lblPairSvc.Text = "未搵到配對服務";
            lblPairSvc.Location = new Point(258, 204);
            lblPairSvc.Size = new Size(470, 20);
            lblPairSvc.ForeColor = Color.DimGray;
            gbPair.Controls.Add(lblPairSvc);

            lblCodeLabel = new Label();
            lblCodeLabel.Text = "配對碼：";
            lblCodeLabel.Location = new Point(258, 234);
            lblCodeLabel.Size = new Size(60, 22);
            gbPair.Controls.Add(lblCodeLabel);

            txtCode = new TextBox();
            txtCode.Location = new Point(318, 230);
            txtCode.Size = new Size(80, 24);
            txtCode.MaxLength = 6;
            gbPair.Controls.Add(txtCode);

            btnPairCode = MakeButton(gbPair, "用配對碼配對", 408, 230, 130);
            btnPairCode.Click += delegate { PairByCode(); };

            Label lblTip = new Label();
            lblTip.Text = "若冇線：手機 設定 → 開發者選項 → 無線偵錯 → 使用 QR 碼配對裝置（最快）。\r\n若手上有線：按上面「USB 轉無線」，一次之後永久免輸入。";
            lblTip.Location = new Point(258, 262);
            lblTip.Size = new Size(470, 34);
            lblTip.ForeColor = Color.DimGray;
            gbPair.Controls.Add(lblTip);

            // ---------- 選項 ----------
            GroupBox gbOpt = new GroupBox();
            gbOpt.Text = "投屏選項";
            gbOpt.Location = new Point(10, 534);
            gbOpt.Size = new Size(744, 80);
            gbOpt.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(gbOpt);

            Label l1 = new Label(); l1.Text = "碼率"; l1.Location = new Point(14, 28); l1.Size = new Size(38, 20); gbOpt.Controls.Add(l1);
            cmbBitrate = new ComboBox();
            cmbBitrate.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbBitrate.Items.AddRange(new object[] { "4M", "8M", "16M", "24M" });
            cmbBitrate.SelectedIndex = 1;
            cmbBitrate.Location = new Point(56, 24);
            cmbBitrate.Size = new Size(80, 24);
            gbOpt.Controls.Add(cmbBitrate);

            Label l2 = new Label(); l2.Text = "解像度"; l2.Location = new Point(150, 28); l2.Size = new Size(52, 20); gbOpt.Controls.Add(l2);
            cmbMax = new ComboBox();
            cmbMax.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbMax.Items.AddRange(new object[] { "原生", "1440", "1280", "1024" });
            cmbMax.SelectedIndex = 0;
            cmbMax.Location = new Point(204, 24);
            cmbMax.Size = new Size(100, 24);
            gbOpt.Controls.Add(cmbMax);

            chkStay = new CheckBox();
            chkStay.Text = "保持螢幕常亮";
            chkStay.Checked = false;
            chkStay.Location = new Point(322, 26);
            chkStay.Size = new Size(140, 24);
            gbOpt.Controls.Add(chkStay);

            chkFs = new CheckBox();
            chkFs.Text = "豎屏全屏";
            chkFs.Checked = true;
            chkFs.Location = new Point(468, 26);
            chkFs.Size = new Size(110, 24);
            gbOpt.Controls.Add(chkFs);

            chkUhid = new CheckBox();
            chkUhid.Text = "鼠標鎖定";
            chkUhid.Checked = false;
            chkUhid.Location = new Point(584, 26);
            chkUhid.Size = new Size(130, 24);
            gbOpt.Controls.Add(chkUhid);

            lblPaths = new Label();
            lblPaths.Location = new Point(132, 52);
            lblPaths.Size = new Size(596, 20);
            lblPaths.ForeColor = Color.DimGray;

            chkFill = new CheckBox();
            chkFill.Text = "填滿螢幕";
            chkFill.Checked = true;
            chkFill.Location = new Point(14, 50);
            chkFill.Size = new Size(112, 24);
            gbOpt.Controls.Add(chkFill);
            lblPaths.Text = "adb: " + Path.GetFileName(Path.GetDirectoryName(Adb.AdbPath)) + "\\adb.exe     scrcpy: " + Path.GetFileName(Adb.ScrcpyDir == null ? "?" : Adb.ScrcpyDir) + (Adb.ScrcpyDir == null ? "（未安裝）" : "（已就緒）");
            gbOpt.Controls.Add(lblPaths);

            // ---------- 狀態 / 記錄 ----------
            lblStatus = new Label();
            lblStatus.Text = "準備中…";
            lblStatus.Location = new Point(12, 622);
            lblStatus.Size = new Size(740, 22);
            lblStatus.Font = new Font(Font, FontStyle.Bold);
            lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(lblStatus);

            txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.WordWrap = true;
            txtLog.Font = new Font("Consolas", 8.5F);
            txtLog.BackColor = Color.White;
            txtLog.Location = new Point(12, 646);
            txtLog.Size = new Size(740, 106);
            txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(txtLog);

            Adb.Log.OnLine += delegate(string s) { Log(s); };

            if (Program.MinStart) { WindowState = FormWindowState.Minimized; ShowInTaskbar = true; }
            Load += delegate { OnLoaded(); };
            FormClosing += delegate { StopCast(); };

            tmr = new System.Windows.Forms.Timer();
            tmr.Interval = 3000;
            tmr.Tick += delegate { if (chkAutoScan.Checked) DoScan(false); };
        }

        private Button MakeButton(Control parent, string text, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, 28);
            parent.Controls.Add(b);
            return b;
        }

        private void Log(string s)
        {
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(Log), s); } catch { } return; }
            string t = DateTime.Now.ToString("HH:mm:ss");
            txtLog.AppendText(t + "  " + s.Replace("\r", "").Replace("\n", " | ") + "\r\n");
            if (txtLog.TextLength > 40000) txtLog.Text = txtLog.Text.Substring(txtLog.TextLength - 20000);
        }

        private void SetStatus(string s)
        {
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(SetStatus), s); } catch { } return; }
            lblStatus.Text = s;
        }

        private void OnLoaded()
        {
            Log("adb = " + Adb.AdbPath);
            Log("scrcpy = " + (Adb.ScrcpyPath ?? "?") + (Adb.ScrcpyDir != null ? ("  (" + Adb.ScrcpyDir + ")") : ""));
            LoadKnown();
            Task.Factory.StartNew(delegate
            {
                Adb.A("start-server", 15000);
                Adb.Log.Write("adb server 已啟動");
                AutoConnectKnown();
            });
            tmr.Start();
            DoScan(true);
            if (Program.DemoQr) StartQr();
            try { Agent.Start(HandleAgentRequest); }
            catch (Exception ex) { Log("手機控制服務啟動失敗：" + ex.Message); }
        }

        private void LoadKnown()
        {
            try
            {
                if (File.Exists(knownFile))
                    foreach (string l in File.ReadAllLines(knownFile))
                        if (l.Trim().Length > 0 && !known.Contains(l.Trim())) known.Add(l.Trim());
            }
            catch { }
        }

        private void SaveKnown(string ep)
        {
            if (ep == null || !ep.Contains(":") || known.Contains(ep)) return;
            known.Add(ep);
            try { File.WriteAllLines(knownFile, known.ToArray()); } catch { }
        }

        private void AutoConnectKnown()
        {
            if (!chkAutoConn.Checked) return;
            List<string> snapshot = new List<string>(known);
            foreach (string ep in snapshot)
            {
                string host = ep.Split(':')[0];
                int port = 5555;
                try { port = int.Parse(ep.Split(':')[1]); } catch { }
                if (!TcpAlive(host, port, 400)) continue;
                string o = Adb.A("connect " + ep, 8000);
                Log("自動連接 " + ep + " → " + o.Trim());
            }
        }

        private static bool TcpAlive(string host, int port, int ms)
        {
            try
            {
                IAsyncResult ar = null;
                using (TcpClient c = new TcpClient())
                {
                    ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(ms)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        private void DoScan(bool force)
        {
            if (busy) return;
            busy = true;
            if (force) SetStatus("掃描中…");
            Task.Factory.StartNew(delegate
            {
                List<Dev> devs = new List<Dev>();
                List<Svc> svcs = new List<Svc>();
                try { devs = Adb.Devices(8000); } catch { }
                try { svcs = ScanServices(2600); } catch { }
                try { BeginInvoke(new Action<List<Dev>, List<Svc>>(Apply), devs, svcs); } catch { }
            });
        }

        private static List<Svc> ScanServices(int ms)
        {
            return SvcScan.Scan(ms);
        }

        private void Apply(List<Dev> devs, List<Svc> svcs)
        {
            try
            {
                string sel = null;
                if (lv.SelectedItems.Count > 0) sel = (string)lv.SelectedItems[0].Tag;

                lv.BeginUpdate();
                lv.Items.Clear();
                foreach (Dev d in devs)
                {
                    if (d.State == "device")
                    {
                        ListViewItem it = new ListViewItem(d.Serial);
                        it.SubItems.Add(d.Wifi ? "Wi-Fi" : "USB");
                        it.SubItems.Add("已連線");
                        Svc m = svcs.FirstOrDefault(delegate(Svc s) { return s.Connect && s.End == d.Serial; });
                        it.SubItems.Add(m != null ? "mDNS 自動發現" : (d.Wifi ? "手動/記錄" : "USB 線"));
                        it.Tag = d.Serial;
                        if (d.Wifi) SaveKnown(d.Serial);
                        lv.Items.Add(it);
                    }
                    else
                    {
                        ListViewItem it = new ListViewItem(d.Serial);
                        it.SubItems.Add(d.Wifi ? "Wi-Fi" : "USB");
                        it.SubItems.Add(d.State == "unauthorized" ? "未授權（手機要按允許）" : d.State);
                        it.SubItems.Add("");
                        it.Tag = d.Serial;
                        lv.Items.Add(it);
                    }
                }
                // mDNS 有但未連線嘅裝置
                foreach (Svc s in svcs)
                {
                    if (!s.Connect) continue;
                    if (lv.Items.Cast<ListViewItem>().Any(delegate(ListViewItem it) { return (string)it.Tag == s.End; })) continue;
                    ListViewItem it2 = new ListViewItem(s.End);
                    it2.SubItems.Add("Wi-Fi");
                    it2.SubItems.Add("未連線");
                    it2.SubItems.Add("mDNS 發現 " + s.Name);
                    it2.Tag = s.End;
                    lv.Items.Add(it2);
                    TryConnect(s.End);
                }
                if (sel != null)
                    foreach (ListViewItem it in lv.Items)
                        if ((string)it.Tag == sel) it.Selected = true;
                if (lv.SelectedItems.Count == 0 && lv.Items.Count > 0) lv.Items[0].Selected = true;
                lv.EndUpdate();

                // 狀態行
                int online = devs.Count(delegate(Dev d) { return d.State == "device"; });
                string st;
                if (online > 0)
                {
                    Dev first = devs.First(delegate(Dev d) { return d.State == "device"; });
                    lastTarget = first.Serial;
                    st = "已連線：" + first.Serial + "（" + (first.Wifi ? "Wi-Fi 無線" : "USB") + "）— 可以按「開始投屏」";
                }
                else if (scr != null && !scr.HasExited) st = "投屏中…";
                else st = "未發現手機 — 用手機掃 QR 碼，或插 USB 按「USB 轉無線」";
                SetStatus(st);

                // 配對服務顯示
                List<Svc> pairs = svcs.Where(delegate(Svc s) { return s.Pairing; }).ToList();
                if (sess != null && DateTime.Now > sess.Deadline)
                {
                    sess = null;
                    lblQrState.Text = "QR 碼已逾時，請再按「產生 QR 碼」。";
                }
                if (sess != null && !sess.Paired)
                {
                    Svc mine = pairs.FirstOrDefault(delegate(Svc s) { return s.Name.StartsWith(sess.Name) || s.Name == sess.Name; });
                    if (mine == null && pairs.Count > 0 && (DateTime.Now - sess.Started).TotalSeconds > 12)
                    {
                        mine = pairs[0];
                        Log("配對服務名唔 match（" + mine.Name + "），照試第一個");
                    }
                    if (mine != null)
                    {
                        DateTime lastTry;
                        bool skip = tried.TryGetValue("pair:" + mine.End, out lastTry) && (DateTime.Now - lastTry).TotalSeconds < 20;
                        if (skip)
                        {
                            lblQrState.Text = "配對服務喺度，等下次重試…";
                        }
                        else
                        {
                            tried["pair:" + mine.End] = DateTime.Now;
                            Log("搵到配對服務 " + mine.End + "（" + mine.Name + "）");
                            sess.Paired = true;
                            string code = sess.Pass;
                            Svc target = mine;
                            SetStatus("配對中…");
                            lblQrState.Text = "手機已掃到 QR，配對中…";
                            Task.Factory.StartNew(delegate { DoPair(target.End, code); });
                        }
                    }
                    else lblQrState.Text = "等待手機掃碼…（" + Math.Max(0, (int)(sess.Deadline - DateTime.Now).TotalSeconds) + " 秒）";
                }
                else if (pairs.Count > 0)
                {
                    lblPairSvc.Text = "搵到配對服務：" + pairs[0].End + "（" + pairs[0].Name + "）";
                }
                else if (sess == null) lblPairSvc.Text = "未搵到配對服務（手機要先開「使用配對碼配對裝置」畫面）";

                if (svcs.Count > 0)
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (Svc s in svcs) sb.Append(s.End + " ");
                    Log("mDNS: " + sb.ToString().Trim());
                }
            }
            catch (Exception ex) { Log("介面更新失敗: " + ex.Message); }
            finally { busy = false; }
        }

        private void TryConnect(string ep)
        {
            DateTime last;
            if (tried.TryGetValue(ep, out last) && (DateTime.Now - last).TotalSeconds < 12) return;
            tried[ep] = DateTime.Now;
            Task.Factory.StartNew(delegate
            {
                string o = Adb.A("connect " + ep, 8000);
                Log("連接 " + ep + " → " + o.Trim());
            });
        }

        // ---------------- QR ----------------
        private void StartQr()
        {
            string name = "studio-" + Rand(10);
            string code = RandDigits(6);
            string payload = "WIFI:T:ADB;S:" + name + ";P:" + code + ";;";
            try
            {
                QrResult r = Qr.Encode(payload, -1);
                Image old = pbQr.Image;
                pbQr.Image = Qr.Render(r, 8, 2);
                if (old != null) old.Dispose();
                sess = new QrSession();
                sess.Name = name;
                sess.Pass = code;
                sess.Started = DateTime.Now;
                sess.Deadline = DateTime.Now.AddSeconds(150);
                lblQrState.Text = "等待手機掃碼…（150 秒）";
                lblQrHint.Text = "手機：設定 → 開發者選項 → 無線偵錯 → 使用 QR 碼配對裝置";
                lblPairSvc.Text = "QR 密碼：" + code + "（唔使理，手機掃碼會自動帶）";
                Log("已產生 QR：name=" + name + " code=" + code + " version=" + r.Version + " mask=" + r.Mask);
            }
            catch (Exception ex) { Log("QR 產生失敗: " + ex.Message); }
        }

        private static Random rnd = new Random();
        private static string Rand(int n)
        {
            const string cs = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz123456789";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < n; i++) sb.Append(cs[rnd.Next(cs.Length)]);
            return sb.ToString();
        }
        private static string RandDigits(int n)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < n; i++) sb.Append((char)('0' + rnd.Next(10)));
            return sb.ToString();
        }

        // ---------------- 6 位碼配對 ----------------
        private void PairByCode()
        {
            List<Svc> pairs = new List<Svc>();
            try { pairs = SvcScan.Scan(3000).Where(delegate(Svc s) { return s.Pairing; }).ToList(); } catch { }
            using (PairDialog dlg = new PairDialog(pairs, txtCode.Text))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                DoPair(dlg.EndPoint, dlg.Code);
            }
        }

        private void DoPair(string ep, string code)
        {
            SetStatus("配對中…");
            string o = Adb.A("pair " + ep + " " + code, 25000);
            Log("pair " + ep + " → " + o.Trim());
            bool ok = o.IndexOf("Successfully paired", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!ok)
            {
                BeginInvoke(new Action(delegate
                {
                    if (sess != null)
                    {
                        sess.Paired = false; // 容許下次掃描重試（有 20 秒節流）
                        SetStatus("配對失敗（會自動重試）— 睇下面記錄");
                        Log("配對失敗：" + o.Trim());
                    }
                    else
                    {
                        SetStatus("配對失敗 — 睇下面記錄，或者試 QR 配對");
                        MessageBox.Show(this, "配對失敗：\n" + o, "配對", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }));
                return;
            }
            string ip = ep.Split(':')[0];
            Thread.Sleep(1500);
            string target = null;
            try
            {
                List<Svc> cs = SvcScan.Scan(3000);
                foreach (Svc s in cs) if (s.Connect && s.Addr == ip) { target = s.End; break; }
            }
            catch { }
            if (target == null) target = ip + ":5555";
            string o2 = Adb.A("connect " + target, 10000);
            Log("connect " + ip + ":5555 → " + o2.Trim());
            Thread.Sleep(1000);
            List<Dev> devs = Adb.Devices(8000);
            Dev hit = devs.FirstOrDefault(delegate(Dev d) { return d.State == "device" && d.Wifi; });
            BeginInvoke(new Action(delegate
            {
                if (hit != null)
                {
                    SetStatus("配對成功：" + hit.Serial + "（已記住，下次開機自動連）");
                    lblQrState.Text = "配對成功！之後開 App 會自動連線。";
                    lblPairSvc.Text = "已記住 " + hit.Serial;
                    SaveKnown(hit.Serial);
                    DoScan(true);
                }
                else
                {
                    SetStatus("配對成功但未連上 — 手機無線偵錯可能熄咗，或者要再掃一次");
                }
            }));
        }

        // ---------------- USB → 無線 ----------------
        private void UsbToWireless()
        {
            List<Dev> devs = new List<Dev>();
            try { devs = Adb.Devices(8000); } catch { }
            Dev usb = devs.FirstOrDefault(delegate(Dev d) { return !d.Wifi && d.State == "device"; });
            if (usb == null)
            {
                MessageBox.Show(this, "未見到 USB 連住嘅手機。\n插線 + 手機彈「允許 USB 偵錯」按允許，再按一次。", "USB 轉無線",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string serial = usb.Serial;
            SetStatus("USB 轉無線中…");
            Task.Factory.StartNew(delegate
            {
                string o1 = Adb.A("-s " + serial + " tcpip 5555", 12000);
                Log("tcpip 5555 → " + o1.Trim());
                Thread.Sleep(2500);
                string ip = WifiIp(serial);
                if (ip == null)
                {
                    Log("自動攞 IP 失敗，改試 ip addr");
                    string o2 = Adb.A("-s " + serial + " shell ip -f inet addr show wlan0", 8000);
                    Match m = Regex.Match(o2, @"inet\s+(\d+\.\d+\.\d+\.\d+)");
                    if (m.Success) ip = m.Groups[1].Value;
                }
                if (ip == null)
                {
                    BeginInvoke(new Action(delegate { SetStatus("攞唔到手機 IP — 可以照用手機 Wi-Fi 偵錯畫面嘅位址"); }));
                    return;
                }
                string ep = ip + ":5555";
                string o3 = Adb.A("connect " + ep, 10000);
                Log("connect " + ep + " → " + o3.Trim());
                Thread.Sleep(1200);
                List<Dev> now = Adb.Devices(8000);
                bool ok = now.Any(delegate(Dev d) { return d.Serial == ep && d.State == "device"; });
                if (ok) SaveKnown(ep);
                BeginInvoke(new Action(delegate
                {
                    if (ok)
                    {
                        SetStatus("已轉無線：" + ep + " — 而家可以拔 USB 線");
                        MessageBox.Show(this, "已轉無線：" + ep + "\n\n而家可以拔 USB 線，之後開 App 會自動連。", "USB 轉無線",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else SetStatus("轉無線未成功 — 睇記錄");
                    DoScan(true);
                }));
            });
        }

        private string WifiIp(string serial)
        {
            string o = Adb.A("-s " + serial + " shell ip route", 8000);
            foreach (string raw in o.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.IndexOf("wlan") < 0) continue;
                string[] p = line.Split(' ');
                if (p.Length > 0 && Regex.IsMatch(p[0], @"^\d+\.\d+\.\d+\.\d+$")) return p[0];
                Match m = Regex.Match(line, @"src\s+(\d+\.\d+\.\d+\.\d+)");
                if (m.Success) return m.Groups[1].Value;
            }
            return null;
        }

        // ---------------- 投屏 ----------------
        private void Cast()
        {
            if (scr != null && !scr.HasExited) { SetStatus("已經投屏中"); return; }
            string target = null;
            if (lv.SelectedItems.Count > 0) target = (string)lv.SelectedItems[0].Tag;
            List<Dev> devs = new List<Dev>();
            try { devs = Adb.Devices(8000); } catch { }
            bool okDev = devs.Any(delegate(Dev d) { return d.State == "device" && d.Serial == target; });
            if (!okDev)
            {
                Dev only = devs.FirstOrDefault(delegate(Dev d) { return d.State == "device"; });
                if (only != null) target = only.Serial;
            }
            if (target == null)
            {
                MessageBox.Show(this, "未連到手機。先配對或插 USB。", "投屏", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (Adb.ScrcpyDir == null)
            {
                MessageBox.Show(this, "搵唔到 scrcpy.exe，請先安裝：winget install Genymobile.scrcpy", "投屏",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // 未連線嘅就先連（例如 mDNS 未自動連）
            if (!okDev) Adb.A("connect " + target, 8000);

            string args = "-s " + target;
            if (cmbBitrate.SelectedIndex > 0) args += " --video-bit-rate=" + cmbBitrate.SelectedItem.ToString();
            if (cmbMax.SelectedIndex > 0) args += " --max-size=" + cmbMax.SelectedItem.ToString();
            if (chkStay.Checked) args += " --stay-awake";
            if (chkFs.Checked) args += Core.PlacementArgs();
            if (chkFill.Checked) args += Core.CropArgs();
            if (chkUhid.Checked) args += " --mouse=uhid --keyboard=uhid";
            args += " --window-title=\"無線投屏 " + target + "\"";

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Adb.ScrcpyPath, args);
                psi.WorkingDirectory = Adb.ScrcpyDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                scr = new Process();
                scr.StartInfo = psi;
                scr.EnableRaisingEvents = true;
                scr.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Adb.Log.Write("scrcpy: " + e.Data); };
                scr.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Adb.Log.Write("scrcpy: " + e.Data); };
                scr.Exited += delegate { Adb.Log.Write("scrcpy 已結束"); };
                scr.Start();
                scr.BeginOutputReadLine();
                scr.BeginErrorReadLine();
                Log("投屏啟動：" + target + "  " + args);
                SetStatus("投屏中：" + target + "（關閉 scrcpy 視窗即停）");
                SaveKnown(target);
            }
            catch (Exception ex)
            {
                Log("啟動 scrcpy 失敗: " + ex.Message);
                MessageBox.Show(this, "啟動 scrcpy 失敗：\n" + ex.Message, "投屏", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---------------- 手機 APK 叫投屏（Agent 用）----------------
        private string EnsureDevice()
        {
            List<Dev> devs = new List<Dev>();
            try { devs = Adb.Devices(8000); } catch { }
            Dev d = devs.FirstOrDefault(delegate(Dev x) { return x.State == "device"; });
            if (d != null) return d.Serial;
            // 試已知位址
            foreach (string ep in new List<string>(known))
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
            // 試 mDNS 上嘅連線服務
            try
            {
                List<Svc> svcs = SvcScan.Scan(2500);
                foreach (Svc s in svcs)
                {
                    if (!s.Connect) continue;
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

        private bool StartScrcpy(string target)
        {
            string args = "-s " + target;
            string br = null, mx = null; bool stay = true;
            try
            {
                Invoke(new Action(delegate
                {
                    if (cmbBitrate.SelectedIndex > 0) br = cmbBitrate.SelectedItem.ToString();
                    if (cmbMax.SelectedIndex > 0) mx = cmbMax.SelectedItem.ToString();
                    stay = chkStay.Checked;
                }));
            }
            catch { }
            if (Adb.ScrcpyDir == null) return false;
            if (br != null) args += " --video-bit-rate=" + br;
            if (mx != null) args += " --max-size=" + mx;
            if (stay) args += " --stay-awake";
            args += Core.PlacementArgs();
            args += Core.CropArgs();
            args += " --window-title=\"無線投屏 " + target + "\"";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Adb.ScrcpyPath, args);
                psi.WorkingDirectory = Adb.ScrcpyDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                scr = new Process();
                scr.StartInfo = psi;
                scr.EnableRaisingEvents = true;
                scr.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Adb.Log.Write("scrcpy: " + e.Data); };
                scr.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Adb.Log.Write("scrcpy: " + e.Data); };
                scr.Exited += delegate { Adb.Log.Write("scrcpy 已結束"); };
                scr.Start();
                scr.BeginOutputReadLine();
                scr.BeginErrorReadLine();
                Log("投屏啟動：" + target + "  " + args);
                SaveKnown(target);
                return true;
            }
            catch (Exception ex)
            {
                Log("啟動 scrcpy 失敗: " + ex.Message);
                return false;
            }
        }

        private string CastFromPhone()
        {
            try
            {
                if (scr != null && !scr.HasExited) return "already";
                string target = EnsureDevice();
                if (target == null) return "no_device";
                if (!StartScrcpy(target)) return "scrcpy_fail";
                SetStatus("手機叫投屏中：" + target);
                return "ok " + target;
            }
            catch (Exception ex) { return "error " + ex.Message; }
        }

        private string DeviceInfo()
        {
            try
            {
                List<Dev> devs = Adb.Devices(5000);
                Dev d = devs.FirstOrDefault(delegate(Dev x) { return x.State == "device"; });
                if (d == null) return "offline";
                return d.Serial;
            }
            catch { return "offline"; }
        }

        private string StatusText()
        {
            string sc = (scr != null && !scr.HasExited) ? "casting" : "idle";
            return "ok " + sc + " " + DeviceInfo();
        }

        private string HandleAgentRequest(string path)
        {
            string p = path == null ? "/" : path.Trim();
            int q = p.IndexOf('?');
            if (q >= 0) p = p.Substring(0, q);
            p = p.TrimStart('/').ToLower();
            if (p == "ping") return "ok androidcast " + Agent.Version + " scrcpy=" + (Adb.ScrcpyDir != null ? "ready" : "no");
            if (p == "status") return StatusText();
            if (p == "cast") return CastFromPhone();
            if (p == "stop")
            {
                StopCast();
                return "stopped";
            }
            return "unknown_command";
        }

        private void StopCast()
        {
            try
            {
                if (scr != null && !scr.HasExited)
                {
                    scr.Kill();
                    Log("已停止投屏");
                    SetStatus("已停止投屏");
                }
            }
            catch { }
        }
    }

    // ============================================================ mDNS 服務掃描（adb 內建 + 自帶）
    internal static class SvcScan
    {
        public static List<Svc> Scan(int ms)
        {
            List<Svc> list = new List<Svc>();
            try { list.AddRange(Adb.Mdns(ms)); } catch { }
            try
            {
                string[] types = new string[] { "_adb-tls-pairing._tcp.local", "_adb-tls-connect._tcp.local", "_adb._tcp.local" };
                List<Svc> mine = MiniMdns.Browse(types, ms < 1500 ? 1800 : ms);
                foreach (Svc s in mine)
                {
                    bool dup = false;
                    foreach (Svc x in list) if (x.End == s.End) dup = true;
                    if (!dup)
                    {
                        list.Add(s);
                        Adb.Log.Write("自帶 mDNS 發現 " + s.Name + " [" + s.Type + "] " + s.End);
                    }
                }
            }
            catch { }
            return list;
        }
    }

    // ============================================================ 6 位碼配對對話框
    internal class PairDialog : Form
    {
        private ComboBox cb;
        private TextBox tb;
        private List<Svc> pairs;
        public string EndPoint;
        public string Code;

        public PairDialog(List<Svc> list, string prefill)
        {
            pairs = list;
            Text = "用 6 位配對碼配對";
            ClientSize = new Size(430, 210);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Microsoft JhengHei UI", 9F);

            Label l1 = new Label();
            l1.Text = "手機配對服務（自動搵到，唔使打 IP）：";
            l1.Location = new Point(12, 12);
            l1.Size = new Size(400, 20);
            Controls.Add(l1);

            cb = new ComboBox();
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
            cb.Location = new Point(12, 36);
            cb.Size = new Size(310, 24);
            Controls.Add(cb);
            Fill();

            Button rb = new Button();
            rb.Text = "重新搵";
            rb.Location = new Point(330, 35);
            rb.Size = new Size(88, 26);
            rb.Click += delegate
            {
                List<Svc> np = SvcScan.Scan(2000).Where(delegate(Svc s) { return s.Pairing; }).ToList();
                if (np.Count > 0) pairs = np;
                Fill();
            };
            Controls.Add(rb);

            Label l2 = new Label();
            l2.Text = "手機畫面顯示嘅 6 位配對碼：";
            l2.Location = new Point(12, 74);
            l2.Size = new Size(400, 20);
            Controls.Add(l2);

            tb = new TextBox();
            tb.Location = new Point(12, 98);
            tb.Size = new Size(120, 24);
            tb.MaxLength = 6;
            tb.Text = prefill == null ? "" : prefill;
            Controls.Add(tb);

            Label l3 = new Label();
            l3.Text = "手機：無線偵錯 → 使用配對碼配對裝置（要留住嗰個畫面）";
            l3.Location = new Point(12, 128);
            l3.Size = new Size(410, 34);
            l3.ForeColor = Color.DimGray;
            Controls.Add(l3);

            Button ok = new Button();
            ok.Text = "配對";
            ok.Location = new Point(228, 168);
            ok.Size = new Size(90, 28);
            ok.DialogResult = DialogResult.OK;
            ok.Click += delegate
            {
                if (cb.SelectedIndex < 0) { MessageBox.Show(this, "未搵到配對服務：請喺手機開「使用配對碼配對裝置」畫面再按「重新搵」。"); return; }
                if (tb.Text.Trim().Length < 6) { MessageBox.Show(this, "請輸入 6 位配對碼。"); return; }
                Svc s = pairs[cb.SelectedIndex];
                EndPoint = s.End;
                Code = tb.Text.Trim();
            };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Location = new Point(326, 168);
            cancel.Size = new Size(90, 28);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void Fill()
        {
            cb.Items.Clear();
            foreach (Svc s in pairs) cb.Items.Add(s.End + "   (" + s.Name + ")");
            if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        }
    }
}

// 手機 APK 控制服務：UDP 廣播搵電腦 + TCP(HTTP) 收指令開 scrcpy
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AndroidCast
{
    internal static class Agent
    {
        public const int UdpPort = 8766;
        public static int HttpPort = 8765;
        public static Func<string, string> Handler;
        public static bool Running;
        public static string Version = "1.0";

        public static void Start(Func<string, string> handler)
        {
            Handler = handler;
            Running = true;
            Thread tu = new Thread(UdpLoop);
            tu.IsBackground = true;
            tu.Start();
            Thread th = new Thread(HttpLoop);
            th.IsBackground = true;
            th.Start();
        }

        private static void UdpLoop()
        {
            Socket s = null;
            try
            {
                s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                s.Bind(new IPEndPoint(IPAddress.Any, UdpPort));
                byte[] buf = new byte[1024];
                while (Running)
                {
                    EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    int n;
                    try { n = s.ReceiveFrom(buf, ref ep); }
                    catch { continue; }
                    string msg = Encoding.UTF8.GetString(buf, 0, n).Trim();
                    if (msg.StartsWith("ANDROIDCAST-DISCOVER"))
                    {
                        string reply = "ANDROIDCAST-PC " + HttpPort + " " + Environment.MachineName + " " +
                                       (Adb.ScrcpyDir != null ? "ready" : "noscrcpy");
                        try
                        {
                            byte[] rb = Encoding.UTF8.GetBytes(reply);
                            s.SendTo(rb, ep);
                            Adb.Log.Write("手機 UDP 搵上門：" + ((IPEndPoint)ep).Address + " → " + reply);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Adb.Log.Write("UDP 服務啟動失敗：" + ex.Message); }
            finally { if (s != null) { try { s.Close(); } catch { } } }
        }

        private static void HttpLoop()
        {
            TcpListener lis = null;
            try
            {
                lis = new TcpListener(IPAddress.Any, HttpPort);
                lis.Start();
            }
            catch (Exception ex)
            {
                Adb.Log.Write("手機控制埠 " + HttpPort + " 開唔到（可能已經有另一個實例／後台版喺度行）：" + ex.Message);
                return;
            }
            Adb.Log.Write("手機控制服務已開：TCP " + HttpPort + " / UDP " + UdpPort);
            while (Running)
            {
                TcpClient c = null;
                try { c = lis.AcceptTcpClient(); }
                catch { continue; }
                try { Handle(c); }
                catch { }
                finally { try { c.Close(); } catch { } }
            }
        }

        private static void Handle(TcpClient c)
        {
            c.ReceiveTimeout = 4000;
            NetworkStream ns = c.GetStream();
            byte[] buf = new byte[4096];
            int n = ns.Read(buf, 0, buf.Length);
            string req = n > 0 ? Encoding.UTF8.GetString(buf, 0, n) : "";
            string[] lines = req.Split('\n');
            string line = lines.Length > 0 ? lines[0].Trim() : "";
            string[] parts = line.Split(' ');
            string path = parts.Length > 1 ? parts[1] : "/";
            string from = "?";
            try { from = ((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString(); }
            catch { }
            string body;
            try { body = Handler == null ? "error no-handler" : Handler(path); }
            catch (Exception ex) { body = "error " + ex.Message; }
            Adb.Log.Write("手機指令 " + path + " ← " + from + " → " + body.Replace("\r", " ").Replace("\n", " | "));
            byte[] bb = Encoding.UTF8.GetBytes(body + "\n");
            string hdr = "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: " + bb.Length +
                         "\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n";
            byte[] hb = Encoding.UTF8.GetBytes(hdr);
            ns.Write(hb, 0, hb.Length);
            ns.Write(bb, 0, bb.Length);
            ns.Flush();
        }
    }
}

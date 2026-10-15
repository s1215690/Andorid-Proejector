// 電腦音量控制（Windows Core Audio API；失敗會退回用音量鍵）
using System;
using System.Runtime.InteropServices;

namespace AndroidCast
{
    internal static class Vol
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }

        private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
        private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IntPtr ppDevices);
            int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
            int GetDevice(string pwstrId, out IMMDevice ppDevice);
            int RegisterEndpointNotificationCallback(IntPtr pClient);
            int UnregisterEndpointNotificationCallback(IntPtr pClient);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
                         [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
            int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
            int GetState(out int pdwState);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr pNotify);
            int UnregisterControlChangeNotify(IntPtr pNotify);
            int GetChannelCount(out int pnChannelCount);
            int SetMasterVolumeLevel(float fLevelDB, IntPtr pguidEventContext);
            int SetMasterVolumeLevelScalar(float fLevel, IntPtr pguidEventContext);
            int GetMasterVolumeLevel(out float pfLevelDB);
            int GetMasterVolumeLevelScalar(out float pfLevel);
            int SetChannelVolumeLevel(int nChannel, float fLevelDB, IntPtr pguidEventContext);
            int SetChannelVolumeLevelScalar(int nChannel, float fLevel, IntPtr pguidEventContext);
            int GetChannelVolumeLevel(int nChannel, out float pfLevelDB);
            int GetChannelVolumeLevelScalar(int nChannel, out float pfLevel);
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, IntPtr pguidEventContext);
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        }

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

        private const byte VK_VOL_UP = 0xAF;
        private const byte VK_VOL_DOWN = 0xAE;
        private const byte VK_VOL_MUTE = 0xAD;

        private static string lastErr = null;

        private static IAudioEndpointVolume Open()
        {
            IMMDeviceEnumerator en = (IMMDeviceEnumerator)(new MMDeviceEnumerator());
            IMMDevice dev = null;
            int hr = en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out dev);
            if (hr != 0 || dev == null) throw new Exception("GetDefaultAudioEndpoint 0x" + hr.ToString("X8"));
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            object o = null;
            hr = dev.Activate(ref iid, 23, IntPtr.Zero, out o);   // CLSCTX_ALL
            if (hr != 0 || o == null) throw new Exception("Activate 0x" + hr.ToString("X8"));
            return (IAudioEndpointVolume)o;
        }

        private static void Note(string msg)
        {
            if (msg == lastErr) return;
            lastErr = msg;
            Core.Log("音量：由核心音訊 API 轉用音量鍵（" + msg + "）");
        }

        public static int Get(out bool mute)
        {
            mute = false;
            try
            {
                IAudioEndpointVolume v = Open();
                float f;
                v.GetMasterVolumeLevelScalar(out f);
                bool m;
                v.GetMute(out m);
                mute = m;
                return (int)Math.Round(f * 100.0);
            }
            catch (Exception ex)
            {
                Note(ex.Message);
                return -1;
            }
        }

        public static bool Set(int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            try
            {
                IAudioEndpointVolume v = Open();
                v.SetMasterVolumeLevelScalar((float)(percent / 100.0), IntPtr.Zero);
                return true;
            }
            catch (Exception ex)
            {
                Note(ex.Message);
                return false;
            }
        }

        public static bool Mute(bool m)
        {
            try
            {
                IAudioEndpointVolume v = Open();
                v.SetMute(m, IntPtr.Zero);
                return true;
            }
            catch (Exception ex)
            {
                Note(ex.Message);
                keybd_event(VK_VOL_MUTE, 0, 0, IntPtr.Zero);
                keybd_event(VK_VOL_MUTE, 0, 2, IntPtr.Zero);
                return false;
            }
        }

        // 加減（核心 API 唔通就用音量鍵，Windows 預設每格 2%）
        public static int Step(int delta)
        {
            bool m;
            int cur = Get(out m);
            if (cur >= 0) { Set(cur + delta); return cur + delta; }
            int n = Math.Abs(delta) / 2;
            if (n < 1) n = 1;
            for (int i = 0; i < n; i++)
            {
                byte k = delta > 0 ? VK_VOL_UP : VK_VOL_DOWN;
                keybd_event(k, 0, 0, IntPtr.Zero);
                keybd_event(k, 0, 2, IntPtr.Zero);
            }
            return -1;
        }
    }
}

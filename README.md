# 一鍵投屏 · Android Projector

手機撳一下 → 電腦自動彈 scrcpy 投屏（默認全屏開喺豎屏副螢幕），電腦鼠標／鍵盤直接操控手機。
電腦端係**常駐後台**（開機自動、完全冇視窗），手機端係一個 APK。

## 架構

```
手機 APK (一鍵投屏)                    電腦常駐後台 (無線投屏.exe --bg)
  │  UDP 廣播 ANDROIDCAST-DISCOVER ──────►  UDP 8766 回應「ANDROIDCAST-PC <port> <機名>」
  │  HTTP GET /cast ─────────────────────►  TCP 8765  收到 → 確保 adb 連住手機 → 開 scrcpy
  └─ /status /stop /ping /quit            └─ 同時 keepalive：自帶 mDNS 掃 _adb-tls-connect 自動 adb connect
```

- **自帶 mDNS 查詢器**：本機 adb 內建 mDNS（`adb mdns services`）會揀錯網卡而完全搵唔到裝置，
  所以程式自己用 UDP 5353 廣播查詢（`IP_ADD_MEMBERSHIP` + 綁 Wi-Fi 介面）解析 SRV/A 記錄。
- **自寫 QR 編碼器**（C#，零依賴）：用嚟做 adb 無線配對，QR 內容格式 `WIFI:T:ADB;S:<名>;P:<6位碼>;;`。
- 投屏本身交畀 [scrcpy](https://github.com/Genymobile/scrcpy)（需要先安裝）。

## 快速開始

1. 電腦裝好 scrcpy：`winget install Genymobile.scrcpy`（adb 用 Android platform-tools）
2. 電腦跑後台：`無線投屏.exe --bg`（或者雙擊有介面版做第一次配對）
3. 手機裝 `android/apk/OneTapCast-v1.3.apk`
4. 第一次配對（兩者揀一）：
   - 有介面版按「① 產生 QR 碼」→ 手機 開發者選項 → 無線偵錯 → 使用 QR 碼配對裝置 → 掃碼
   - 或者插 USB 一次 → 按「USB 轉無線」
5. 之後手機開 App 撳大掣（或下拉快捷面板嘅「一鍵投屏」磚）→ 電腦自動開投屏

## 操控（鼠標移入投屏窗時）

| 動作 | 效果 |
|---|---|
| 左鍵 | 撳 |
| 拖 | 滑 |
| 右鍵 | 返回 |
| 中鍵 | Home |
| 滾輪 | 捲動 |
| 直接打字 | 手機鍵盤輸入（中文用返手機自己輸入法）|

鼠標**唔會被鎖死**（默認 SDK 模式），隨時移出去返桌面。

## 小米／HyperOS 必開

| 開關 | 位置 | 為咩 |
|---|---|---|
| 無線偵錯 | 開發者選項 | 連線用 |
| **USB 偵錯（安全設定）** | 開發者選項 | **唔開就只可以睇，鼠標／鍵盤控制唔到**（scrcpy 會報 INJECT_EVENTS）|
| 透過 USB 安裝 | 開發者選項 | 唔開 `adb install` 會被擋（INSTALL_FAILED_USER_RESTRICTED）|

「USB 偵錯（安全設定）」開完要重啟手機；之後 `pm grant` 都通，程式可以幫你自動開返「無線偵錯」。

## 目錄

```
pc/            C# 電腦端（main.cs = GUI、core.cs = 後台、agent.cs = UDP/TCP 服務、
               qr.cs = QR 編碼器、mdns.cs = mDNS 查詢器）
pc/bin/        已編譯 exe（無線投屏.exe = AndroidCast.exe 同一支）
pc/tools/      mDNS 探測、QR 配對 watcher、輸入測試等開發小工具
android/       Kotlin APK 專案（Gradle）
android/apk/   已建置 APK
docs/          使用說明
```

## 建置

電腦端（Windows，.NET Framework 4.x 內建編譯器）：

```bat
csc /target:winexe /out:Cast.exe /win32icon:icon.ico ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll ^
    main.cs qr.cs mdns.cs agent.cs core.cs
```

手機端（Gradle 8.7 + AGP 8.5.2 + Kotlin 1.9.24，記得自己寫 `local.properties` 指去 Android SDK）：

```bash
export JAVA_HOME="C:/Program Files/Android/Android Studio/jbr"
gradle assembleDebug
```

## 備註

- 個人自用專案，冇上架、冇 release 簽名（APK 係 debug 簽名）。
- 手機重開機後「無線偵錯」會自動熄，要開返一次（有 grant 權限時程式會自己開）。

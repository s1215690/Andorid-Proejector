# 一鍵投屏 · Android Projector

手機撳一下 → 電腦自動彈 scrcpy 投屏（直屏落豎屏副螢幕、橫屏落主螢幕，全屏填滿），電腦鼠標／鍵盤直接操控手機。
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

- 投屏窗自動揀螢幕：**直屏 → 豎屏副螢幕**（實測 2560,0 1080x1920）、**橫屏 → 橫放主螢幕**（0,0 2048x1152）；
  投屏期間手機一轉向就**即時搬位**（scrcpy 報 Texture 尺寸變 → SetWindowPos 搬窗，
  **唔會重開 scrcpy、唔會黑一下**，實測 0.35～2.8 秒到位、PID 全程不變）。
- 「夾手機解析度」會叫手機改用螢幕比例排版（9:20 → 9:16），
  畫面填滿之餘狀態列／底部 dock 都睇得齊，停止投屏自動 `wm size reset` 還原 9:20；
  比例已經一樣時唔會再裁切（舊 bug：橫屏照用直屏裁切 → 畫面被夾成 608x1080 窄條）。
- 鼠標**唔會被鎖死**（默認 SDK 模式），隨時移出去返桌面。
- 手機 App 可以叫**電腦關機**（60 秒緩衝、長按掣取消）；電腦端「允許手機關機」可隨時剔走，
  剔走之後 `/ping` 回 `shutdown=off`、手機嗰粒掣變灰。
- 手機 App 開關都會**即時同步電腦**：顯示關機掣、橫屏自動搬主螢幕、**手機排版 9:20 原裝／9:16 填滿**
  （`/cast?move=&fit=`、`/setmove?on=`、`/setfit?on=`，並寫入 `settings.txt`；電腦端 GUI 亦有對應選項）。
- **9:16 填滿** = 投屏時 `wm size` 叫手機對齊螢幕比例（狀態列＋底部 dock 齊、完全填滿）；
  **9:20 原裝** = 唔改手機、亦唔裁切，完整睇到手機上下（螢幕會有黑邊）。

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

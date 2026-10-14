# 安卓無線投屏 — adb 無線偵錯 + scrcpy
$ErrorActionPreference = 'Continue'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch {}

$adb    = 'C:\Users\s1215\AppData\Local\Android\Sdk\platform-tools\adb.exe'
$scrcpy = 'C:\Users\s1215\AppData\Local\Microsoft\WinGet\Packages\Genymobile.scrcpy_Microsoft.Winget.Source_8wekyb3d8bbwe\scrcpy-win64-v4.1\scrcpy.exe'
$log    = Join-Path $PSScriptRoot 'cast.log'

function Get-Devices {
  $out = & $adb devices 2>&1
  $out | Select-Object -Skip 1 | ForEach-Object {
    if ("$_" -match '^(\S+)\s+(\S+)') { [pscustomobject]@{ Serial = $Matches[1]; State = $Matches[2] } }
  }
}

function Fail($msg) {
  Write-Host ""
  Write-Host "[X] $msg" -ForegroundColor Red
  if (Test-Path $log) { Write-Host "詳細記錄：$log"; Start-Process notepad $log }
  Read-Host "按 Enter 關閉"
  exit 1
}

if (-not (Test-Path $adb))    { Fail "找唔到 adb：$adb" }
if (-not (Test-Path $scrcpy)) { Fail "找唔到 scrcpy：$scrcpy" }

Write-Host "================================================"
Write-Host "   安卓無線投屏   (scrcpy 4.1 / adb 36)"
Write-Host "================================================"

& $adb start-server *>> $log

$devs  = @(Get-Devices)
$wifi  = $devs | Where-Object { $_.Serial -like '*:*'  -and $_.State -eq 'device' } | Select-Object -First 1
$usb   = $devs | Where-Object { $_.Serial -notlike '*:*' -and $_.State -eq 'device' } | Select-Object -First 1
foreach ($o in ($devs | Where-Object { $_.State -ne 'device' })) {
  Write-Host "[!] 裝置 $($o.Serial) 狀態 = $($o.State)（未授權？請喺手機按「允許」）" -ForegroundColor Yellow
}

$target = $null

if ($wifi) {
  Write-Host "[OK] 手機已用 Wi-Fi 連住：$($wifi.Serial)" -ForegroundColor Green
  $target = $wifi.Serial
}
elseif ($usb) {
  Write-Host "[OK] 見到 USB 連住嘅手機：$($usb.Serial)" -ForegroundColor Green
  Write-Host "     幫你開無線模式 (tcpip 5555)，跟住就可以拔線..."
  & $adb -s $usb.Serial tcpip 5555 *>> $log
  Start-Sleep -Seconds 2
  $ip = $null
  foreach ($pat in @('wlan0', 'wlan')) {
    $route = & $adb -s $usb.Serial shell ip route 2>$null | Where-Object { "$_" -match $pat } | Select-Object -First 1
    if ($route) { $ip = ("$route" -split '\s+')[0]; break }
  }
  if (-not $ip) {
    $r2 = & $adb -s $usb.Serial shell ip -f inet addr show wlan0 2>$null | Where-Object { "$_" -match 'inet ' } | Select-Object -First 1
    if ($r2 -and "$r2" -match 'inet\s+([0-9.]+)') { $ip = $Matches[1] }
  }
  if (-not $ip) { $ip = Read-Host "自動攞唔到手機 IP，請輸入手機 Wi-Fi IP" }
  Write-Host "     手機 IP = $ip"
  & $adb connect "${ip}:5555" *>> $log
  $target = "${ip}:5555"
  Write-Host "     >>> 而家可以拔 USB 線 <<<" -ForegroundColor Cyan
}
else {
  Write-Host "手機未連線。請確認："
  Write-Host "  1) 手機同電腦連同一個 Wi-Fi"
  Write-Host "  2) 手機 設定 > 開發者選項 > 無線偵錯 = 開"
  Write-Host ""
  $pairAddr = Read-Host "首次配對？輸入手機「使用配對碼配對裝置」畫面嘅 IP:埠（配對過就按 Enter 跳過）"
  if ($pairAddr) {
    $code = Read-Host "輸入螢幕上 6 位配對碼"
    & $adb pair $pairAddr $code
    Write-Host ""
    Write-Host "[!] 配對完成。返去「無線偵錯」主畫面睇「IP 位址與連接埠」再輸入。" -ForegroundColor Yellow
  }
  $conn = Read-Host "手機「無線偵錯」主畫面嘅 IP:埠"
  if (-not $conn) { Fail "冇輸入位址。" }
  & $adb connect $conn
  $target = $conn
}

Start-Sleep -Seconds 1
$ok = Get-Devices | Where-Object { $_.Serial -eq $target -and $_.State -eq 'device' }
if (-not $ok) { Fail "連唔上 $target — 睇 cast.log；如果電腦行緊 VPN，閂咗再試。" }

Write-Host ""
Write-Host "=== 開始投屏（關閉視窗即停）===" -ForegroundColor Cyan
& $scrcpy -s $target --stay-awake --video-bit-rate=8M "--window-title=無線投屏 $target"
if ($LASTEXITCODE -ne 0) { Fail "scrcpy 退出碼 $LASTEXITCODE" }
Write-Host "投屏已結束。"
Read-Host "按 Enter 關閉"

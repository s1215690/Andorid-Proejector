$folder = Join-Path $env:USERPROFILE 'android-cast'
$exe = Join-Path $folder 'AndroidCast.exe'
$startup = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
$w = New-Object -ComObject WScript.Shell

# 1) 開機自動：後台模式（無視窗）
$lnk1 = Join-Path $startup '無線投屏-後台.lnk'
$s1 = $w.CreateShortcut($lnk1)
$s1.TargetPath = $exe
$s1.Arguments = '--bg'
$s1.WorkingDirectory = $folder
$s1.IconLocation = $exe
$s1.Description = '無線投屏 常駐後台（開機自動、冇視窗）'
$s1.Save()
Remove-Item (Join-Path $startup '無線投屏.lnk') -ErrorAction SilentlyContinue

# 2) 一鍵關掉後台（桌面 + 開始功能表都有）
foreach ($dir in @([Environment]::GetFolderPath('Desktop'), (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'))) {
    $lnk2 = Join-Path $dir '關閉無線投屏後台.lnk'
    $s2 = $w.CreateShortcut($lnk2)
    $s2.TargetPath = $exe
    $s2.Arguments = '--quit'
    $s2.WorkingDirectory = $folder
    $s2.IconLocation = $exe
    $s2.Description = '關掉無線投屏後台'
    $s2.Save()
}

"startup: " + (Test-Path $lnk1)
"desktop quit: " + (Test-Path (Join-Path ([Environment]::GetFolderPath('Desktop')) '關閉無線投屏後台.lnk'))

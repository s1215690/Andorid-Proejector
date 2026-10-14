param(
    [string]$titleLike = "FSTEST",
    [ValidateSet("none", "swipedown", "tapcenter")][string]$action = "none"
)
Add-Type -Namespace WC -Name U -MemberDefinition '
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, int e);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, int extra);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
public struct RECT { public int L, T, R, B; }
public struct POINT { public int X, Y; }'

$p = Get-Process scrcpy -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like "*$titleLike*" } | Select-Object -First 1
if (-not $p) { "搵唔到 scrcpy 視窗（$titleLike）"; exit 1 }

$w = New-Object WC.U+RECT
[void][WC.U]::GetWindowRect($p.MainWindowHandle, [ref]$w)
"視窗外框 = $($w.L),$($w.T) → $($w.R),$($w.B)  尺寸 $($w.R-$w.L)x$($w.B-$w.T)"

# ALT trick：先模擬按一下 ALT，令本程序有資格搶前景
[WC.U]::keybd_event(0x12, 0, 0, 0)
[WC.U]::keybd_event(0x12, 0, 2, 0)
Start-Sleep -Milliseconds 150
[void][WC.U]::SetForegroundWindow($p.MainWindowHandle)
Start-Sleep -Milliseconds 600
$fg = [WC.U]::GetForegroundWindow()
"前景視窗 = scrcpy？ " + ($fg -eq $p.MainWindowHandle)

if ($action -ne "none") {
    $c = New-Object WC.U+RECT
    [void][WC.U]::GetClientRect($p.MainWindowHandle, [ref]$c)
    $o = New-Object WC.U+POINT
    $o.X = 0; $o.Y = 0
    [void][WC.U]::ClientToScreen($p.MainWindowHandle, [ref]$o)
    $left = $o.X; $top = $o.Y; $right = $o.X + $c.R; $bottom = $o.Y + $c.B
    $cx = [int](($left + $right) / 2)
    "內容區 = $left,$top → $right,$bottom"
    if ($action -eq "swipedown") {
        [void][WC.U]::SetCursorPos($cx, ($top + 15)); Start-Sleep -Milliseconds 120
        [WC.U]::mouse_event(0x0002, 0, 0, 0, 0)
        for ($y = $top + 15; $y -lt ($top + 800); $y += 15) { [void][WC.U]::SetCursorPos($cx, $y); Start-Sleep -Milliseconds 25 }
        [WC.U]::mouse_event(0x0004, 0, 0, 0, 0)
        "已由內容區頂部向下滑"
    } else {
        [void][WC.U]::SetCursorPos([int](($left + $right) / 2), [int](($top + $bottom) / 2)); Start-Sleep -Milliseconds 120
        [WC.U]::mouse_event(0x0002, 0, 0, 0, 0); Start-Sleep -Milliseconds 110
        [WC.U]::mouse_event(0x0004, 0, 0, 0, 0)
        "已點內容區中心"
    }
}
Start-Sleep -Milliseconds 300

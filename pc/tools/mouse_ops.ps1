param(
    [string]$titleLike = "SDK",
    [ValidateSet("where", "tap", "swipedown", "swipeup")][string]$mode = "where"
)
Add-Type -Namespace WB -Name U -MemberDefinition '
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, int e);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
public struct RECT { public int L, T, R, B; }
public struct POINT { public int X, Y; }'

$p = Get-Process scrcpy -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like "*$titleLike*" } | Select-Object -First 1
if (-not $p) { "no scrcpy window matching '$titleLike'"; exit 1 }
$w = New-Object WB.U+RECT
[void][WB.U]::GetWindowRect($p.MainWindowHandle, [ref]$w)
$c = New-Object WB.U+RECT
[void][WB.U]::GetClientRect($p.MainWindowHandle, [ref]$c)
$o = New-Object WB.U+POINT
$o.X = 0; $o.Y = 0
[void][WB.U]::ClientToScreen($p.MainWindowHandle, [ref]$o)
$left = $o.X; $top = $o.Y; $right = $o.X + $c.R; $bottom = $o.Y + $c.B
$cx = [int](($left + $right) / 2)
"視窗 '$($p.MainWindowTitle)' 內容區 $left,$top,$right,$bottom 中心 x=$cx (標題列 $($top - $w.T) px)"

[void][WB.U]::SetForegroundWindow($p.MainWindowHandle)
Start-Sleep -Milliseconds 700

function MoveTo([int]$x, [int]$y) { [void][WB.U]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 50 }

switch ($mode) {
    "where" { MoveTo $cx ([int](($top + $bottom) / 2)); "鼠標移到內容區中心" }
    "tap" {
        MoveTo $cx ([int](($top + $bottom) / 2))
        [WB.U]::mouse_event(0x0002, 0, 0, 0, 0); Start-Sleep -Milliseconds 110
        [WB.U]::mouse_event(0x0004, 0, 0, 0, 0); "已點擊內容區中心"
    }
    "swipedown" {
        MoveTo $cx ($top + 15)
        [WB.U]::mouse_event(0x0002, 0, 0, 0, 0)
        for ($y = $top + 15; $y -lt ($top + 800); $y += 20) { MoveTo $cx $y }
        [WB.U]::mouse_event(0x0004, 0, 0, 0, 0); "已由內容區頂部向下滑"
    }
    "swipeup" {
        MoveTo $cx ($bottom - 15)
        [WB.U]::mouse_event(0x0002, 0, 0, 0, 0)
        for ($y = $bottom - 15; $y -gt ($top + 200); $y -= 20) { MoveTo $cx $y }
        [WB.U]::mouse_event(0x0004, 0, 0, 0, 0); "已由底部向上滑"
    }
}
Start-Sleep -Milliseconds 300

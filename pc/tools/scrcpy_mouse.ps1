param(
    [string]$titleLike = "UHID",
    [ValidateSet("move", "tap", "swipe", "swipedown")][string]$mode = "move"
)
Add-Type -Namespace W9 -Name U -MemberDefinition '
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, int e);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
public struct RECT { public int L, T, R, B; }'

$p = Get-Process scrcpy -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like "*$titleLike*" } | Select-Object -First 1
if (-not $p) { "no scrcpy window matching '$titleLike'"; exit 1 }
$r = New-Object W9.U+RECT
[void][W9.U]::GetWindowRect($p.MainWindowHandle, [ref]$r)
[void][W9.U]::SetForegroundWindow($p.MainWindowHandle)
Start-Sleep -Milliseconds 800
$cx = [int](($r.L + $r.R) / 2)
$top = $r.T + 12
"window '$($p.MainWindowTitle)' rect=$($r.L),$($r.T),$($r.R),$($r.B) center=$cx"

function MoveTo([int]$x, [int]$y) { [void][W9.U]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 60 }

switch ($mode) {
    "move" { MoveTo $cx ([int](($r.T + $r.B) / 2)); "已移鼠標入視窗中心" }
    "tap" {
        MoveTo $cx ([int](($r.T + $r.B) / 2))
        [W9.U]::mouse_event(0x0002, 0, 0, 0, 0); Start-Sleep -Milliseconds 120
        [W9.U]::mouse_event(0x0004, 0, 0, 0, 0)
        "已點擊視窗中心"
    }
    "swipedown" {
        MoveTo $cx $top
        [W9.U]::mouse_event(0x0002, 0, 0, 0, 0)
        for ($y = $top; $y -lt ($r.T + 450); $y += 25) { MoveTo $cx $y }
        [W9.U]::mouse_event(0x0004, 0, 0, 0, 0)
        "已由頂部向下滑"
    }
    "swipe" {
        MoveTo $cx ([int](($r.T + $r.B) / 2))
        [W9.U]::mouse_event(0x0002, 0, 0, 0, 0)
        for ($x = $cx; $x -gt ($r.L + 60); $x -= 25) { MoveTo $x ([int](($r.T + $r.B) / 2)) }
        [W9.U]::mouse_event(0x0004, 0, 0, 0, 0)
        "已向左滑"
    }
}
Start-Sleep -Milliseconds 400

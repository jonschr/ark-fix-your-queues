param([double]$X, [double]$Y)
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AsaClick {
    public delegate bool Callback(IntPtr h, IntPtr state);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(Callback callback, IntPtr state);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
}
'@
$process = Get-Process -Name ArkAscended -ErrorAction Stop | Select-Object -First 1
$best = [IntPtr]::Zero; $bestRect = New-Object AsaClick+Rect; $bestArea = 0L
$callback = [AsaClick+Callback]{ param($window,$state)
    if (-not [AsaClick]::IsWindowVisible($window)) { return $true }
    [uint32]$owner=0; [void][AsaClick]::GetWindowThreadProcessId($window,[ref]$owner)
    if ($owner -ne $process.Id) { return $true }
    $rect=New-Object AsaClick+Rect; if(-not [AsaClick]::GetWindowRect($window,[ref]$rect)){return $true}
    $area=[int64]([Math]::Max(0,$rect.Right-$rect.Left))*[Math]::Max(0,$rect.Bottom-$rect.Top)
    if($area -gt $bestArea){$script:best=$window;$script:bestRect=$rect;$script:bestArea=$area}; return $true
}
[void][AsaClick]::EnumWindows($callback,[IntPtr]::Zero)
if($best -eq [IntPtr]::Zero){throw 'No visible ASA window found.'}
[void][AsaClick]::SetForegroundWindow($best); Start-Sleep -Milliseconds 250
$px=$bestRect.Left+[int](($bestRect.Right-$bestRect.Left)*$X); $py=$bestRect.Top+[int](($bestRect.Bottom-$bestRect.Top)*$Y)
[void][AsaClick]::SetCursorPos($px,$py); [AsaClick]::mouse_event(2,0,0,0,[IntPtr]::Zero); [AsaClick]::mouse_event(4,0,0,0,[IntPtr]::Zero)

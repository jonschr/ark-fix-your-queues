Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class HelperWindows {
    public delegate bool Callback(IntPtr h, IntPtr state);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(Callback callback, IntPtr state);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect rect);
}
'@
$process = Get-Process -Name ArkFixYourQueues -ErrorAction Stop | Sort-Object StartTime -Descending | Select-Object -First 1
$best=[IntPtr]::Zero;$bestRect=New-Object HelperWindows+Rect;$bestArea=0L
$callback=[HelperWindows+Callback]{param($window,$state)
 if(-not [HelperWindows]::IsWindowVisible($window)){return $true};[uint32]$owner=0;[void][HelperWindows]::GetWindowThreadProcessId($window,[ref]$owner)
 if($owner -ne $process.Id){return $true};$rect=New-Object HelperWindows+Rect;if(-not [HelperWindows]::GetWindowRect($window,[ref]$rect)){return $true}
 $area=[int64]([Math]::Max(0,$rect.Right-$rect.Left))*[Math]::Max(0,$rect.Bottom-$rect.Top);if($area -gt $bestArea){$script:best=$window;$script:bestRect=$rect;$script:bestArea=$area};return $true}
[void][HelperWindows]::EnumWindows($callback,[IntPtr]::Zero);if($best -eq [IntPtr]::Zero){throw 'No visible helper window found.'}
$bitmap=New-Object Drawing.Bitmap($($bestRect.Right-$bestRect.Left),$($bestRect.Bottom-$bestRect.Top));$graphics=[Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($bestRect.Left,$bestRect.Top,0,0,$bitmap.Size);$bitmap.Save((Join-Path $PSScriptRoot 'helper-current.png'),[Drawing.Imaging.ImageFormat]::Png);$graphics.Dispose();$bitmap.Dispose()

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AsaWindows {
    public delegate bool Callback(IntPtr h, IntPtr state);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(Callback callback, IntPtr state);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect rect);
}
'@

$process = Get-Process -Name ArkAscended -ErrorAction Stop | Select-Object -First 1
$best = [IntPtr]::Zero
$bestRect = New-Object AsaWindows+Rect
$bestArea = 0L
$callback = [AsaWindows+Callback]{
    param([IntPtr]$window, [IntPtr]$state)
    if (-not [AsaWindows]::IsWindowVisible($window)) { return $true }
    [uint32]$owner = 0
    [void][AsaWindows]::GetWindowThreadProcessId($window, [ref]$owner)
    if ($owner -ne $process.Id) { return $true }
    $rect = New-Object AsaWindows+Rect
    if (-not [AsaWindows]::GetWindowRect($window, [ref]$rect)) { return $true }
    $area = [int64]([Math]::Max(0, $rect.Right - $rect.Left)) * [Math]::Max(0, $rect.Bottom - $rect.Top)
    if ($area -gt $bestArea) { $script:best = $window; $script:bestRect = $rect; $script:bestArea = $area }
    return $true
}
[void][AsaWindows]::EnumWindows($callback, [IntPtr]::Zero)
if ($best -eq [IntPtr]::Zero) { throw 'No visible ASA window found.' }
$width = $bestRect.Right - $bestRect.Left
$height = $bestRect.Bottom - $bestRect.Top
$bitmap = New-Object System.Drawing.Bitmap($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($bestRect.Left, $bestRect.Top, 0, 0, $bitmap.Size)
$path = Join-Path $PSScriptRoot 'asa-current.png'
$bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bitmap.Dispose()

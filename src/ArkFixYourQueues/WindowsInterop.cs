using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ArkFixYourQueues;

internal static class WindowsInterop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    // INPUT's native union is 32 bytes on x64 because MOUSEINPUT is its largest member.
    // SendInput rejects cbSize when this union is allowed to shrink to KEYBDINPUT's size.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const uint Unicode = 0x0004;
    private const ushort VkOem3 = 0xC0;
    private const ushort VkReturn = 0x0D;
    private const ushort VkEscape = 0x1B;
    private const ushort VkSpace = 0x20;
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkAlt = 0x12;
    private const ushort VkA = 0x41;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extraInfo);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScan(char character);

    public static string LastInputDiagnostic { get; private set; } = "No input attempted.";

    public static Process? FindArk() => Process.GetProcessesByName("ArkAscended").FirstOrDefault();

    public static bool ActivateArk(Process process)
    {
        var window = FindLargestVisibleWindow(process.Id);
        if (window == IntPtr.Zero) return false;
        _ = ShowWindowAsync(window, 9); // SW_RESTORE
        return SetForegroundWindow(window);
    }

    public static bool ClickWindowRelative(Process process, double xRatio, double yRatio)
    {
        var window = FindLargestVisibleWindow(process.Id);
        if (window == IntPtr.Zero || !GetWindowRect(window, out var rect)) return false;
        var x = rect.Left + (int)((rect.Right - rect.Left) * Math.Clamp(xRatio, 0, 1));
        var y = rect.Top + (int)((rect.Bottom - rect.Top) * Math.Clamp(yRatio, 0, 1));
        if (!SetCursorPos(x, y)) return false;
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
        return true;
    }

    public static bool ClickWindowDesignRelative(Process process, double xRatio, double yRatio)
    {
        var window = FindLargestVisibleWindow(process.Id);
        if (window == IntPtr.Zero || !GetWindowRect(window, out var rect)) return false;
        var viewport = DesignViewport(rect.Right - rect.Left, rect.Bottom - rect.Top);
        var x = rect.Left + viewport.Left + (int)(viewport.Width * Math.Clamp(xRatio, 0, 1));
        var y = rect.Top + viewport.Top + (int)(viewport.Height * Math.Clamp(yRatio, 0, 1));
        if (!SetCursorPos(x, y)) return false;
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
        return true;
    }

    public static bool SendSpace() => SendSingleKey(VkSpace);
    public static bool SendEscape() => SendSingleKey(VkEscape);

    public static bool TypeText(string text)
    {
        foreach (var character in text)
        {
            var key = BuildPhysicalKey(character);
            if (key is null || !SendAll(key)) return false;
            Thread.Sleep(18);
        }
        return true;
    }

    public static bool ReplaceFocusedText(string text)
    {
        var selectAll = new List<Input>
        {
            Keyboard(VkControl, 0), Keyboard(VkA, 0), Keyboard(VkA, KeyUp), Keyboard(VkControl, KeyUp)
        };
        return SendAll(selectAll) && TypeText(text);
    }

    private static bool SendSingleKey(ushort key)
    {
        var inputs = new List<Input>();
        AddKey(inputs, key);
        return SendAll(inputs);
    }

    public static bool IsArkForeground(Process process)
    {
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var processId);
        return processId == process.Id;
    }

    public static Bitmap? CaptureWindow(Process process)
    {
        var window = FindLargestVisibleWindow(process.Id);
        if (window == IntPtr.Zero || !GetWindowRect(window, out var rect)) return null;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 100 || height < 100) return null;

        using var full = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(full))
        {
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, full.Size, CopyPixelOperation.SourceCopy);
        }

        const int sampleWidth = 320;
        var sampleHeight = Math.Clamp((int)Math.Round(sampleWidth * height / (double)width), 90, 320);
        var sample = new Bitmap(sampleWidth, sampleHeight, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(sample))
        {
            graphics.DrawImage(full, 0, 0, sample.Width, sample.Height);
        }
        return sample;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);

    private static IntPtr FindLargestVisibleWindow(int processId)
    {
        var best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            GetWindowThreadProcessId(window, out var owner);
            if (owner != processId || !GetWindowRect(window, out var rect)) return true;
            var area = (long)Math.Max(0, rect.Right - rect.Left) * Math.Max(0, rect.Bottom - rect.Top);
            if (area > bestArea) { best = window; bestArea = area; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    public static double Difference(Bitmap baseline, Bitmap current)
    {
        long total = 0;
        for (var y = 0; y < baseline.Height; y += 2)
        for (var x = 0; x < baseline.Width; x += 2)
        {
            var a = baseline.GetPixel(x, y);
            var b = current.GetPixel(x, y);
            total += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
        }
        return total / (double)((baseline.Width / 2) * (baseline.Height / 2) * 3 * 255);
    }

    public static bool SendJoinCommand(string command, bool resetMenuBeforeAttempt)
    {
        if (resetMenuBeforeAttempt)
        {
            var back = new List<Input>();
            AddKey(back, VkEscape);
            if (!SendAll(back)) return false;
            Thread.Sleep(750);
        }
        var toggle = new List<Input>();
        AddKey(toggle, VkOem3);
        if (!SendAll(toggle)) return false;
        Thread.Sleep(1000);

        var totalRequested = toggle.Count;
        uint totalSent = (uint)toggle.Count;
        foreach (var character in command)
        {
            var key = BuildPhysicalKey(character);
            if (key is null)
            {
                LastInputDiagnostic = $"unsupportedCharacter=U+{(int)character:X4}";
                return false;
            }
            if (!SendAll(key)) return false;
            totalRequested += key.Count;
            totalSent += (uint)key.Count;
            Thread.Sleep(25);
        }
        Thread.Sleep(200);
        var enter = new List<Input>();
        AddKey(enter, VkReturn);
        if (!SendAll(enter)) return false;
        totalRequested += enter.Count;
        totalSent += (uint)enter.Count;
        LastInputDiagnostic = $"physicalKeys requested={totalRequested}, sent={totalSent}, menuReset={resetMenuBeforeAttempt}, inputSize={Marshal.SizeOf<Input>()}, win32Error=0";
        return true;
    }

    public static bool LooksLikeAutoJoinPrompt(Bitmap sample)
    {
        var bluePixels = 0;
        var total = 0;
        CountBlue(sample, .37, .63, .25, .75, 25, 15, out bluePixels, out total);
        // ASA dims this dialog more heavily when it appears over the populated
        // session browser. Keep the distinctive two-button layout as the primary
        // signal while allowing that darker presentation.
        return total > 0 && bluePixels >= total * 0.65 && DarkRatio(sample) >= .55 &&
               BlueRatio(sample, .40, .60, .64, .71) >= .45 &&
               BlueRatio(sample, .47, .53, .64, .71) < .45;
    }

    public static bool LooksLikeNetworkFailurePrompt(Bitmap sample)
    {
        var central = BlueRatio(sample, .37, .63, .25, .74);
        var centeredButton = BlueRatio(sample, .47, .53, .64, .71);
        return central >= .68 && centeredButton >= .55 && DarkRatio(sample) >= .55;
    }

    public static bool LooksLikeSingleOkFailure(Bitmap sample)
    {
        var central = BlueRatio(sample, .37, .63, .28, .64);
        var okButton = BlueRatio(sample, .43, .57, .58, .64);
        var lowerButtons = BlueRatio(sample, .40, .60, .66, .71);
        return central >= .65 && okButton >= .45 && lowerButtons < .35 && DarkRatio(sample) >= .55;
    }

    public static bool LooksLikeSessionBrowser(Bitmap sample)
    {
        var orange = 0;
        var total = 0;
        var region = DesignRegion(sample, .82, .98, .80, .90);
        for (var y = region.Top; y < region.Bottom; y++)
        for (var x = region.Left; x < region.Right; x++)
        {
            var color = sample.GetPixel(x, y);
            if (color.R > 90 && color.R > color.G * 1.25 && color.B < color.R * .55) orange++;
            total++;
        }
        return total > 0 && orange >= total * .18;
    }

    public static bool LooksLikeStartupScreen(Bitmap sample) =>
        BlueRatio(sample, .39, .61, .74, .83) >= .28;

    public static bool LooksLikeMainMenu(Bitmap sample) =>
        BlueRatio(sample, .28, .72, .25, .74) >= .32;

    public static bool LooksLikeLoadingGlobe(Bitmap sample)
    {
        var viewport = DesignViewport(sample.Width, sample.Height);
        var dark = 0; var purple = 0; var bright = 0; var total = 0;
        for (var y = viewport.Top; y < viewport.Bottom; y++)
        for (var x = viewport.Left; x < viewport.Right; x++)
        {
            var color = sample.GetPixel(x, y);
            var luminance = color.R + color.G + color.B;
            if (luminance < 105) dark++;
            if (color.B > 45 && color.R > 35 && color.B > color.G * 1.25 && color.R > color.G * 1.2) purple++;
            if (luminance > 560) bright++;
            total++;
        }
        return total > 0 && dark >= total * .68 && purple >= total * .008 && bright >= total * .0015;
    }

    private static double DarkRatio(Bitmap sample)
    {
        var viewport = DesignViewport(sample.Width, sample.Height);
        var dark = 0; var total = 0;
        for (var y = viewport.Top; y < viewport.Bottom; y += 2)
        for (var x = viewport.Left; x < viewport.Right; x += 2)
        {
            var color = sample.GetPixel(x, y);
            if (color.R + color.G + color.B < 150) dark++;
            total++;
        }
        return total == 0 ? 0 : dark / (double)total;
    }

    private static double BlueRatio(Bitmap sample, double left, double right, double top, double bottom)
    {
        var blue = 0;
        var total = 0;
        var region = DesignRegion(sample, left, right, top, bottom);
        for (var y = region.Top; y < region.Bottom; y++)
        for (var x = region.Left; x < region.Right; x++)
        {
            var color = sample.GetPixel(x, y);
            if (color.B > color.R + 15 && color.G > color.R + 10) blue++;
            total++;
        }
        return total == 0 ? 0 : blue / (double)total;
    }

    private static void CountBlue(Bitmap sample, double left, double right, double top, double bottom,
        int blueOverRed, int greenOverRed, out int blue, out int total)
    {
        blue = 0; total = 0;
        var region = DesignRegion(sample, left, right, top, bottom);
        for (var y = region.Top; y < region.Bottom; y++)
        for (var x = region.Left; x < region.Right; x++)
        {
            var color = sample.GetPixel(x, y);
            if (color.B > color.R + blueOverRed && color.G > color.R + greenOverRed) blue++;
            total++;
        }
    }

    private static Rectangle DesignRegion(Bitmap sample, double left, double right, double top, double bottom)
    {
        var viewport = DesignViewport(sample.Width, sample.Height);
        var x0 = Math.Clamp(viewport.Left + (int)(viewport.Width * left), 0, sample.Width - 1);
        var x1 = Math.Clamp(viewport.Left + (int)(viewport.Width * right), x0 + 1, sample.Width);
        var y0 = Math.Clamp(viewport.Top + (int)(viewport.Height * top), 0, sample.Height - 1);
        var y1 = Math.Clamp(viewport.Top + (int)(viewport.Height * bottom), y0 + 1, sample.Height);
        return Rectangle.FromLTRB(x0, y0, x1, y1);
    }

    private static Rectangle DesignViewport(int width, int height)
    {
        const double aspect = 16d / 9d;
        if (width / (double)height >= aspect)
        {
            var designWidth = (int)Math.Round(height * aspect);
            return new Rectangle((width - designWidth) / 2, 0, designWidth, height);
        }
        var designHeight = (int)Math.Round(width / aspect);
        return new Rectangle(0, (height - designHeight) / 2, width, designHeight);
    }

    private static List<Input>? BuildPhysicalKey(char character)
    {
        var mapping = VkKeyScan(character);
        if (mapping == -1) return null;
        var virtualKey = (ushort)(mapping & 0xff);
        var modifiers = (mapping >> 8) & 0xff;
        var inputs = new List<Input>();
        if ((modifiers & 1) != 0) inputs.Add(Keyboard(VkShift, 0));
        if ((modifiers & 2) != 0) inputs.Add(Keyboard(VkControl, 0));
        if ((modifiers & 4) != 0) inputs.Add(Keyboard(VkAlt, 0));
        inputs.Add(Keyboard(virtualKey, 0));
        inputs.Add(Keyboard(virtualKey, KeyUp));
        if ((modifiers & 4) != 0) inputs.Add(Keyboard(VkAlt, KeyUp));
        if ((modifiers & 2) != 0) inputs.Add(Keyboard(VkControl, KeyUp));
        if ((modifiers & 1) != 0) inputs.Add(Keyboard(VkShift, KeyUp));
        return inputs;
    }

    private static bool SendAll(List<Input> inputs)
    {
        var structureSize = Marshal.SizeOf<Input>();
        Marshal.SetLastPInvokeError(0);
        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), structureSize);
        var error = Marshal.GetLastPInvokeError();
        LastInputDiagnostic = $"requested={inputs.Count}, sent={sent}, inputSize={structureSize}, win32Error={error}";
        return sent == (uint)inputs.Count;
    }

    private static void AddKey(List<Input> inputs, ushort key)
    {
        inputs.Add(Keyboard(key, 0));
        inputs.Add(Keyboard(key, KeyUp));
    }

    private static Input Keyboard(ushort value, uint flags) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = (flags & Unicode) != 0 ? (ushort)0 : value,
                ScanCode = (flags & Unicode) != 0 ? value : (ushort)0,
                Flags = flags
            }
        }
    };
}

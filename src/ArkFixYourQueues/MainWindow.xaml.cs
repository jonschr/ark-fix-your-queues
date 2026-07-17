using System.Diagnostics;
using System.Drawing;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ArkFixYourQueues;

public partial class MainWindow : Window
{
    private const int ToggleHotkeyId = 0x4153;
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkG = 0x47;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    private enum JoinWorkflowPhase { Starting, WaitingForMainMenu, WaitingForSessionBrowser, WaitingForJoinResult, WaitingForAttemptReset, WaitingAfterCancel, WaitingAfterBack }
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Process? _ark;
    private int _runAttemptCount;
    private int _launchAttemptCount;
    private DateTimeOffset? _lastJoinAttemptAt;
    private double _runAttemptIntervalSeconds;
    private int _runAttemptIntervalCount;
    private double _launchAttemptIntervalSeconds;
    private int _launchAttemptIntervalCount;
    private JoinWorkflowPhase _phase;
    private DateTimeOffset _nextWorkflowAction;
    private DateTimeOffset _phaseDeadline;
    private DateTimeOffset _nextPreviewAt;
    private DateTimeOffset _lastWorkflowProgressAt;
    private bool _inactivityAlertPlayed;
    private DateTimeOffset _nextRecoveryScanAt;
    private int _attemptSpacingSeconds;
    private bool _preAlertRecoveryEnabled = true;
    private HwndSource? _windowSource;
    private bool _toggleHotkeyRegistered;
    private bool _loadingGlobeVisible;
    private string? _activeErrorEvidence;
    private int _evidenceSequence;
    private DateTimeOffset? _networkDismissalAttemptAt;
    private int _networkDismissalAttemptCount;
    private bool _networkDismissalLimitReported;
    private int _runLoadingGlobeCount;
    private int _launchLoadingGlobeCount;
    private readonly DelayPerformanceStore _delayPerformance = DelayPerformanceStore.Load();
    private PendingUpdate? _pendingUpdate;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => RegisterToggleHotkey();
        _timer.Tick += (_, _) => TickController();
        Loaded += (_, _) =>
        {
            RenderDelayPerformance();
            Log("Ready. ASA's Join Last Played server will be used; no app-side server selection is required.");
            _ = CheckForUpdateAsync();
        };
        Closed += (_, _) =>
        {
            if (_toggleHotkeyRegistered && _windowSource is not null)
                UnregisterHotKey(_windowSource.Handle, ToggleHotkeyId);
            _windowSource?.RemoveHook(WindowMessageHook);
            _timer.Stop();
            ScreenPreview.Source = null;
            EvidenceStrip.Children.Clear();
            if (_pendingUpdate is not null) UpdateService.TryLaunchOnExit(_pendingUpdate);
            Application.Current.Shutdown();
            Environment.Exit(0);
        };
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            _pendingUpdate = await UpdateService.CheckAndDownloadAsync();
            if (_pendingUpdate is not null)
                Log($"Update v{_pendingUpdate.Version} is ready and will install automatically when the app closes.");
        }
        catch (Exception error)
        {
            Log($"Update check skipped: {error.Message}");
        }
    }

    private void RegisterToggleHotkey()
    {
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
        _toggleHotkeyRegistered = _windowSource is not null &&
                                  RegisterHotKey(_windowSource.Handle, ToggleHotkeyId, ModControl | ModNoRepeat, VkG);
        Log(_toggleHotkeyRegistered
            ? "Global Ctrl+G shortcut ready: press Ctrl+G to start or stop."
            : "Global Ctrl+G shortcut could not be registered; use GO and STOP.");
    }

    private IntPtr WindowMessageHook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey || wParam.ToInt32() != ToggleHotkeyId) return IntPtr.Zero;
        handled = true;
        if (StopButton.IsEnabled) Stop("Stopped with the global Ctrl+G shortcut.");
        else if (StartButton.IsEnabled) Start_OnClick(this, new RoutedEventArgs());
        return IntPtr.Zero;
    }

    private void Start_OnClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DelaySecondsBox.Text.Trim(), out _attemptSpacingSeconds) || _attemptSpacingSeconds is < 0 or > 300)
        {
            MessageBox.Show(this, "Attempt spacing must be a whole number from 0 to 300 seconds.", "Check attempt spacing");
            DelaySecondsBox.Focus();
            DelaySecondsBox.SelectAll();
            return;
        }
        _preAlertRecoveryEnabled = PreAlertRecoveryBox.IsChecked == true;
        _ark = WindowsInterop.FindArk();
        if (_ark is null) { MessageBox.Show(this, "ArkAscended.exe is not running.", "ASA not found"); return; }
        if (!WindowsInterop.ActivateArk(_ark))
        {
            MessageBox.Show(this, "ASA is running, but Windows would not activate its game window.", "Could not activate ASA");
            return;
        }
        _runAttemptCount = 0;
        _runLoadingGlobeCount = 0;
        _networkDismissalAttemptAt = null; _networkDismissalAttemptCount = 0; _networkDismissalLimitReported = false;
        _lastJoinAttemptAt = null; _runAttemptIntervalSeconds = 0; _runAttemptIntervalCount = 0;
        CurrentGoAttemptsText.Text = "0";
        CurrentGoAverageText.Text = "—";
        CurrentGoGlobesText.Text = "0";
        SetRunning(true);
        _phase = JoinWorkflowPhase.Starting;
        MarkWorkflowProgress(DateTimeOffset.Now);
        _nextWorkflowAction = DateTimeOffset.Now;
        _phaseDeadline = DateTimeOffset.Now.AddSeconds(15);
        Log("Join Last Played workflow started. ASA activated automatically.");
        if (_attemptSpacingSeconds > 0) Log($"Join attempts will be spaced at least {_attemptSpacingSeconds} seconds apart.");
        _timer.Start();
    }

    private void Stop_OnClick(object sender, RoutedEventArgs e) => Stop("Stopped by user.");

    private void TickController()
    {
        if (_ark is null || _ark.HasExited) { Stop("ASA closed; stopped safely."); return; }
        var now = DateTimeOffset.Now;
        using var screen = WindowsInterop.CaptureWindow(_ark);
        if (screen is null) { Stop("Could not capture ASA; stopped safely."); return; }
        if (now >= _nextPreviewAt)
        {
            UpdateScreenPreview(screen);
            _nextPreviewAt = now.AddSeconds(1);
        }
        var isSessions = WindowsInterop.LooksLikeSessionBrowser(screen);
        var isModal = WindowsInterop.LooksLikeAutoJoinPrompt(screen);
        var isOkFailure = WindowsInterop.LooksLikeSingleOkFailure(screen);
        // The session list is a large blue panel and can resemble the network-failure
        // modal while its centered "Joining server..." overlay is visible. The orange
        // JOIN control is a stronger, mutually exclusive signal for this normal screen.
        var isNetworkFailure = !isSessions && !isModal &&
                               WindowsInterop.LooksLikeNetworkFailurePrompt(screen);
        // The populated session browser contains enough blue UI to satisfy these
        // broad menu heuristics. Never allow menu actions while the stronger
        // session-browser signal is present (including its Joining server overlay).
        var isStartup = !isSessions && WindowsInterop.LooksLikeStartupScreen(screen);
        var isMainMenu = !isSessions && WindowsInterop.LooksLikeMainMenu(screen);
        var isLoadingGlobe = WindowsInterop.LooksLikeLoadingGlobe(screen);
        if (!isNetworkFailure && _networkDismissalAttemptAt is not null)
        {
            _networkDismissalAttemptAt = null;
            _networkDismissalAttemptCount = 0;
            _networkDismissalLimitReported = false;
            Log("Full-server / connection-failed dialog cleared.");
            MarkWorkflowProgress(now);
            _phase = JoinWorkflowPhase.WaitingAfterCancel;
            _nextWorkflowAction = now.AddMilliseconds(650);
            _phaseDeadline = now.AddSeconds(12);
        }

        if (isNetworkFailure) CaptureErrorEvidence("Full server / connection failed", screen);
        else if (isOkFailure) CaptureErrorEvidence("Connection timeout", screen);
        else if (isModal) CaptureErrorEvidence("Connection / server full", screen);
        else _activeErrorEvidence = null;

        var canBeLoading = isLoadingGlobe &&
                           !isNetworkFailure && !isOkFailure && !isModal && !isSessions && !isStartup && !isMainMenu;
        if (canBeLoading)
        {
            // A globe can appear late, after one or more retry transitions. Count
            // the screen transition into it, not timer ticks and not join clicks:
            // it must disappear for at least one capture before another globe is
            // considered a new loading event.
            if (!_loadingGlobeVisible)
            {
                CurrentGoGlobesText.Text = (++_runLoadingGlobeCount).ToString();
                LaunchGlobesText.Text = (++_launchLoadingGlobeCount).ToString();
                var performance = _delayPerformance.RecordGlobe(_attemptSpacingSeconds);
                var ratio = performance.Attempts == 0 ? 0 : performance.LoadingGlobes / (double)performance.Attempts;
                RenderDelayPerformance();
                AddEvidence("Loading globe", screen);
                Log("Loading globe detected; retry navigation is suspended while ASA loads or returns a post-load error.");
                Log($"Delay optimization: {_attemptSpacingSeconds}s spacing has produced {performance.LoadingGlobes} globe(s) from {performance.Attempts} all-time attempt(s) ({ratio:P1}).");
                MarkWorkflowProgress(now);
            }
            _loadingGlobeVisible = true;
        }
        else _loadingGlobeVisible = false;

        var inactiveFor = now - _lastWorkflowProgressAt;
        if (_preAlertRecoveryEnabled && now >= _nextRecoveryScanAt)
        {
            _nextRecoveryScanAt = now.AddSeconds(5);
            if (TryPreAlertRecovery(now, isNetworkFailure, isOkFailure, isModal)) return;
            if (_phase != JoinWorkflowPhase.WaitingForJoinResult)
                Log("Recovery scan found no safe known action; the next scan will run in 5 seconds.");
        }
        if (!_inactivityAlertPlayed && inactiveFor >= TimeSpan.FromSeconds(5))
        {
            _inactivityAlertPlayed = true;
            SystemSounds.Exclamation.Play();
            Log("No recognized workflow transition for 5 seconds; played the attention sound.");
        }

        if (_phase is JoinWorkflowPhase.WaitingForJoinResult or JoinWorkflowPhase.WaitingForAttemptReset)
        {
            if (isNetworkFailure)
            {
                DismissNetworkFailure(now, "Full-server / connection-failed dialog detected");
                return;
            }
            else if (isOkFailure)
            {
                if (!WindowsInterop.ClickWindowDesignRelative(_ark, .500, .612)) { Stop("Could not click OK on the joining-failed dialog."); return; }
                Log("Joining-failed dialog detected; clicked OK.");
                MarkWorkflowProgress(now);
                _phase = JoinWorkflowPhase.WaitingAfterCancel; _nextWorkflowAction = now.AddMilliseconds(650); _phaseDeadline = now.AddSeconds(12);
                return;
            }
            else if (isModal)
            {
                if (!WindowsInterop.ClickWindowDesignRelative(_ark, .558, .675)) { Stop("Could not click CANCEL on the connection modal."); return; }
                Log("Connection/full modal detected; clicked CANCEL.");
                MarkWorkflowProgress(now);
                _phase = JoinWorkflowPhase.WaitingAfterCancel; _nextWorkflowAction = now.AddMilliseconds(650); _phaseDeadline = now.AddSeconds(12);
                return;
            }
        }

        if (_phase == JoinWorkflowPhase.WaitingForJoinResult)
        {
            // Attempt spacing is deliberately only the post-JOIN wait. ASA can
            // redraw its "Joining server..." status in several different ways,
            // so waiting for a particular intermediate frame makes the retry
            // loop brittle. A recognized result dialog is handled above; absent
            // one, return through the session browser's BACK control as soon as
            // the requested post-JOIN delay has elapsed.
            if (now >= _nextWorkflowAction)
            {
                if (isLoadingGlobe)
                {
                    Log("ASA has entered its loading screen; retry navigation is suspended while it loads or reports an error.");
                    MarkWorkflowProgress(now);
                    _nextWorkflowAction = now.AddSeconds(5);
                    _phaseDeadline = now.AddSeconds(12);
                }
                else
                {
                    // The visible "Joining server..." status is not a cancellable
                    // dialog. Escape opens ASA's pause menu, which is the wrong
                    // screen. This known session-browser control is the reliable
                    // reset path, even if ASA has dimmed or redrawn the list.
                    if (!WindowsInterop.ClickWindowDesignRelative(_ark, .087, .811)) { Stop("Could not click BACK after the configured post-JOIN delay."); return; }
                    Log($"Post-JOIN delay elapsed ({_attemptSpacingSeconds}s); clicked the session browser BACK control.");
                    MarkWorkflowProgress(now);
                    _phase = JoinWorkflowPhase.WaitingAfterBack;
                    _nextWorkflowAction = now.AddMilliseconds(250);
                    _phaseDeadline = now.AddSeconds(12);
                }
            }
            return;
        }

        if (now < _nextWorkflowAction) return;
        switch (_phase)
        {
            case JoinWorkflowPhase.WaitingForAttemptReset:
            case JoinWorkflowPhase.WaitingAfterCancel:
                if (isNetworkFailure || isOkFailure || isModal)
                {
                    _nextWorkflowAction = now.AddMilliseconds(250);
                    break;
                }
                if (isSessions)
                {
                    if (!WindowsInterop.ClickWindowDesignRelative(_ark, .087, .811)) { Stop("Could not click BACK after clearing the attempt."); return; }
                    Log("Attempt cleared; clicked BACK.");
                    MarkWorkflowProgress(now);
                    _phase = JoinWorkflowPhase.WaitingAfterBack;
                    _nextWorkflowAction = now.AddMilliseconds(250);
                    _phaseDeadline = now.AddSeconds(12);
                    break;
                }
                if (isStartup)
                {
                    if (!WindowsInterop.ClickWindowDesignRelative(_ark, .500, .795)) { Stop("Could not click PRESS TO START after cancelling a pending attempt."); return; }
                    Log("Pending attempt cleared to the title screen; clicked PRESS TO START once.");
                    MarkWorkflowProgress(now);
                    _phase = JoinWorkflowPhase.WaitingForMainMenu;
                    _nextWorkflowAction = now.AddMilliseconds(250);
                    _phaseDeadline = now.AddSeconds(12);
                    break;
                }
                if (isMainMenu)
                {
                    if (!WindowsInterop.ClickWindowDesignRelative(_ark, .387, .517)) { Stop("Could not click JOIN GAME after cancelling a pending attempt."); return; }
                    Log("Pending attempt cleared; clicked JOIN GAME.");
                    MarkWorkflowProgress(now);
                    _phase = JoinWorkflowPhase.WaitingForSessionBrowser;
                    _nextWorkflowAction = now.AddMilliseconds(250);
                    _phaseDeadline = now.AddSeconds(12);
                    break;
                }
                if (now >= _phaseDeadline) { Log("Attempt reset is still unresolved; continuing recovery scans."); _phaseDeadline = now.AddSeconds(12); }
                else _nextWorkflowAction = now.AddMilliseconds(250);
                break;
            case JoinWorkflowPhase.Starting:
                if (isNetworkFailure)
                {
                    DismissNetworkFailure(now, "Existing full-server / connection-failed dialog detected");
                }
                else if (isOkFailure)
                {
                    if (!WindowsInterop.ClickWindowDesignRelative(_ark, .500, .612)) { Stop("Could not click OK on the existing joining-failed dialog."); return; }
                    Log("Dismissed an existing joining-failed dialog.");
                    MarkWorkflowProgress(now);
                    _phase = JoinWorkflowPhase.WaitingAfterCancel; _nextWorkflowAction = now.AddMilliseconds(650); _phaseDeadline = now.AddSeconds(12);
                }
                else if (isModal)
                {
                    if (!WindowsInterop.ClickWindowDesignRelative(_ark, .558, .675)) { Stop("Could not cancel the existing connection modal."); return; }
                    Log("Cancelled an existing connection modal.");
                    MarkWorkflowProgress(now);
                    _phase = JoinWorkflowPhase.WaitingAfterCancel; _nextWorkflowAction = now.AddMilliseconds(650); _phaseDeadline = now.AddSeconds(12);
                }
                else if (isSessions) ClickJoinLastPlayed(now);
                else if (isStartup)
                {
                    if (!WindowsInterop.ClickWindowDesignRelative(_ark, .500, .795)) { Stop("Could not click PRESS TO START."); return; }
                    Log("Clicked PRESS TO START on the ASA title screen.");
                    MarkWorkflowProgress(now);
                    _phase = JoinWorkflowPhase.WaitingForMainMenu;
                    _nextWorkflowAction = now.AddMilliseconds(250);
                    _phaseDeadline = now.AddSeconds(12);
                }
                else if (isMainMenu)
                {
                    if (!WindowsInterop.ClickWindowDesignRelative(_ark, .387, .517)) { Stop("Could not click JOIN GAME."); return; }
                    Log("Clicked JOIN GAME.");
                    MarkWorkflowProgress(now);
                    _phase = JoinWorkflowPhase.WaitingForSessionBrowser;
                    _nextWorkflowAction = now.AddMilliseconds(250);
                    _phaseDeadline = now.AddSeconds(12);
                }
                else
                {
                    if (!WindowsInterop.ClickWindowDesignRelative(_ark, .500, .795)) { Stop("Could not click PRESS TO START."); return; }
                    Log("Clicked PRESS TO START on the ASA startup screen.");
                    MarkWorkflowProgress(now);
                    _phase = JoinWorkflowPhase.WaitingForMainMenu; _nextWorkflowAction = now.AddMilliseconds(250);
                }
                break;
            case JoinWorkflowPhase.WaitingForMainMenu:
                if (!isMainMenu)
                {
                    if (now >= _phaseDeadline) { Log("ASA main menu is still unresolved; continuing recovery scans."); _phaseDeadline = now.AddSeconds(12); }
                    else _nextWorkflowAction = now.AddMilliseconds(250);
                    break;
                }
                if (!WindowsInterop.ClickWindowDesignRelative(_ark, .387, .517)) { Stop("Could not click JOIN GAME."); return; }
                Log("Clicked JOIN GAME.");
                MarkWorkflowProgress(now);
                _phase = JoinWorkflowPhase.WaitingForSessionBrowser; _nextWorkflowAction = now.AddMilliseconds(250); _phaseDeadline = now.AddSeconds(12);
                break;
            case JoinWorkflowPhase.WaitingForSessionBrowser:
                if (isSessions) ClickJoinLastPlayed(now);
                else if (now >= _phaseDeadline) { Log("Session browser is still unresolved; continuing recovery scans."); _phaseDeadline = now.AddSeconds(12); }
                else _nextWorkflowAction = now.AddMilliseconds(250);
                break;
            case JoinWorkflowPhase.WaitingAfterBack:
                if (isStartup)
                {
                    if (!WindowsInterop.ClickWindowDesignRelative(_ark, .500, .795)) { Stop("Could not click PRESS TO START after returning from the session browser."); return; }
                    Log("Returned to the ASA title screen; clicked PRESS TO START once.");
                    MarkWorkflowProgress(now);
                    _phase = JoinWorkflowPhase.WaitingForMainMenu;
                    _nextWorkflowAction = now.AddMilliseconds(250);
                    _phaseDeadline = now.AddSeconds(12);
                    break;
                }
                if (!isMainMenu)
                {
                    if (now >= _phaseDeadline) { Log("ASA main menu has not returned; continuing recovery scans."); _phaseDeadline = now.AddSeconds(12); }
                    else _nextWorkflowAction = now.AddMilliseconds(250);
                    break;
                }
                if (!WindowsInterop.ClickWindowDesignRelative(_ark, .387, .517)) { Stop("Could not click JOIN GAME."); return; }
                Log("Clicked JOIN GAME for the next attempt.");
                MarkWorkflowProgress(now);
                _phase = JoinWorkflowPhase.WaitingForSessionBrowser; _nextWorkflowAction = now.AddMilliseconds(250); _phaseDeadline = now.AddSeconds(12);
                break;
        }
    }

    private void ClickJoinLastPlayed(DateTimeOffset now)
    {
        if (DeferForAttemptSpacing(now)) return;
        if (_ark is null || !WindowsInterop.ClickWindowDesignRelative(_ark, .891, .823))
        {
            Stop("Could not click JOIN LAST PLAYED.");
            return;
        }
        RecordAttempt("Clicked JOIN LAST PLAYED", now);
    }

    private bool DeferForAttemptSpacing(DateTimeOffset now)
    {
        if (_attemptSpacingSeconds == 0 || _lastJoinAttemptAt is null) return false;
        var earliest = _lastJoinAttemptAt.Value.AddSeconds(_attemptSpacingSeconds);
        if (now >= earliest) return false;
        _nextWorkflowAction = earliest;
        Log($"Waiting {Math.Ceiling((earliest - now).TotalSeconds)}s before the next join attempt.");
        return true;
    }

    private void RecordAttempt(string action, DateTimeOffset now)
    {
        MarkWorkflowProgress(now);
        CurrentGoAttemptsText.Text = (++_runAttemptCount).ToString();
        LaunchAttemptsText.Text = (++_launchAttemptCount).ToString();
        _delayPerformance.RecordAttempt(_attemptSpacingSeconds);
        RenderDelayPerformance();
        var attemptAt = DateTimeOffset.Now;
        if (_lastJoinAttemptAt is not null)
        {
            var intervalSeconds = (attemptAt - _lastJoinAttemptAt.Value).TotalSeconds;
            _runAttemptIntervalSeconds += intervalSeconds;
            _runAttemptIntervalCount++;
            _launchAttemptIntervalSeconds += intervalSeconds;
            _launchAttemptIntervalCount++;
            CurrentGoAverageText.Text = $"{_runAttemptIntervalSeconds / _runAttemptIntervalCount:F1}s";
            LaunchAverageText.Text = $"{_launchAttemptIntervalSeconds / _launchAttemptIntervalCount:F1}s";
        }
        _lastJoinAttemptAt = attemptAt;
        Log($"{action} (attempt {_runAttemptCount} this GO, {_launchAttemptCount} since app launch).");
        _phase = JoinWorkflowPhase.WaitingForJoinResult;
        // ASA's "Joining server..." overlay is not a result and may never clear.
        // The configured delay is only how long we intentionally wait before
        // taking the session browser's BACK route for the next attempt.
        _nextWorkflowAction = now.AddSeconds(_attemptSpacingSeconds);
        _phaseDeadline = _nextWorkflowAction;
    }

    private void MarkWorkflowProgress(DateTimeOffset now)
    {
        _lastWorkflowProgressAt = now;
        _inactivityAlertPlayed = false;
        _nextRecoveryScanAt = now.AddSeconds(4);
    }

    private bool TryPreAlertRecovery(DateTimeOffset now, bool isNetworkFailure, bool isOkFailure, bool isModal)
    {
        if (_ark is null) return false;
        if (isNetworkFailure)
        {
            return DismissNetworkFailure(now, "Recovery scan found a full-server / connection-failed dialog");
        }
        if (isOkFailure)
        {
            if (!WindowsInterop.ClickWindowDesignRelative(_ark, .500, .612)) return false;
            Log("Recovery scan clicked OK on the joining-failed dialog.");
            MarkWorkflowProgress(now);
            _phase = JoinWorkflowPhase.WaitingAfterCancel;
            _nextWorkflowAction = now.AddMilliseconds(650);
            _phaseDeadline = now.AddSeconds(12);
            return true;
        }
        if (isModal)
        {
            if (!WindowsInterop.ClickWindowDesignRelative(_ark, .558, .675)) return false;
            Log("Recovery scan clicked CANCEL on the connection/full dialog.");
            MarkWorkflowProgress(now);
            _phase = JoinWorkflowPhase.WaitingAfterCancel;
            _nextWorkflowAction = now.AddMilliseconds(650);
            _phaseDeadline = now.AddSeconds(12);
            return true;
        }
        // Never navigate menus speculatively while ASA is resolving a join. The
        // blue session browser and its overlays are too similar to menu screens.
        // Result dialogs are handled explicitly in TickController; loading is left
        // untouched. Recovery navigation resumes only after a confirmed reset.
        return false;
    }

    private bool DismissNetworkFailure(DateTimeOffset now, string context)
    {
        if (_ark is null) return false;
        if (_networkDismissalAttemptAt is not null && now - _networkDismissalAttemptAt < TimeSpan.FromSeconds(3))
        {
            _nextWorkflowAction = _networkDismissalAttemptAt.Value.AddSeconds(3);
            return true;
        }
        if (_networkDismissalAttemptCount >= 2)
        {
            if (!_networkDismissalLimitReported)
            {
                _networkDismissalLimitReported = true;
                SystemSounds.Exclamation.Play();
                Log("Full-server / connection-failed dialog remained after two dismiss clicks; stopped clicking and waiting for user attention.");
            }
            _nextWorkflowAction = now.AddSeconds(5);
            return true;
        }
        // This classifier covers two ASA variants: a centered single ACCEPT button
        // and the full-server ACCEPT/CANCEL pair. This point is inside the former
        // and on CANCEL in the latter, so both safely return control to our loop.
        if (!WindowsInterop.ClickWindowDesignRelative(_ark, .545, .675))
        {
            Stop("Could not dismiss the network-failure dialog.");
            return true;
        }
        _networkDismissalAttemptAt = now;
        _networkDismissalAttemptCount++;
        Log($"{context}; clicked the safe dismiss position and waiting for the dialog to clear.");
        // Sending a click is not proof of progress. Leave the inactivity clock alone
        // until a subsequent capture confirms that the dialog actually disappeared.
        // Stay here until a later capture confirms the dialog disappeared. Only
        // then does TickController advance to BACK -> JOIN GAME -> JOIN LAST PLAYED.
        _phase = JoinWorkflowPhase.WaitingForJoinResult;
        _nextWorkflowAction = now.AddMilliseconds(500);
        return true;
    }

    private void Stop(string reason)
    {
        _timer.Stop();
        SetRunning(false);
        Log(reason);
    }

    private void SetRunning(bool running)
    {
        DelaySecondsBox.IsEnabled = !running;
        PreAlertRecoveryBox.IsEnabled = !running;
        StartButton.IsEnabled = !running; StopButton.IsEnabled = running;
    }

    private void UpdateScreenPreview(Bitmap screen)
    {
        ScreenPreviewBorder.AspectRatio = screen.Width / (double)screen.Height;
        var handle = screen.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            ScreenPreview.Source = source;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PreviewTimeText.Text = $"CAPTURED {DateTime.Now:T}";
        }
        finally { DeleteObject(handle); }
    }

    private void RenderDelayPerformance()
    {
        if (DelayPerformancePanel is null) return;
        DelayPerformancePanel.Children.Clear();
        var ranked = _delayPerformance.Values
            .Where(item => item.Value.Attempts > 0)
            .OrderByDescending(item => item.Value.LoadingGlobes / (double)item.Value.Attempts)
            .ThenByDescending(item => item.Value.Attempts)
            .ThenBy(item => item.Key)
            .Take(5)
            .ToList();

        if (ranked.Count == 0)
        {
            DelayPerformancePanel.Children.Add(new TextBlock
            {
                Text = "No loading-globe history yet",
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });
            return;
        }

        foreach (var item in ranked)
        {
            var yield = item.Value.LoadingGlobes / (double)item.Value.Attempts;
            DelayPerformancePanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 244, 245)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Child = new TextBlock
                {
                    Text = $"{item.Key}s   {item.Value.LoadingGlobes}/{item.Value.Attempts}   {yield:P1}",
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold
                }
            });
        }
    }

    private void CaptureErrorEvidence(string label, Bitmap screen)
    {
        if (_activeErrorEvidence == label) return;
        _activeErrorEvidence = label;
        AddEvidence(label, screen);
    }

    private void AddEvidence(string label, Bitmap screen)
    {
        AddEvidence(label, CreateBitmapSource(screen), screen.Width / (double)screen.Height);
    }

    private void AddEvidence(string label, BitmapSource source, double aspectRatio)
    {
        const int maximumCards = 60;
        if (EvidenceStrip.Children.Count >= maximumCards)
            EvidenceStrip.Children.RemoveAt(EvidenceStrip.Children.Count - 1);

        var image = new System.Windows.Controls.Image { Source = source, Stretch = Stretch.Uniform };
        var preview = new AspectRatioBorder
        {
            Width = 210,
            AspectRatio = aspectRatio,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27)),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Child = image
        };
        preview.Cursor = Cursors.Hand;
        preview.ToolTip = "Click to open a larger preview";
        var capturedAt = DateTime.Now;
        preview.MouseLeftButtonUp += (_, _) => ShowEvidencePreview(source, label, capturedAt);
        var stack = new StackPanel();
        stack.Children.Add(preview);
        stack.Children.Add(new TextBlock
        {
            Text = $"{++_evidenceSequence}. {label}", FontSize = 11, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 1)
        });
        stack.Children.Add(new TextBlock
        {
            Text = capturedAt.ToLongTimeString(), FontSize = 10,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122))
        });
        EvidenceStrip.Children.Insert(0, new Border
        {
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(212, 212, 216)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7), Margin = new Thickness(0, 0, 8, 0), Child = stack
        });
    }

    private void ShowEvidencePreview(BitmapSource source, string label, DateTime capturedAt)
    {
        var image = new System.Windows.Controls.Image { Source = source, Stretch = Stretch.Uniform };
        var viewer = new Window
        {
            Owner = this,
            Title = $"{label} — {capturedAt:T}",
            Width = 1100,
            Height = 760,
            MinWidth = 640,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.Black,
            Content = image
        };
        viewer.ShowDialog();
    }

    private static BitmapSource CreateBitmapSource(Bitmap screen)
    {
        var handle = screen.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally { DeleteObject(handle); }
    }

    private async void CopyLogs_OnClick(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(string.IsNullOrWhiteSpace(ActivityBox.Text) ? "No activity has been logged yet." : ActivityBox.Text);
        CopyLogsButton.Content = "COPIED";
        CopyLogsButton.IsEnabled = false;
        await Task.Delay(1200);
        CopyLogsButton.Content = "COPY LOGS";
        CopyLogsButton.IsEnabled = true;
    }

    private void Log(string message)
    {
        ActivityBox.AppendText($"[{DateTime.Now:T}] {message}{Environment.NewLine}");
        ActivityBox.ScrollToEnd();
        DiagnosticLog.Write(message);
    }
}

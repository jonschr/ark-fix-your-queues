using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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

    private enum JoinWorkflowPhase { Idle, Starting, WaitingForMainMenu, WaitingForSessionBrowser, FilteringServer, WaitingForJoinResult, WaitingForAttemptReset, WaitingAfterCancel, WaitingAfterBack }
    private readonly RetryController _controller = new();
    private readonly AsaServerDirectoryClient _directory = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Process? _ark;
    private Bitmap? _baseline;
    private ServerTarget? _server;
    private int _attemptCount;
    private string? _lastStatus;
    private DateTimeOffset? _lastJoinAttemptAt;
    private double _attemptIntervalSeconds;
    private int _attemptIntervalCount;
    private JoinWorkflowPhase _phase;
    private DateTimeOffset _nextWorkflowAction;
    private DateTimeOffset _phaseDeadline;
    private DateTimeOffset _nextPreviewAt;
    private DateTimeOffset _lastWorkflowProgressAt;
    private bool _inactivityAlertPlayed;
    private DateTimeOffset _nextRecoveryScanAt;
    private int _attemptSpacingSeconds;
    private double _previewAspectRatio = 16d / 9d;
    private bool _preAlertRecoveryEnabled = true;
    private HwndSource? _windowSource;
    private bool _toggleHotkeyRegistered;
    private bool _loadingGlobeCapturedForAttempt;
    private string? _activeErrorEvidence;
    private int _evidenceSequence;
    private DateTimeOffset? _loadingGlobeSince;
    private BitmapSource? _latestScreenSource;
    private DateTimeOffset? _networkDismissalAttemptAt;
    private int _networkDismissalAttemptCount;
    private bool _networkDismissalLimitReported;
    private int _loadingGlobeCount;
    private readonly DelayPerformanceStore _delayPerformance = DelayPerformanceStore.Load();
    private PendingUpdate? _pendingUpdate;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => RegisterToggleHotkey();
        _timer.Tick += (_, _) => TickController();
        Loaded += (_, _) =>
        {
            SetupCard.Visibility = Visibility.Collapsed;
            SelectedServerCard.Visibility = Visibility.Collapsed;
            TargetText.Text = "ASA Join Last Played";
            RenderDelayPerformance();
            Log("Ready. ASA's Join Last Played server will be used; no app-side server selection is required.");
            _ = CheckForUpdateAsync();
        };
        Closed += (_, _) =>
        {
            if (_toggleHotkeyRegistered && _windowSource is not null)
                UnregisterHotKey(_windowSource.Handle, ToggleHotkeyId);
            _windowSource?.RemoveHook(WindowMessageHook);
            _timer.Stop(); _baseline?.Dispose(); _controller.Stop();
            ScreenPreview.Source = null;
            _latestScreenSource = null;
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

    private async void Detect_OnClick(object sender, RoutedEventArgs e) => await DetectLocalServers();

    private async Task DetectLocalServers()
    {
        var saved = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam", "steamapps", "common", "ARK Survival Ascended", "ShooterGame", "Saved");
        var last = AsaLocalDiscovery.FindLastJoined([Path.Combine(saved, "Config", "Windows", "GameUserSettings.ini")]);
        var favorites = AsaLocalDiscovery.FindFavorites([Path.Combine(saved, "SaveGames", "MenuPlayerLocalData.arkprofile.sav")]);
        var cached = LocalPreferences.Load();

        FavoritesBox.ItemsSource = favorites;
        if (last is not null)
        {
            ServerNameBox.Text = last.Name;
            FavoritesBox.SelectedItem = favorites.FirstOrDefault(x => x.Name.Equals(last.Name, StringComparison.OrdinalIgnoreCase));
        }
        if (cached.ServerName?.Equals(ServerNameBox.Text, StringComparison.OrdinalIgnoreCase) == true && !string.IsNullOrWhiteSpace(cached.Endpoint))
        {
            AddressBox.Text = cached.Endpoint;
            Log("Loaded the previously confirmed game address automatically.");
        }
        DetectedText.Text = last is null
            ? $"Found {favorites.Count} in-game favorite(s). Choose one below."
            : $"Last joined: {last.Name}  •  {favorites.Count} in-game favorite(s) found";
        if (FavoritesBox.SelectedItem is AsaFavoriteServer selected)
        {
            await ResolveAddress(selected);
            if (!string.IsNullOrWhiteSpace(AddressBox.Text)) CommitServerSelection();
        }
    }

    private void FavoritesBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UseFavoriteButton.IsEnabled = FavoritesBox.SelectedItem is AsaFavoriteServer;

    private async void UseFavorite_OnClick(object sender, RoutedEventArgs e)
    {
        if (FavoritesBox.SelectedItem is not AsaFavoriteServer favorite) return;
        ServerNameBox.Text = favorite.Name;
        var cached = LocalPreferences.Load();
        AddressBox.Text = cached.ServerName?.Equals(favorite.Name, StringComparison.OrdinalIgnoreCase) == true ? cached.Endpoint ?? "" : "";
        if (string.IsNullOrWhiteSpace(AddressBox.Text)) await ResolveAddress(favorite);
        else Log($"Selected {favorite.Name} and loaded its saved address.");
        if (!string.IsNullOrWhiteSpace(AddressBox.Text)) CommitServerSelection();
    }

    private void CommitServerSelection()
    {
        SelectedServerNameText.Text = ServerNameBox.Text;
        SelectedServerEndpointText.Text = AddressBox.Text;
        SetupCard.Visibility = Visibility.Collapsed;
        SelectedServerCard.Visibility = Visibility.Visible;
    }

    private void ChangeServer_OnClick(object sender, RoutedEventArgs e)
    {
        SelectedServerCard.Visibility = Visibility.Collapsed;
        SetupCard.Visibility = Visibility.Visible;
    }

    private async void Lookup_OnClick(object sender, RoutedEventArgs e)
    {
        if (FavoritesBox.SelectedItem is not AsaFavoriteServer favorite)
        {
            MessageBox.Show(this, "Choose an ASA favorite first. You can still type a game address manually.", "Address lookup");
            return;
        }
        await ResolveAddress(favorite);
    }

    private async Task ResolveAddress(AsaFavoriteServer favorite)
    {
        LookupButton.IsEnabled = false;
        LookupButton.Content = "RESOLVING…";
        AddressSourceText.Text = "Checking ASA's public server directory…";
        try
        {
            _server = await _directory.ResolveAsync(favorite.Name, favorite.Official, CancellationToken.None);
            AddressBox.Text = _server.Endpoint;
            TargetText.Text = $"{_server.DisplayName}\n{_server.Endpoint}";
            SavePreferences();
            AddressSourceText.Text = "Resolved automatically from ASA's public server directory. Manual override is available above.";
            Log($"ASA directory resolved the game address automatically: {_server.Endpoint}");
        }
        catch (Exception error)
        {
            AddressSourceText.Text = "Automatic resolution failed. Enter a game address manually or try Refresh Address again.";
            Log($"Automatic address lookup failed: {error.Message}");
        }
        finally { LookupButton.IsEnabled = true; LookupButton.Content = "REFRESH ADDRESS"; }
    }

    private void Target_OnChanged(object sender, TextChangedEventArgs e) => ResolveTarget(false);

    private bool ResolveTarget(bool showError)
    {
        if (ServerTarget.TryParse(AddressBox.Text, ServerNameBox.Text, out var target, out var error))
        {
            _server = target;
            TargetText.Text = $"{target!.DisplayName}\n{target.Endpoint}";
            return true;
        }
        _server = null;
        TargetText.Text = string.IsNullOrWhiteSpace(AddressBox.Text) ? "Address required" : "Invalid address";
        if (showError) MessageBox.Show(this, error, "Game address required");
        return false;
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
        _baseline?.Dispose(); _baseline = null; _attemptCount = 0;
        _networkDismissalAttemptAt = null; _networkDismissalAttemptCount = 0; _networkDismissalLimitReported = false;
        _lastJoinAttemptAt = null; _attemptIntervalSeconds = 0; _attemptIntervalCount = 0;
        AttemptsText.Text = "0"; AverageAttemptSecondsText.Text = "—"; SimilarityText.Text = "—";
        _controller.Start(10, DateTimeOffset.Now);
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
            Log("Network-failure dialog cleared.");
            MarkWorkflowProgress(now);
            _phase = JoinWorkflowPhase.WaitingAfterCancel;
            _nextWorkflowAction = now.AddMilliseconds(650);
        }

        if (isNetworkFailure) CaptureErrorEvidence("Network failure / server full", screen);
        else if (isOkFailure) CaptureErrorEvidence("Connection timeout", screen);
        else if (isModal) CaptureErrorEvidence("Connection / server full", screen);
        else _activeErrorEvidence = null;

        var canBeLoading = _phase == JoinWorkflowPhase.WaitingForJoinResult && isLoadingGlobe &&
                           !isNetworkFailure && !isOkFailure && !isModal && !isSessions && !isStartup && !isMainMenu;
        if (canBeLoading)
        {
            _loadingGlobeSince ??= now;
            if (now - _loadingGlobeSince >= TimeSpan.FromSeconds(2.5))
            {
                if (!_loadingGlobeCapturedForAttempt)
                {
                    _loadingGlobeCapturedForAttempt = true;
                    _loadingGlobeCount++;
                    LoadingGlobesText.Text = _loadingGlobeCount.ToString();
                    var performance = _delayPerformance.RecordGlobe(_attemptSpacingSeconds);
                    var ratio = performance.Attempts == 0 ? 0 : performance.LoadingGlobes / (double)performance.Attempts;
                    RenderDelayPerformance();
                    AddEvidence("Loading globe", screen);
                    Log("Loading globe confirmed; retry navigation is suspended while ASA loads or returns a post-load error.");
                    Log($"Delay optimization: {_attemptSpacingSeconds}s spacing has produced {performance.LoadingGlobes} globe(s) from {performance.Attempts} all-time attempt(s) ({ratio:P1}).");
                    MarkWorkflowProgress(now);
                }
            }
        }
        else _loadingGlobeSince = null;

        var inactiveFor = now - _lastWorkflowProgressAt;
        if (_preAlertRecoveryEnabled && now >= _nextRecoveryScanAt)
        {
            _nextRecoveryScanAt = now.AddSeconds(5);
                if (TryPreAlertRecovery(now, isNetworkFailure, isOkFailure, isModal, isStartup, isMainMenu)) return;
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
                DismissNetworkFailure(now, "Network-failure dialog detected");
                RenderWorkflow(); return;
            }
            else if (isOkFailure)
            {
                if (!WindowsInterop.ClickWindowDesignRelative(_ark, .500, .612)) { Stop("Could not click OK on the joining-failed dialog."); return; }
                Log("Joining-failed dialog detected; clicked OK.");
                MarkWorkflowProgress(now);
                _phase = JoinWorkflowPhase.WaitingAfterCancel; _nextWorkflowAction = now.AddMilliseconds(650);
                RenderWorkflow(); return;
            }
            else if (isModal)
            {
                if (!WindowsInterop.ClickWindowDesignRelative(_ark, .558, .675)) { Stop("Could not click CANCEL on the connection modal."); return; }
                Log("Connection/full modal detected; clicked CANCEL.");
                MarkWorkflowProgress(now);
                _phase = JoinWorkflowPhase.WaitingAfterCancel; _nextWorkflowAction = now.AddMilliseconds(650);
                RenderWorkflow(); return;
            }
            if (_phase == JoinWorkflowPhase.WaitingForJoinResult && now >= _phaseDeadline)
            {
                if (isSessions)
                {
                    if (!WindowsInterop.SendEscape()) { Stop("Could not send Escape when the configured attempt spacing elapsed."); return; }
                    Log($"Attempt spacing elapsed ({_attemptSpacingSeconds}s); sent Escape once to cancel the pending overlay.");
                    MarkWorkflowProgress(now);
                    _phase = JoinWorkflowPhase.WaitingForAttemptReset;
                    _nextWorkflowAction = now.AddMilliseconds(250);
                    _phaseDeadline = now.AddSeconds(12);
                }
                else
                {
                    Log("Join attempt remains off the known menu screens; continuing to watch for loading or an error.");
                    _phaseDeadline = now.AddSeconds(Math.Max(1, _attemptSpacingSeconds));
                }
            }
            RenderWorkflow(); return;
        }

        if (now < _nextWorkflowAction) { RenderWorkflow(); return; }
        switch (_phase)
        {
            case JoinWorkflowPhase.WaitingForAttemptReset:
                if (isSessions)
                {
                    _nextWorkflowAction = now.AddMilliseconds(250);
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
                }
                break;
            case JoinWorkflowPhase.Starting:
                if (isNetworkFailure)
                {
                    DismissNetworkFailure(now, "Existing network-failure dialog detected");
                }
                else if (isOkFailure)
                {
                    if (!WindowsInterop.ClickWindowDesignRelative(_ark, .500, .612)) { Stop("Could not click OK on the existing joining-failed dialog."); return; }
                    Log("Dismissed an existing joining-failed dialog.");
                    MarkWorkflowProgress(now);
                    _phase = JoinWorkflowPhase.WaitingAfterCancel; _nextWorkflowAction = now.AddMilliseconds(650);
                }
                else if (isModal)
                {
                    if (!WindowsInterop.ClickWindowDesignRelative(_ark, .558, .675)) { Stop("Could not cancel the existing connection modal."); return; }
                    Log("Cancelled an existing connection modal.");
                    MarkWorkflowProgress(now);
                    _phase = JoinWorkflowPhase.WaitingAfterCancel; _nextWorkflowAction = now.AddMilliseconds(650);
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
            case JoinWorkflowPhase.WaitingAfterCancel:
                if (!WindowsInterop.ClickWindowDesignRelative(_ark, .087, .811)) { Stop("Could not click BACK."); return; }
                Log("Clicked BACK.");
                MarkWorkflowProgress(now);
                _phase = JoinWorkflowPhase.WaitingAfterBack; _nextWorkflowAction = now.AddMilliseconds(250); _phaseDeadline = now.AddSeconds(12);
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
        RenderWorkflow();
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
        _loadingGlobeCapturedForAttempt = false;
        _loadingGlobeSince = null;
        AttemptsText.Text = (++_attemptCount).ToString();
        _delayPerformance.RecordAttempt(_attemptSpacingSeconds);
        RenderDelayPerformance();
        var attemptAt = DateTimeOffset.Now;
        if (_lastJoinAttemptAt is not null)
        {
            _attemptIntervalSeconds += (attemptAt - _lastJoinAttemptAt.Value).TotalSeconds;
            _attemptIntervalCount++;
            AverageAttemptSecondsText.Text = $"{_attemptIntervalSeconds / _attemptIntervalCount:F1}s";
        }
        _lastJoinAttemptAt = attemptAt;
        Log($"{action} (attempt {_attemptCount}).");
        _phase = JoinWorkflowPhase.WaitingForJoinResult;
        // ASA's "Joining server..." overlay is not a result and may never clear.
        // The user-configured attempt spacing is the retry deadline.
        _phaseDeadline = now.AddSeconds(_attemptSpacingSeconds);
    }

    private void MarkWorkflowProgress(DateTimeOffset now)
    {
        _lastWorkflowProgressAt = now;
        _inactivityAlertPlayed = false;
        _nextRecoveryScanAt = now.AddSeconds(4);
    }

    private bool TryPreAlertRecovery(DateTimeOffset now, bool isNetworkFailure, bool isOkFailure, bool isModal, bool isStartup, bool isMainMenu)
    {
        if (_ark is null) return false;
        if (isNetworkFailure)
        {
            return DismissNetworkFailure(now, "Recovery scan found a network-failure dialog");
        }
        if (isOkFailure)
        {
            if (!WindowsInterop.ClickWindowDesignRelative(_ark, .500, .612)) return false;
            Log("Recovery scan clicked OK on the joining-failed dialog.");
            MarkWorkflowProgress(now);
            _phase = JoinWorkflowPhase.WaitingAfterCancel;
            _nextWorkflowAction = now.AddMilliseconds(650);
            return true;
        }
        if (isModal)
        {
            if (!WindowsInterop.ClickWindowDesignRelative(_ark, .558, .675)) return false;
            Log("Recovery scan clicked CANCEL on the connection/full dialog.");
            MarkWorkflowProgress(now);
            _phase = JoinWorkflowPhase.WaitingAfterCancel;
            _nextWorkflowAction = now.AddMilliseconds(650);
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
                Log("Network-failure dialog remained after two dismiss clicks; stopped clicking and waiting for user attention.");
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

    private void RenderWorkflow()
    {
        StateText.Text = _phase switch
        {
            JoinWorkflowPhase.WaitingForJoinResult => "WAITING FOR RESULT",
            JoinWorkflowPhase.WaitingForAttemptReset => "RESETTING ATTEMPT",
            JoinWorkflowPhase.FilteringServer => "SELECTING SERVER",
            JoinWorkflowPhase.WaitingAfterCancel or JoinWorkflowPhase.WaitingAfterBack => "RESETTING MENU",
            _ => "NAVIGATING ASA"
        };
        NextAttemptText.Text = _phase.ToString();
    }

    private void RenderState(DateTimeOffset now)
    {
        var focused = _ark is not null && !_ark.HasExited && WindowsInterop.IsArkForeground(_ark);
        StateText.Text = _controller.State switch
        {
            RetryState.Arming when !focused => "RETURN TO ASA", RetryState.Arming => "CALIBRATING",
            RetryState.Ready when !focused => "PAUSED", RetryState.Ready => "READY",
            RetryState.Cooldown => "WAITING", RetryState.PausedForLoading => "JOINING / PAUSED", _ => "STOPPED"
        };
        NextAttemptText.Text = _controller.State is RetryState.Cooldown or RetryState.Arming
            ? $"{Math.Max(0, Math.Ceiling((_controller.NextAttemptAt - now).TotalSeconds))}s" : "paused";
        if (_lastStatus != StateText.Text)
        {
            _lastStatus = StateText.Text;
            DiagnosticLog.Write($"state={_controller.State} display={_lastStatus} asaForeground={focused}");
        }
    }

    private void Stop(string reason)
    {
        _timer.Stop(); _controller.Stop(); _baseline?.Dispose(); _baseline = null;
        SetRunning(false); StateText.Text = "STOPPED"; NextAttemptText.Text = "—"; Log(reason);
    }

    private void CompleteJoin(string message)
    {
        _phase = JoinWorkflowPhase.Idle;
        _timer.Stop();
        _controller.Stop();
        _baseline?.Dispose();
        _baseline = null;
        SetRunning(false);
        StateText.Text = "JOINING / LOADING";
        NextAttemptText.Text = "stopped safely";
        Log(message);
    }

    private void SetRunning(bool running)
    {
        FavoritesBox.IsEnabled = UseFavoriteButton.IsEnabled = ServerNameBox.IsEnabled = AddressBox.IsEnabled =
            LookupButton.IsEnabled = !running;
        DelaySecondsBox.IsEnabled = !running;
        PreAlertRecoveryBox.IsEnabled = !running;
        StartButton.IsEnabled = !running; StopButton.IsEnabled = running;
        SetupCard.Visibility = Visibility.Collapsed;
        SelectedServerCard.Visibility = Visibility.Collapsed;
    }

    private void UpdateScreenPreview(Bitmap screen)
    {
        _previewAspectRatio = screen.Width / (double)screen.Height;
        ScreenPreviewBorder.AspectRatio = _previewAspectRatio;
        var handle = screen.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            _latestScreenSource = source;
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

    private void SavePreferences()
    {
        if (_server is null) return;
        new LocalPreferences(_server.DisplayName, _server.Endpoint, 10).Save();
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

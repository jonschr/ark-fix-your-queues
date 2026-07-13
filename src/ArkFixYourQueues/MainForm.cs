using System.Diagnostics;

namespace ArkFixYourQueues;

public sealed class MainForm : Form
{
    private readonly TextBox _serverName = new() { PlaceholderText = "Server name (editable)" };
    private readonly TextBox _address = new() { PlaceholderText = "Direct game address, such as 1.2.3.4:7777" };
    private readonly TextBox _apiKey = new() { PlaceholderText = "ARK Status API key (never saved)", UseSystemPasswordChar = true };
    private readonly Button _search = new() { Text = "LOOK UP ADDRESS" };
    private readonly Button _detect = new() { Text = "REFRESH LIST" };
    private readonly ComboBox _results = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _useResult = new() { Text = "USE FAVORITE", Enabled = false };
    private readonly NumericUpDown _interval = new() { Minimum = RetryController.MinimumRetrySeconds, Maximum = 300, Value = 10 };
    private readonly Button _arm = new() { Text = "START AUTO-RETRY", Height = 52 };
    private readonly Button _stop = new() { Text = "STOP RETRYING", Height = 52, Enabled = false };
    private readonly Label _state = Metric("STOPPED");
    private readonly Label _target = Metric("Not configured");
    private readonly Label _attempts = Metric("0");
    private readonly Label _similarity = Metric("—");
    private readonly Label _nextAttempt = Metric("—");
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly Label _detected = new() { Text = "Local discovery has not run", AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly RetryController _controller = new();
    private readonly ArkStatusClient _arkStatus = new();

    private Process? _ark;
    private Bitmap? _baseline;
    private ServerTarget? _server;
    private CancellationTokenSource? _searchCancellation;
    private int _attemptCount;

    public MainForm()
    {
        Text = "ARK: Fix Your Queues";
        MinimumSize = new Size(1060, 820);
        Size = new Size(1120, 900);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(245, 245, 245);
        ForeColor = Color.FromArgb(24, 24, 27);
        Font = new Font("Segoe UI Variable Text", 10);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(28), RowCount = 5, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(Header(), 0, 0);
        root.Controls.Add(ServerCard(), 0, 1);
        root.Controls.Add(ControlCard(), 0, 2);
        root.Controls.Add(Metrics(), 0, 3);
        root.Controls.Add(LogCard(), 0, 4);
        Controls.Add(root);

        _search.Click += async (_, _) => await SearchServers();
        _detect.Click += (_, _) => DetectLocalServers();
        _useResult.Click += async (_, _) => await UseSelectedServer();
        _results.SelectedIndexChanged += (_, _) => _useResult.Enabled = _results.SelectedItem is ArkStatusSearchResult or AsaFavoriteServer;
        _address.TextChanged += (_, _) => ResolveManualTarget(false);
        _serverName.TextChanged += (_, _) => { if (_server?.ProviderId is null) ResolveManualTarget(false); };
        _arm.Click += (_, _) => Arm();
        _stop.Click += (_, _) => Stop("Stopped by user.");
        _timer.Tick += (_, _) => TickController();
        FormClosing += (_, _) => { _searchCancellation?.Cancel(); _controller.Stop(); };
        Shown += (_, _) => DetectLocalServers();
        ApplyPolish();
    }

    private Control Header()
    {
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0, 0, 0, 14) };
        panel.Controls.Add(new Label { Text = "ARK Join Retry", AutoSize = true, Font = new Font(Font.FontFamily, 24, FontStyle.Bold), ForeColor = Color.FromArgb(24, 24, 27) });
        panel.Controls.Add(new Label { Text = "Select a server, confirm its address, then start. Retries pause when ASA leaves the join screen.", AutoSize = true, ForeColor = Color.FromArgb(82, 82, 91) });
        return panel;
    }

    private Control ServerCard()
    {
        var grid = Card(7);
        var title = SectionTitle("1  Choose a server");
        grid.Controls.Add(title, 0, 0);
        grid.SetColumnSpan(title, 2);
        grid.Controls.Add(FieldLabel("ASA FAVORITES"), 0, 1);
        var favoriteRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        favoriteRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        favoriteRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        favoriteRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        favoriteRow.Controls.Add(_results, 0, 0);
        favoriteRow.Controls.Add(_useResult, 1, 0);
        favoriteRow.Controls.Add(_detect, 2, 0);
        grid.Controls.Add(favoriteRow, 1, 1);
        grid.Controls.Add(FieldLabel("FOUND LOCALLY"), 0, 2);
        grid.Controls.Add(_detected, 1, 2);
        grid.Controls.Add(FieldLabel("SERVER NAME"), 0, 3);
        grid.Controls.Add(_serverName, 1, 3);
        grid.Controls.Add(FieldLabel("GAME ADDRESS"), 0, 4);
        grid.Controls.Add(_address, 1, 4);
        grid.Controls.Add(FieldLabel("LOOK UP ADDRESS"), 0, 5);
        var searchRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchRow.Controls.Add(_apiKey, 0, 0);
        searchRow.Controls.Add(_search, 1, 0);
        searchRow.Controls.Add(_useResult, 2, 0);
        grid.Controls.Add(searchRow, 1, 5);
        grid.Controls.Add(new Label { Text = "Optional: use your ARK Status key to refresh the selected server's address. You can always type an address above.", AutoSize = true, ForeColor = Color.FromArgb(82, 82, 91), MaximumSize = new Size(780, 0) }, 1, 6);
        return grid;
    }

    private Control ControlCard()
    {
        var grid = Card(4);
        var title = SectionTitle("2  Start retrying");
        grid.Controls.Add(title, 0, 0);
        grid.SetColumnSpan(title, 2);
        grid.Controls.Add(FieldLabel("TIME BETWEEN ATTEMPTS"), 0, 1);
        var intervalRow = new FlowLayoutPanel { AutoSize = true };
        intervalRow.Controls.Add(_interval);
        intervalRow.Controls.Add(new Label { Text = "seconds  •  default: 10  •  minimum: 2", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(6, 6, 0, 0) });
        grid.Controls.Add(intervalRow, 1, 1);
        grid.Controls.Add(new Label { Text = "Start Auto-Retry gives you 3 seconds to return to ASA. It sends commands only while ASA is already focused. Stop Retrying ends the run immediately.", AutoSize = true, ForeColor = Color.FromArgb(82, 82, 91), MaximumSize = new Size(800, 0), Padding = new Padding(0, 4, 0, 10) }, 1, 2);
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        buttons.Controls.Add(_arm, 0, 0);
        buttons.Controls.Add(_stop, 1, 0);
        grid.Controls.Add(buttons, 0, 3);
        grid.SetColumnSpan(buttons, 2);
        return grid;
    }

    private Control Metrics()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 5, Margin = new Padding(0, 12, 0, 12) };
        for (var i = 0; i < 5; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        AddMetric(grid, 0, "CURRENT STATUS", _state);
        AddMetric(grid, 1, "SELECTED SERVER", _target);
        AddMetric(grid, 2, "COMMANDS SENT", _attempts);
        AddMetric(grid, 3, "JOIN SCREEN MATCH", _similarity);
        AddMetric(grid, 4, "NEXT RETRY", _nextAttempt);
        return grid;
    }

    private Control LogCard()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(14), BackColor = Color.White };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(FieldLabel("ACTIVITY"), 0, 0);
        panel.Controls.Add(_log, 0, 1);
        return panel;
    }

    private async Task SearchServers()
    {
        if (string.IsNullOrWhiteSpace(_apiKey.Text) || string.IsNullOrWhiteSpace(_serverName.Text))
        {
            MessageBox.Show("Enter a server name and your ARK Status API key. The key is not saved.", "Resolver details required");
            return;
        }
        _searchCancellation?.Cancel();
        _searchCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _search.Enabled = false;
        _search.Text = "Searching…";
        try
        {
            var results = await _arkStatus.SearchAsync(_apiKey.Text, _serverName.Text, _searchCancellation.Token);
            _results.Items.Clear();
            _results.Items.AddRange(results.Cast<object>().ToArray());
            if (_results.Items.Count > 0) _results.SelectedIndex = 0;
            Log($"Resolver found {results.Count} matching server(s).");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log($"Resolver error: {error.Message}");
            MessageBox.Show(error.Message, "Server search failed");
        }
        finally
        {
            _search.Enabled = true;
            _search.Text = "LOOK UP ADDRESS";
        }
    }

    private async Task UseSelectedServer()
    {
        if (_results.SelectedItem is AsaFavoriteServer favorite)
        {
            _serverName.Text = favorite.Name;
            var cached = LocalPreferences.Load();
            if (cached.ServerName?.Equals(favorite.Name, StringComparison.OrdinalIgnoreCase) == true && !string.IsNullOrWhiteSpace(cached.Endpoint))
                _address.Text = cached.Endpoint;
            Log($"Selected ASA favorite: {favorite.Name}. " + (string.IsNullOrWhiteSpace(_address.Text) ? "Resolve it with ARK Status or enter an endpoint override." : "Loaded its cached endpoint; verify before arming."));
            return;
        }
        if (_results.SelectedItem is not ArkStatusSearchResult result) return;
        _useResult.Enabled = false;
        try
        {
            _server = await _arkStatus.ResolveAsync(_apiKey.Text, result, CancellationToken.None);
            _serverName.Text = _server.DisplayName;
            _address.Text = _server.Endpoint;
            UpdateTarget();
            Log($"Resolved provider server #{_server.ProviderId}: {_server.DisplayName} → {_server.Endpoint}");
            SavePreferences();
        }
        catch (Exception error)
        {
            Log($"Could not resolve selected server: {error.Message}");
            MessageBox.Show(error.Message, "Resolution failed");
        }
        finally { _useResult.Enabled = true; }
    }

    private bool ResolveManualTarget(bool showError)
    {
        if (ServerTarget.TryParse(_address.Text, _serverName.Text, out var target, out var error))
        {
            if (_server?.Endpoint != target!.Endpoint) _server = target;
            UpdateTarget();
            return true;
        }
        _server = null;
        _target.Text = "Not configured";
        if (showError) MessageBox.Show(error, "Invalid server endpoint");
        return false;
    }

    private void Arm()
    {
        if (!ResolveManualTarget(true) || _server is null) return;
        _ark = WindowsInterop.FindArk();
        if (_ark is null)
        {
            MessageBox.Show("ArkAscended.exe is not running.", "ASA not found");
            return;
        }
        _baseline?.Dispose();
        _baseline = null;
        _attemptCount = 0;
        _attempts.Text = "0";
        _similarity.Text = "—";
        _controller.Start((int)_interval.Value, DateTimeOffset.Now);
        SavePreferences();
        SetInputsEnabled(false);
        Log($"Auto-retry started for {_server.DisplayName} ({_server.Endpoint}). Switch to ASA now.");
        _timer.Start();
    }

    private void TickController()
    {
        if (_ark is null || _ark.HasExited) { Stop("ASA closed; stopped safely."); return; }
        var now = DateTimeOffset.Now;
        if (_controller.State == RetryState.Arming && now >= _controller.NextAttemptAt)
        {
            if (!WindowsInterop.IsArkForeground(_ark)) { RenderState(now); return; }
            _baseline = WindowsInterop.CaptureWindow(_ark);
            if (_baseline is null) { Stop("Could not capture ASA; stopped safely."); return; }
            _controller.FinishArming(now);
            Log("Join screen calibrated. The helper will pause when this screen changes materially.");
        }

        if (_baseline is not null && _controller.State is RetryState.Cooldown or RetryState.PausedForLoading)
        {
            using var current = WindowsInterop.CaptureWindow(_ark);
            if (current is null) { Stop("Screen capture failed; stopped safely."); return; }
            var difference = WindowsInterop.Difference(_baseline, current);
            var resemblesBaseline = difference < 0.12;
            _similarity.Text = $"{Math.Clamp(1 - difference, 0, 1):P0}";
            var before = _controller.State;
            _controller.ObserveScreen(resemblesBaseline, now);
            if (before != _controller.State && _controller.State == RetryState.PausedForLoading)
                Log("Screen changed: attempts paused for joining/loading/gameplay.");
            else if (before == RetryState.PausedForLoading && _controller.State == RetryState.Ready)
                Log("Calibrated join screen returned: attempts may resume.");
        }

        if (_server is not null && _controller.ShouldAttempt(now, WindowsInterop.IsArkForeground(_ark)))
        {
            if (!WindowsInterop.SendJoinCommand(_server.JoinCommand)) { Stop("Windows rejected input; stopped safely."); return; }
            _controller.Attempted(now);
            _attemptCount++;
            _attempts.Text = _attemptCount.ToString();
            Log($"Attempt {_attemptCount}: {_server.JoinCommand}. Next possible attempt in {_controller.RetrySeconds}s.");
        }
        RenderState(now);
    }

    private void RenderState(DateTimeOffset now)
    {
        var focused = _ark is not null && !_ark.HasExited && WindowsInterop.IsArkForeground(_ark);
        _state.Text = _controller.State switch
        {
            RetryState.Arming when !focused => "WAITING FOR ASA",
            RetryState.Arming => "CALIBRATING",
            RetryState.Ready when !focused => "PAUSED: UNFOCUSED",
            RetryState.Ready => "READY",
            RetryState.Cooldown => "COOLDOWN",
            RetryState.PausedForLoading => "PAUSED: JOINING",
            _ => "STOPPED"
        };
        _state.ForeColor = Color.FromArgb(24, 24, 27);
        _nextAttempt.Text = _controller.State switch
        {
            RetryState.Cooldown or RetryState.Arming => $"{Math.Max(0, Math.Ceiling((_controller.NextAttemptAt - now).TotalSeconds))}s",
            RetryState.Ready when focused => "now",
            _ => "paused"
        };
    }

    private void Stop(string reason)
    {
        _timer.Stop();
        _controller.Stop();
        _baseline?.Dispose();
        _baseline = null;
        SetInputsEnabled(true);
        _state.Text = "STOPPED";
        _nextAttempt.Text = "—";
        Log(reason);
    }

    private void SetInputsEnabled(bool enabled)
    {
        _serverName.Enabled = enabled;
        _address.Enabled = enabled;
        _apiKey.Enabled = enabled;
        _detect.Enabled = enabled;
        _search.Enabled = enabled;
        _results.Enabled = enabled;
        _useResult.Enabled = enabled && _results.SelectedItem is ArkStatusSearchResult or AsaFavoriteServer;
        _interval.Enabled = enabled;
        _arm.Enabled = enabled;
        _stop.Enabled = !enabled;
    }

    private void UpdateTarget() => _target.Text = _server is null ? "Not configured" : $"{_server.DisplayName}\n{_server.Endpoint}";
    private void Log(string message) => _log.AppendText($"[{DateTime.Now:T}] {message}{Environment.NewLine}");

    private static TableLayoutPanel Card(int rows)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = rows, Padding = new Padding(18), Margin = new Padding(0, 0, 0, 14), BackColor = Color.White };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    private static Label FieldLabel(string text) => new() { Text = text, AutoSize = true, ForeColor = Color.FromArgb(82, 82, 91), Font = new Font("Segoe UI", 8, FontStyle.Bold), Anchor = AnchorStyles.Left, Margin = new Padding(3, 11, 12, 5) };
    private static Label SectionTitle(string text) => new() { Text = text, AutoSize = true, Font = new Font("Segoe UI", 14, FontStyle.Bold), ForeColor = Color.FromArgb(24, 24, 27), Margin = new Padding(0, 0, 0, 12) };
    private static Label Metric(string value) => new() { Text = value, AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold), ForeColor = Color.FromArgb(24, 24, 27), MaximumSize = new Size(180, 0) };
    private static void AddMetric(TableLayoutPanel grid, int column, string title, Label value)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12), Margin = new Padding(0, 0, 8, 0), BackColor = Color.White };
        panel.Controls.Add(FieldLabel(title));
        panel.Controls.Add(value);
        grid.Controls.Add(panel, column, 0);
    }

    private void DetectLocalServers()
    {
        var install = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "ARK Survival Ascended", "ShooterGame", "Saved");
        var settings = Path.Combine(install, "Config", "Windows", "GameUserSettings.ini");
        var profile = Path.Combine(install, "SaveGames", "MenuPlayerLocalData.arkprofile.sav");
        var last = AsaLocalDiscovery.FindLastJoined([settings]);
        var favorites = AsaLocalDiscovery.FindFavorites([profile]);
        var cached = LocalPreferences.Load();

        _results.Items.Clear();
        _results.Items.AddRange(favorites.Cast<object>().ToArray());
        if (last is not null)
        {
            _serverName.Text = last.Name;
            var index = favorites.ToList().FindIndex(item => item.Name.Equals(last.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _results.SelectedIndex = index;
        }
        if (cached.ServerName?.Equals(_serverName.Text, StringComparison.OrdinalIgnoreCase) == true && !string.IsNullOrWhiteSpace(cached.Endpoint))
            _address.Text = cached.Endpoint;
        _interval.Value = Math.Clamp(cached.RetrySeconds, (int)_interval.Minimum, (int)_interval.Maximum);
        _detected.Text = last is null
            ? $"Found {favorites.Count} ASA favorite(s); no last server was recorded."
            : $"Last joined: {last.Name}  •  {favorites.Count} in-game favorite(s)";
        _detected.ForeColor = Color.FromArgb(63, 63, 70);
        Log(_detected.Text);
    }

    private void SavePreferences()
    {
        if (_server is null) return;
        new LocalPreferences(_server.DisplayName, _server.Endpoint, (int)_interval.Value).Save();
    }

    private void ApplyPolish()
    {
        foreach (var button in new[] { _detect, _search, _useResult, _arm, _stop })
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(161, 161, 170);
            button.Padding = new Padding(12, 7, 12, 7);
            button.MinimumSize = new Size(0, 38);
            button.Cursor = Cursors.Hand;
        }
        _arm.BackColor = Color.FromArgb(22, 163, 74);
        _arm.ForeColor = Color.White;
        _stop.BackColor = Color.FromArgb(225, 29, 72);
        _stop.ForeColor = Color.White;
        _arm.FlatAppearance.BorderSize = _stop.FlatAppearance.BorderSize = 0;
        _detect.BackColor = _search.BackColor = _useResult.BackColor = Color.FromArgb(244, 244, 245);
        _detect.ForeColor = _search.ForeColor = _useResult.ForeColor = Color.FromArgb(24, 24, 27);
        _detected.ForeColor = Color.FromArgb(82, 82, 91);
        _log.BackColor = Color.White;
        _log.ForeColor = Color.FromArgb(39, 39, 42);
        foreach (var input in new Control[] { _serverName, _address, _apiKey, _results, _interval })
        {
            input.Font = new Font("Segoe UI", 11);
            input.MinimumSize = new Size(0, 36);
            input.Margin = new Padding(3, 5, 8, 5);
            input.BackColor = Color.White;
            input.ForeColor = Color.FromArgb(24, 24, 27);
        }
        _results.IntegralHeight = false;
        _results.DropDownHeight = 240;
        _results.DropDownWidth = 720;
    }
}

using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace OpenXLR.UI;

/// <summary>One entry in the enforced-default pickers; Name null = don't enforce.</summary>
public sealed record DeviceChoice(string? Name, string Label);

/// <summary>
/// Backs the Options window. Startup toggles apply immediately to the system
/// (systemd unit, autostart entry) and persist in ui.json; the enforced-default
/// pickers go straight to the daemon, which owns that setting.
/// </summary>
public sealed class OptionsViewModel : ViewModelBase
{
    private readonly DaemonClient _client;
    private readonly MainViewModel _main;
    private bool _applying;

    /// <summary>Exposed for the diagnostics collector in the Options window.</summary>
    public DaemonClient Client => _client;
    public UpdatesViewModel Updates => _main.Updates;

    /// <summary>The main view model, for the interface reset that lives in Options.</summary>
    public MainViewModel Main => _main;

    public OptionsViewModel(DaemonClient client, MainViewModel main)
    {
        _client = client;
        _main = main;

        UiSettings s = UiSettings.Load();
        _startDaemonAtLogin = s.StartDaemonAtLogin;
        _openWindowAtLogin = s.OpenWindowAtLogin;
        _minimizeToTray = s.MinimizeToTray;
        _startMinimized = s.StartMinimized;
        _checkForUpdates = s.CheckForUpdates;
        _startupError = RepairNote(StartupIntegration.LastRepair);
        // No saved choice means the daemon runs whatever its unit asked for,
        // which for every shipped unit is the submixer on.
        _submixer = DaemonPrefs.Load().Submixer ?? true;

        BuildChoices();
        _applying = true;
        try
        {
            EnforcedOutput = OutputChoices.FirstOrDefault(c => c.Name == main.EnforcedDefaultSink) ?? OutputChoices[0];
            EnforcedInput = InputChoices.FirstOrDefault(c => c.Name == main.EnforcedDefaultSource) ?? InputChoices[0];
        }
        finally { _applying = false; }
    }

    // --- plugins ---

    private string _pluginDirectories = "Plugins are looked for in the home and system plugin directories.";
    /// <summary>Where installs go, once the daemon has said.</summary>
    public string PluginDirectories { get => _pluginDirectories; private set => Set(ref _pluginDirectories, value); }

    private string _windowsPlugins = "Windows plugins: checking for yabridge…";
    /// <summary>yabridge and Wine as found, and how many folders are bridged.</summary>
    public string WindowsPlugins { get => _windowsPlugins; private set => Set(ref _windowsPlugins, value); }

    private bool _canSyncWindows;
    public bool CanSyncWindows { get => _canSyncWindows; private set => Set(ref _canSyncWindows, value); }

    private string? _windowsImportNote;
    public string? WindowsImportNote { get => _windowsImportNote; private set => Set(ref _windowsImportNote, value); }

    public ObservableCollection<string> WindowsDirectories { get; } = [];
    public bool HasWindowsDirectories => WindowsDirectories.Count > 0;

    private bool _canManageWindows;
    public bool CanManageWindows { get => _canManageWindows; private set => Set(ref _canManageWindows, value); }

    private bool _systemBridge;
    public bool SystemBridge { get => _systemBridge; private set => Set(ref _systemBridge, value); }

    /// <summary>Wine's own plugin folders waiting to be bridged, absolute.</summary>
    public System.Collections.Generic.IReadOnlyList<string> WineFolders { get; private set; } = [];

    private string _bridgeWineLabel = "Bridge Wine's plugins";
    /// <summary>What the button offers, named after what it will bridge.</summary>
    public string BridgeWineLabel { get => _bridgeWineLabel; private set => Set(ref _bridgeWineLabel, value); }

    private string? _windowsEditorNote;
    /// <summary>What to know before opening a bridged plugin's own editor, or null.</summary>
    public string? WindowsEditorNote { get => _windowsEditorNote; private set { if (Set(ref _windowsEditorNote, value)) Raise(nameof(HasWindowsEditorNote)); } }

    public bool HasWindowsEditorNote => !string.IsNullOrEmpty(_windowsEditorNote);

    private bool _canBridgeWine;
    /// <summary>
    /// Whether Wine holds plugins nobody has bridged yet. The button spares
    /// the user a file dialog: Wine keeps them under a dot directory, which
    /// a file dialog hides until the user knows to ask for hidden folders.
    /// </summary>
    public bool CanBridgeWine { get => _canBridgeWine; private set => Set(ref _canBridgeWine, value); }

    /// <summary>Ask the daemon where plugins go and what is there to bridge Windows ones.</summary>
    public async System.Threading.Tasks.Task LoadPluginSetupAsync()
    {
        System.Text.Json.Nodes.JsonNode? setup = await _client.RequestPluginSetupAsync(TimeSpan.FromSeconds(10));
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ApplyPluginSetup(setup));
    }

    internal void ApplyPluginSetup(System.Text.Json.Nodes.JsonNode? setup)
    {
        if (setup is null)
        {
            WindowsPlugins = "Windows plugins: the daemon did not answer.";
            CanSyncWindows = false;
            CanManageWindows = false;
            return;
        }
        string lv2 = setup["lv2Directory"]?.GetValue<string>() ?? "~/.lv2";
        string clap = setup["clapDirectory"]?.GetValue<string>() ?? "~/.clap";
        string vst3 = setup["vst3Directory"]?.GetValue<string>() ?? "~/.vst3";
        bool host = setup["hostInstalled"]?.GetValue<bool>() ?? true;
        PluginDirectories = $"Plugins are looked for in {lv2}, {clap} and {vst3} and the system plugin directories."
            + (host ? "" : " The native plugin host is not installed beside the daemon, so CLAP and VST3 plugins cannot run.");
        WindowsEditorNote = setup["windowsEditorNote"]?.GetValue<string>();
        string? yabridge = setup["yabridge"]?.GetValue<string>();
        bool wine = setup["wine"]?.GetValue<bool>() ?? false;
        int folders = (setup["windowsDirectories"] as System.Text.Json.Nodes.JsonArray)?.Count ?? 0;
        WindowsPlugins = WindowsLine(yabridge, wine, folders, setup["bridgeProvider"]?.GetValue<string>() == "openxlr");
        if (setup["windowsPluginDirectory"]?.GetValue<string>() is { } managedDirectory)
            PluginDirectories += $" OpenXLR's Windows plugin wrappers are in {managedDirectory}.";
        WindowsImportNote = setup["windowsImportDirectory"]?.GetValue<string>() is { } imports
            ? $"Single-plugin imports are kept in {imports}." : null;
        CanSyncWindows = yabridge is not null && wine;
        CanManageWindows = yabridge is not null;
        SystemBridge = yabridge is not null && setup["bridgeProvider"]?.GetValue<string>() != "openxlr";
        WindowsDirectories.Clear();
        foreach (string directory in (setup["windowsDirectories"] as System.Text.Json.Nodes.JsonArray ?? [])
            .Select(f => f?.GetValue<string>()).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            WindowsDirectories.Add(directory);
        Raise(nameof(HasWindowsDirectories));
        WineFolders = [.. (setup["wineFolders"] as System.Text.Json.Nodes.JsonArray ?? [])
            .Select(f => f?.GetValue<string>()).OfType<string>()];
        CanBridgeWine = WineFolders.Count > 0;
        BridgeWineLabel = WineFolders.Count > 1
            ? $"Bridge Wine's {WineFolders.Count} plugin folders"
            : "Bridge Wine's plugins";
    }

    /// <summary>One line on Windows plugins, from what the daemon found.</summary>
    internal static string WindowsLine(string? yabridge, bool wine, int folders, bool managed = false)
    {
        if (yabridge is null && !wine) return "Windows plugins: yabridge and Wine are not installed. Install the optional openxlr-yabridge package and Wine, then install a Windows VST3 or CLAP plugin here.";
        if (yabridge is null) return "Windows plugins: Wine is installed, yabridge is not. Install the optional openxlr-yabridge package, then install a Windows VST3 or CLAP plugin here.";
        string bridge = managed ? $"OpenXLR bridge {yabridge} (64-bit)" : $"yabridge {yabridge}";
        if (!wine) return $"Windows plugins: {bridge} is installed, Wine is not. Install Wine from your distribution.";
        string bridged = folders switch { 0 => "no folder bridged yet", 1 => "1 folder bridged", _ => $"{folders} folders bridged" };
        return $"Windows plugins: {bridge} and Wine are installed, {bridged}. Run a plugin's installer with Wine, then install the folder it created.";
    }

    // --- startup behaviour ---

    private bool _checkForUpdates;
    public bool CheckForUpdates
    {
        get => _checkForUpdates;
        set { if (Set(ref _checkForUpdates, value)) Persist(); }
    }

    private bool _startDaemonAtLogin;
    public bool StartDaemonAtLogin
    {
        get => _startDaemonAtLogin;
        set
        {
            if (_startDaemonAtLogin == value) return;
            if (!StartupIntegration.SetDaemonAtLogin(value))
            {
                StartupError = value
                    ? "Could not start the audio service at login. Check that OpenXLR is installed and that systemd user services work here (systemctl --user status)."
                    : "Could not stop the audio service from starting at login. Check that systemd user services work here (systemctl --user status).";
                Reject(ref _startDaemonAtLogin, value, nameof(StartDaemonAtLogin));
                return;
            }
            StartupError = null;
            _startDaemonAtLogin = value;
            Raise();
            Raise(nameof(StartupHint));
            Persist();
        }
    }

    private bool _openWindowAtLogin;
    public bool OpenWindowAtLogin
    {
        get => _openWindowAtLogin;
        set
        {
            if (_openWindowAtLogin == value) return;
            if (!StartupIntegration.SetWindowAtLogin(value))
            {
                StartupError = "Could not update mixer autostart. Check that OpenXLR is installed, the autostart folder is writable, and the entry there is not a symbolic link.";
                Reject(ref _openWindowAtLogin, value, nameof(OpenWindowAtLogin));
                return;
            }
            StartupError = null;
            _openWindowAtLogin = value;
            Raise();
            Raise(nameof(StartupHint));
            Persist();
        }
    }

    /// <summary>
    /// Put a rejected toggle back where it was. The check box has already
    /// drawn itself in the new state, and a notification carrying the value
    /// the property already had does not move it: the binding compares
    /// against what it last wrote and pushes nothing. Publishing the rejected
    /// value and then the real one does move it.
    /// </summary>
    private void Reject(ref bool field, bool rejected, string name)
    {
        field = rejected;
        Raise(name);
        field = !rejected;
        Raise(name);
    }

    private string? _startupError;
    public string? StartupError { get => _startupError; private set => Set(ref _startupError, value); }

    /// <summary>What the startup repair did, in words, or null when it did nothing worth saying.</summary>
    internal static string? RepairNote(StartupIntegration.AutostartRepair repair) => repair switch
    {
        StartupIntegration.AutostartRepair.RemovedOutside =>
            "The mixer autostart entry was removed outside OpenXLR, so it was left removed. Turn this option off and on again to create it.",
        StartupIntegration.AutostartRepair.Failed =>
            "Could not repair the mixer autostart entry. Check that OpenXLR is installed, the autostart folder is writable, and the entry there is not a symbolic link.",
        _ => null,
    };

    public string StartupHint => (StartDaemonAtLogin, OpenWindowAtLogin) switch
    {
        (true, true) => "Audio and the app will start when you sign in.",
        (false, true) => "The app will start at login. Start the audio service separately to use it.",
        (true, false) => "Audio will start at login without the app or tray icon.",
        _ => "Neither audio nor the app will start at login.",
    };

    // The selectors keep the existing saved booleans, including their defaults.
    public int LaunchBehavior
    {
        get => StartMinimized ? 1 : 0;
        set { if (value is 0 or 1) StartMinimized = value == 1; }
    }

    public int CloseBehavior
    {
        get => MinimizeToTray ? 0 : 1;
        set { if (value is 0 or 1) MinimizeToTray = value == 0; }
    }

    private bool _minimizeToTray;
    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set
        {
            if (!Set(ref _minimizeToTray, value)) return;
            Raise(nameof(CloseBehavior));
            Persist();
            _main.MinimizeToTray = value;
        }
    }

    private bool _startMinimized;
    public bool StartMinimized
    {
        get => _startMinimized;
        set
        {
            if (!Set(ref _startMinimized, value)) return;
            Raise(nameof(LaunchBehavior));
            Persist();
        }
    }

    // --- submixer on/off (daemon-side setting, applied by restarting it) ---

    private bool _submixer;
    public bool Submixer
    {
        get => _submixer;
        set
        {
            if (!Set(ref _submixer, value)) return;
            try
            {
                new DaemonPrefs { Submixer = value }.Save();
            }
            catch (Exception ex)
            {
                SubmixerNote = $"Could not save the setting: {ex.Message}";
                return;
            }
            SubmixerNote = StartupIntegration.RestartDaemon()
                ? (value ? "Daemon restarted with the submixer on."
                         : "Daemon restarted in hardware-control mode; the sound card keeps its stock layout and inserts are not loaded.")
                : "Saved. Restart the daemon to apply (systemctl --user restart openxlr-daemon).";
        }
    }

    private string? _submixerNote;
    public string? SubmixerNote
    {
        get => _submixerNote;
        private set => Set(ref _submixerNote, value);
    }

    // Start from the file so fields owned elsewhere (the main window's
    // collapsed tiles) survive a save from here.
    private void Persist() => (UiSettings.Load() with
    {
        StartDaemonAtLogin = _startDaemonAtLogin,
        OpenWindowAtLogin = _openWindowAtLogin,
        MinimizeToTray = _minimizeToTray,
        StartMinimized = _startMinimized,
        CheckForUpdates = _checkForUpdates,
    }).Save();

    // --- enforced system defaults ---

    public ObservableCollection<DeviceChoice> OutputChoices { get; } = [];
    public ObservableCollection<DeviceChoice> InputChoices { get; } = [];

    private DeviceChoice? _enforcedOutput;
    public DeviceChoice? EnforcedOutput
    {
        get => _enforcedOutput;
        set { if (Set(ref _enforcedOutput, value) && !_applying) SendEnforced(); }
    }

    private DeviceChoice? _enforcedInput;
    public DeviceChoice? EnforcedInput
    {
        get => _enforcedInput;
        set { if (Set(ref _enforcedInput, value) && !_applying) SendEnforced(); }
    }

    private void SendEnforced()
        => _ = _client.SetEnforcedDefaultsAsync(_enforcedOutput?.Name, _enforcedInput?.Name);

    private void BuildChoices()
    {
        OutputChoices.Add(new DeviceChoice(null, "(don't enforce)"));
        // "#phones" entries are channel-pair routing targets, not real sinks a
        // system default can point to.
        foreach (AudioDeviceItem d in _main.Outputs.Where(d => !d.Name.Contains("#phones", StringComparison.Ordinal)))
            OutputChoices.Add(new DeviceChoice(d.Name, d.Label));

        InputChoices.Add(new DeviceChoice(null, "(don't enforce)"));
        foreach (AudioDeviceItem d in _main.Inputs)
            InputChoices.Add(new DeviceChoice(d.Name, d.Label));
    }
}

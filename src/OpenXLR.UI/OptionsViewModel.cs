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
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyPluginSetup(setup));
    }

    internal void ApplyPluginSetup(System.Text.Json.Nodes.JsonNode? setup)
    {
        if (setup is null)
        {
            WindowsPlugins = "Windows plugins: the daemon did not answer.";
            CanSyncWindows = false;
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
        WindowsPlugins = WindowsLine(yabridge, wine, folders);
        CanSyncWindows = yabridge is not null && wine;
        WineFolders = [.. (setup["wineFolders"] as System.Text.Json.Nodes.JsonArray ?? [])
            .Select(f => f?.GetValue<string>()).OfType<string>()];
        CanBridgeWine = WineFolders.Count > 0;
        BridgeWineLabel = WineFolders.Count > 1
            ? $"Bridge Wine's {WineFolders.Count} plugin folders"
            : "Bridge Wine's plugins";
    }

    /// <summary>One line on Windows plugins, from what the daemon found.</summary>
    internal static string WindowsLine(string? yabridge, bool wine, int folders)
    {
        if (yabridge is null && !wine) return "Windows plugins: yabridge and Wine are not installed. Install both from your distribution, then install a Windows VST3 or CLAP plugin here.";
        if (yabridge is null) return "Windows plugins: Wine is installed, yabridge is not. Install yabridge from your distribution, then install a Windows VST3 or CLAP plugin here.";
        if (!wine) return $"Windows plugins: yabridge {yabridge} is installed, Wine is not. Install Wine from your distribution.";
        string bridged = folders switch { 0 => "no folder bridged yet", 1 => "1 folder bridged", _ => $"{folders} folders bridged" };
        return $"Windows plugins: yabridge {yabridge} and Wine are installed, {bridged}. Run a plugin's installer with Wine, then install the folder it created.";
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
            if (!Set(ref _startDaemonAtLogin, value)) return;
            StartupIntegration.SetDaemonAtLogin(value);
            Persist();
        }
    }

    private bool _openWindowAtLogin;
    public bool OpenWindowAtLogin
    {
        get => _openWindowAtLogin;
        set
        {
            if (!Set(ref _openWindowAtLogin, value)) return;
            StartupIntegration.SetWindowAtLogin(value);
            Persist();
        }
    }

    private bool _minimizeToTray;
    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set
        {
            if (!Set(ref _minimizeToTray, value)) return;
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

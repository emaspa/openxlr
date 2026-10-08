using OpenXLR.UI.Localization;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace OpenXLR.UI;

/// <summary>One entry in the enforced-default pickers; Name null = don't enforce.</summary>
public sealed record DeviceChoice(string? Name, string Label);

/// <summary>One skin in the appearance picker; the id is what ui.json keeps.</summary>
public sealed record SkinChoice(string Id, string Label);

/// <summary>One of Material's modes in the appearance picker; the id is what ui.json keeps.</summary>
public sealed record AppearanceModeChoice(string Id, string Label);
public sealed record MixerViewChoice(string Id, string Label);

/// <summary>Standard or Touch sizing for the mixer's controls; ui.json keeps the flag.</summary>
public sealed record ControlSizingChoice(bool Touch, string Label);

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

        UiSettings s = UiSettings.Load(out _preferenceError);
        _startDaemonAtLogin = s.StartDaemonAtLogin;
        _openWindowAtLogin = s.OpenWindowAtLogin;
        _minimizeToTray = s.MinimizeToTray;
        _startMinimized = s.StartMinimized;
        _checkForUpdates = s.CheckForUpdates;
        _startupError = RepairNote(StartupIntegration.LastRepair);
        // No saved choice means the daemon runs whatever its unit asked for,
        // which for every shipped unit is the submixer on.
        _submixer = DaemonPrefs.Load(out string? daemonProblem).Submixer ?? true;
        _preferenceError ??= daemonProblem;

        BuildChoices();
        BuildSkinChoices();
        _applying = true;
        try
        {
            SelectedSkin = SkinChoices.FirstOrDefault(c => c.Id == Skinning.SkinService.Current.Id) ?? SkinChoices[0];
            SelectedAppearanceMode = AppearanceModeChoices.First(c => c.Id == Skinning.SkinService.Mode);
            SelectedControlSizing = ControlSizingChoices.First(c => c.Touch == Skinning.SkinService.TouchControls);
            SelectedMixerView = MixerViewChoices.First(c => c.Id == main.MixerView);
            SelectedLanguage = LanguageChoices.FirstOrDefault(c => c.Id is not null && c.Id == s.Language) ?? LanguageChoices[0];
            EnforcedOutput = OutputChoices.FirstOrDefault(c => c.Name == main.EnforcedDefaultSink) ?? OutputChoices[0];
            EnforcedInput = InputChoices.FirstOrDefault(c => c.Name == main.EnforcedDefaultSource) ?? InputChoices[0];
        }
        finally { _applying = false; }
        ReportSkin(Skinning.SkinService.Errors);
    }

    // --- plugins ---

    private string _pluginDirectories = Localizer.Text("PluginDirectoriesDefault");
    /// <summary>Where installs go, once the daemon has said.</summary>
    public string PluginDirectories { get => _pluginDirectories; private set => Set(ref _pluginDirectories, value); }

    private string _windowsPlugins = Localizer.Text("WindowsPluginsChecking");
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

    private string _bridgeWineLabel = Localizer.Text("BridgeWinePlugins");
    /// <summary>What the button offers, named after what it will bridge.</summary>
    public string BridgeWineLabel { get => _bridgeWineLabel; private set => Set(ref _bridgeWineLabel, value); }

    private string? _windowsEditorNote;
    /// <summary>What to know before opening a bridged plugin's own editor, or null.</summary>
    public string? WindowsEditorNote { get => _windowsEditorNote; private set { if (Set(ref _windowsEditorNote, value)) Raise(nameof(HasWindowsEditorNote)); } }

    public bool HasWindowsEditorNote => !string.IsNullOrEmpty(_windowsEditorNote);

    private string? _memoryLockNote;
    public string? MemoryLockNote { get => _memoryLockNote; private set { if (Set(ref _memoryLockNote, value)) Raise(nameof(HasMemoryLockNote)); } }
    public bool HasMemoryLockNote => !string.IsNullOrEmpty(_memoryLockNote);

    private string _skippedPlugins = Localizer.Text("SkippedBundlesChecking");
    public string SkippedPlugins { get => _skippedPlugins; private set => Set(ref _skippedPlugins, value); }
    public ObservableCollection<string> SkippedPluginDetails { get; } = [];

    private bool _hasSkippedPlugins;
    /// <summary>Whether any bundle is being passed over, and the list is worth the room.</summary>
    public bool HasSkippedPlugins { get => _hasSkippedPlugins; private set => Set(ref _hasSkippedPlugins, value); }

    private bool _canBridgeWine;
    /// <summary>
    /// Whether Wine holds plugins nobody has bridged yet. The button spares
    /// the user a file dialog: Wine keeps them under a dot directory, which
    /// a file dialog hides until the user knows to ask for hidden folders.
    /// </summary>
    public bool CanBridgeWine { get => _canBridgeWine; private set => Set(ref _canBridgeWine, value); }

    private bool? _pluginWineTrace;
    /// <summary>Null until a daemon that supports the switch has answered.</summary>
    public bool? PluginWineTrace => _pluginWineTrace;
    private bool _settingPluginWineTrace;
    public bool CanSetPluginWineTrace => _pluginWineTrace is not null && !_settingPluginWineTrace;
    private string? _pluginWineTraceStatus = Localizer.Text("WineTraceChecking");
    public string? PluginWineTraceStatus { get => _pluginWineTraceStatus; private set => Set(ref _pluginWineTraceStatus, value); }

    public async System.Threading.Tasks.Task SetPluginWineTraceAsync(bool enabled)
    {
        if (!CanSetPluginWineTrace) return;
        _settingPluginWineTrace = true;
        Raise(nameof(CanSetPluginWineTrace));
        PluginWineTraceStatus = Localizer.Text("WineTraceUpdating");
        try
        {
            var setup = await _client.SetPluginWineTraceAsync(enabled, TimeSpan.FromSeconds(30));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ApplyPluginSetup(setup));
        }
        finally
        {
            _settingPluginWineTrace = false;
            Raise(nameof(CanSetPluginWineTrace));
        }
    }

    /// <summary>Ask the daemon where plugins go and what is there to bridge Windows ones.</summary>
    public async System.Threading.Tasks.Task LoadPluginSetupAsync()
    {
        System.Text.Json.Nodes.JsonNode? setup = await _client.RequestPluginSetupAsync(TimeSpan.FromSeconds(10));
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ApplyPluginSetup(setup));
    }

    /// <summary>Every directory the plugin formats search, from the last setup reply.</summary>
    public System.Collections.ObjectModel.ObservableCollection<PluginSearchDirectoryItem> SearchDirectories { get; } = [];
    private string? _searchPathWarning;
    public string? SearchPathWarning { get => _searchPathWarning; private set => Set(ref _searchPathWarning, value); }

    internal void ApplyPluginSetup(System.Text.Json.Nodes.JsonNode? setup)
    {
        // Only the reply sets the value. Raise even when it stayed the same,
        // so a refused change puts the checkbox back where the daemon has it.
        _pluginWineTrace = setup?["wineTrace"]?.GetValue<bool>();
        Raise(nameof(PluginWineTrace));
        Raise(nameof(CanSetPluginWineTrace));
        PluginWineTraceStatus = _pluginWineTrace is null
            ? Localizer.Text("WineTraceUnavailable") : null;
        SearchDirectories.Clear();
        SearchPathWarning = setup?["searchPathWarning"]?.GetValue<string>();
        foreach (var item in setup?["searchDirectories"] as System.Text.Json.Nodes.JsonArray ?? [])
            if (item?["kind"]?.GetValue<string>() is { } kind && item["path"]?.GetValue<string>() is { } path)
                SearchDirectories.Add(new(kind, path, item["custom"]?.GetValue<bool>() == true, item["exists"]?.GetValue<bool>() == true));
        SkippedPluginDetails.Clear();
        int? skipped = setup?["skippedFailedCount"]?.GetValue<int>();
        SkippedPlugins = skipped is null ? Localizer.Text("SkippedBundlesUnavailable") : Localizer.Format("SkippedBundlesAfterFailedScan", skipped);
        foreach (var bundle in setup?["skippedFailedBundles"] as System.Text.Json.Nodes.JsonArray ?? [])
        {
            string? when = bundle?["failedAt"]?.GetValue<string>();
            string time = DateTimeOffset.TryParse(when, out var failedAt)
                ? failedAt.ToLocalTime().ToString("g") : Localizer.Text("TimeNotRecorded");
            SkippedPluginDetails.Add($"{bundle?["path"]?.GetValue<string>()}\n{bundle?["reason"]?.GetValue<string>()}, {time}");
        }
        if (skipped > SkippedPluginDetails.Count)
            SkippedPluginDetails.Add(Localizer.Format("MoreBundlesOmitted", skipped - SkippedPluginDetails.Count));
        HasSkippedPlugins = SkippedPluginDetails.Count > 0;
        if (setup is null)
        {
            WindowsEditorNote = null;
            MemoryLockNote = null;
            WindowsPlugins = Localizer.Text("WindowsPluginsNoAnswer");
            CanSyncWindows = false;
            CanManageWindows = false;
            return;
        }
        string lv2 = setup["lv2Directory"]?.GetValue<string>() ?? "~/.lv2";
        string clap = setup["clapDirectory"]?.GetValue<string>() ?? "~/.clap";
        string vst3 = setup["vst3Directory"]?.GetValue<string>() ?? "~/.vst3";
        bool host = setup["hostInstalled"]?.GetValue<bool>() ?? true;
        PluginDirectories = Localizer.Format("PluginDirectoriesFound", lv2, clap, vst3)
            + (host ? "" : " " + Localizer.Text("NativeHostMissing"));
        WindowsEditorNote = setup["windowsEditorNote"]?.GetValue<string>();
        MemoryLockNote = setup["memoryLockNote"]?.GetValue<string>();
        string? yabridge = setup["yabridge"]?.GetValue<string>();
        bool wine = setup["wine"]?.GetValue<bool>() ?? false;
        int folders = (setup["windowsDirectories"] as System.Text.Json.Nodes.JsonArray)?.Count ?? 0;
        WindowsPlugins = WindowsLine(yabridge, wine, folders, setup["bridgeProvider"]?.GetValue<string>() == "openxlr");
        if (setup["windowsPluginDirectory"]?.GetValue<string>() is { } managedDirectory)
            PluginDirectories += " " + Localizer.Format("WindowsPluginWrappersIn", managedDirectory);
        WindowsImportNote = setup["windowsImportDirectory"]?.GetValue<string>() is { } imports
            ? Localizer.Format("SinglePluginImportsIn", imports) : null;
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
            ? Localizer.Format("BridgeWinePluginFolders", WineFolders.Count)
            : Localizer.Text("BridgeWinePlugins");
    }

    /// <summary>One line on Windows plugins, from what the daemon found.</summary>
    internal static string WindowsLine(string? yabridge, bool wine, int folders, bool managed = false)
    {
        if (yabridge is null && !wine) return Localizer.Text("WindowsPluginsNothingInstalled");
        if (yabridge is null) return Localizer.Text("WindowsPluginsNoYabridge");
        string bridge = managed ? Localizer.Format("OpenXlrBridgeVersion", yabridge) : $"yabridge {yabridge}";
        if (!wine) return Localizer.Format("WindowsPluginsNoWine", bridge);
        string bridged = folders switch { 0 => Localizer.Text("NoFolderBridged"), 1 => Localizer.Text("OneFolderBridged"), _ => Localizer.Format("FoldersBridged", folders) };
        return Localizer.Format("WindowsPluginsReady", bridge, bridged);
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
                    ? Localizer.Text("CouldNotEnableDaemonAtLogin")
                    : Localizer.Text("CouldNotDisableDaemonAtLogin");
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
                StartupError = Localizer.Text("CouldNotUpdateAutostart");
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
            Localizer.Text("AutostartRemovedOutside"),
        StartupIntegration.AutostartRepair.Failed =>
            Localizer.Text("CouldNotRepairAutostart"),
        _ => null,
    };

    public string StartupHint => (StartDaemonAtLogin, OpenWindowAtLogin) switch
    {
        (true, true) => Localizer.Text("StartupAudioAndApp"),
        (false, true) => Localizer.Text("StartupAppOnly"),
        (true, false) => Localizer.Text("StartupAudioOnly"),
        _ => Localizer.Text("StartupNothing"),
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
            // Start from the file so keys this version does not know survive.
            if (Report((DaemonPrefs.Load() with { Submixer = value }).Save()))
            {
                SubmixerNote = null;
                return;
            }
            SubmixerNote = StartupIntegration.RestartDaemon()
                ? (value ? Localizer.Text("DaemonRestartedSubmixerOn")
                         : Localizer.Text("DaemonRestartedHardwareOnly"))
                : Localizer.Text("SavedRestartDaemonToApply");
        }
    }

    private string? _submixerNote;
    public string? SubmixerNote
    {
        get => _submixerNote;
        private set => Set(ref _submixerNote, value);
    }

    // Start from the file so fields owned elsewhere (the main window's
    // collapsed tiles, keys a newer version wrote) survive a save from here.
    private void Persist() => Report((UiSettings.Load() with
    {
        StartDaemonAtLogin = _startDaemonAtLogin,
        OpenWindowAtLogin = _openWindowAtLogin,
        MinimizeToTray = _minimizeToTray,
        StartMinimized = _startMinimized,
        CheckForUpdates = _checkForUpdates,
    }).Save());

    // --- preference files ---

    private string? _preferenceError;
    /// <summary>
    /// Why the last preference change could not be saved, or why ui.json or
    /// daemon.json could not be read when this window opened; null when all
    /// is well. A change that fails to save is not undone. One line for every
    /// setting, replaced by the next save and cleared by a good one.
    /// </summary>
    public string? PreferenceError { get => _preferenceError; private set => Set(ref _preferenceError, value); }

    /// <summary>Show the outcome of a save; true when it failed.</summary>
    private bool Report(string? saveError)
    {
        PreferenceError = saveError;
        return saveError is not null;
    }

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

    // --- appearance ---

    public ObservableCollection<SkinChoice> SkinChoices { get; } = [];

    private SkinChoice? _selectedSkin;
    /// <summary>
    /// The appearance. Choosing one saves it in ui.json and puts it on
    /// immediately; no window is rebuilt and nothing about the mixer, the
    /// daemon or the audio graph is touched.
    /// </summary>
    public SkinChoice? SelectedSkin
    {
        get => _selectedSkin;
        set
        {
            if (!Set(ref _selectedSkin, value) || _applying || value is null) return;
            ReportSkin(Skinning.SkinService.Choose(value.Id, out string? saveError));
            Report(saveError);
        }
    }

    public System.Collections.Generic.IReadOnlyList<AppearanceModeChoice> AppearanceModeChoices { get; } =
    [
        new(AppearanceModes.System, Localizer.Text("AppearanceSystem")),
        new(AppearanceModes.Light, Localizer.Text("AppearanceLight")),
        new(AppearanceModes.Dark, Localizer.Text("AppearanceDark")),
    ];

    private AppearanceModeChoice? _selectedAppearanceMode;
    /// <summary>
    /// Material's mode. Choosing one saves it in ui.json and puts it on at
    /// once, like the skin; a save that fails leaves it on for this run and
    /// says why on the preference line.
    /// </summary>
    public AppearanceModeChoice? SelectedAppearanceMode
    {
        get => _selectedAppearanceMode;
        set
        {
            if (!Set(ref _selectedAppearanceMode, value) || _applying || value is null) return;
            ReportSkin(Skinning.SkinService.ChooseMode(value.Id, out string? saveError));
            Report(saveError);
        }
    }

    public System.Collections.Generic.IReadOnlyList<ControlSizingChoice> ControlSizingChoices { get; } =
    [
        new(false, Localizer.Text("SizingStandard")),
        new(true, Localizer.Text("SizingTouch")),
    ];

    private ControlSizingChoice? _selectedControlSizing;
    /// <summary>
    /// Standard or Touch sizing for the main mixer. Choosing one saves it in
    /// ui.json and puts it on at once, whatever skin is worn; a save that
    /// fails leaves it on for this run and says why on the preference line.
    /// </summary>
    public ControlSizingChoice? SelectedControlSizing
    {
        get => _selectedControlSizing;
        set
        {
            if (!Set(ref _selectedControlSizing, value) || _applying || value is null) return;
            Skinning.SkinService.ChooseControlSizing(value.Touch, out string? saveError);
            Report(saveError);
        }
    }

    public System.Collections.Generic.IReadOnlyList<MixerViewChoice> MixerViewChoices { get; } =
    [
        new(MainViewModel.FullView, Localizer.Text("MixerViewFull")),
        new(MainViewModel.CompactView, Localizer.Text("MixerViewCompact")),
        new(MainViewModel.MiniView, Localizer.Text("MixerViewMini")),
    ];

    private MixerViewChoice? _selectedMixerView;
    /// <summary>
    /// The submixer's view. It changes the main window at once and is saved
    /// in ui.json; a save that fails leaves it on for this run and says why
    /// on the preference line.
    /// </summary>
    public MixerViewChoice? SelectedMixerView
    {
        get => _selectedMixerView;
        set
        {
            if (!Set(ref _selectedMixerView, value) || _applying || value is null) return;
            Report(_main.ChooseMixerView(value.Id));
        }
    }

    /// <summary>Follow the desktop, then every language the window ships, each named in itself.</summary>
    public System.Collections.Generic.IReadOnlyList<LanguageChoice> LanguageChoices { get; } =
    [
        new(null, Localizer.Text("SystemLanguage")),
        .. Localizer.Languages,
    ];

    private LanguageChoice? _selectedLanguage;
    /// <summary>
    /// The window's language. Choosing one saves it in ui.json; the window
    /// reads its text once, so the language changes the next time it starts.
    /// A save that fails says why on the preference line.
    /// </summary>
    public LanguageChoice? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (!Set(ref _selectedLanguage, value) || _applying || value is null) return;
            Report((UiSettings.Load() with { Language = value.Id }).Save());
        }
    }

    /// <summary>The mode only means something while Material is worn and the launch did not force it.</summary>
    public bool CanChooseAppearanceMode => Skinning.SkinService.CanChooseMode;

    /// <summary>What the mode does with the skin that is on.</summary>
    public string AppearanceModeNote => Skinning.SkinService.Overridden
        ? Localizer.Text("AppearanceModeOverridden")
        : CanChooseAppearanceMode ? Localizer.Text("AppearanceModeSystemNote")
        : Localizer.Text("AppearanceModeOtherSkin");

    private string _skinNote = "";
    /// <summary>What the chosen skin says about itself, and where it came from.</summary>
    public string SkinNote { get => _skinNote; private set => Set(ref _skinNote, value); }

    private string? _skinError;
    /// <summary>Everything wrong with the chosen skin, or null when it applied cleanly.</summary>
    public string? SkinError
    {
        get => _skinError;
        private set { if (Set(ref _skinError, value)) Raise(nameof(HasSkinError)); }
    }

    public bool HasSkinError => !string.IsNullOrEmpty(_skinError);

    /// <summary>Where a downloaded skin folder goes, spelled out for the user.</summary>
    public string SkinFolderHint =>
        Localizer.Format("SkinFolderHint", Skinning.SkinCatalog.UserSkinDir);

    /// <summary>Read the skin folders again, keeping the current choice if it is still there.</summary>
    public void ReloadSkins()
    {
        string id = Skinning.SkinService.Current.Id;
        BuildSkinChoices();
        _applying = true;
        try { SelectedSkin = SkinChoices.FirstOrDefault(c => c.Id == id) ?? SkinChoices[0]; }
        finally { _applying = false; }
        ReportSkin(Skinning.SkinService.Reload());
    }

    private void BuildSkinChoices()
    {
        SkinChoices.Clear();
        foreach (Skinning.SkinEntry entry in Skinning.SkinCatalog.Discover())
            SkinChoices.Add(new SkinChoice(entry.Id, Localizer.Format("SkinChoiceLabel", entry.Name, entry.Package.OriginLabel)));
    }

    private void ReportSkin(System.Collections.Generic.IReadOnlyList<string> errors)
    {
        Skinning.SkinPackage package = Skinning.SkinService.Current.Package;
        string note = package.Description ?? "";
        if (package.Author is { Length: > 0 } author) note = note.Length == 0 ? Localizer.Format("SkinByAuthor", author) : Localizer.Format("SkinNoteByAuthor", note, author);
        if (Skinning.SkinService.Overridden)
            note = (note.Length == 0 ? "" : note + " ")
                + Localizer.Format("SkinChosenByLaunch", Skinning.SkinService.OverrideVariable);
        SkinNote = note;
        Raise(nameof(CanChooseAppearanceMode));
        Raise(nameof(AppearanceModeNote));
        SkinError = errors.Count == 0 ? null : string.Join("\n", errors);
    }

    private void BuildChoices()
    {
        OutputChoices.Add(new DeviceChoice(null, Localizer.Text("DontEnforce")));
        OutputChoices.Add(new DeviceChoice("@monitor", Localizer.Text("FollowMonitorOutput")));
        // "#phones" entries are channel-pair routing targets, not real sinks a
        // system default can point to.
        foreach (AudioDeviceItem d in _main.Outputs.Where(d => !d.Name.Contains("#phones", StringComparison.Ordinal)))
            OutputChoices.Add(new DeviceChoice(d.Name, d.Label));

        InputChoices.Add(new DeviceChoice(null, Localizer.Text("DontEnforce")));
        foreach (AudioDeviceItem d in _main.Inputs)
            InputChoices.Add(new DeviceChoice(d.Name, d.Label));
    }
}

/// <summary>One directory a plugin format searches, as the plugin manager lists it.</summary>
public sealed record PluginSearchDirectoryItem(string Kind, string Path, bool Custom, bool Exists)
{
    public string Detail => (Custom, Exists) switch
    {
        (true, true) => Localizer.Format("SearchDirectoryAdded", Kind.ToUpperInvariant()),
        (true, false) => Localizer.Format("SearchDirectoryAddedMissing", Kind.ToUpperInvariant()),
        (false, true) => Localizer.Format("SearchDirectoryStandard", Kind.ToUpperInvariant()),
        (false, false) => Localizer.Format("SearchDirectoryStandardMissing", Kind.ToUpperInvariant()),
    };
}

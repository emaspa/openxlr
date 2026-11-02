using OpenXLR.UI.Localization;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

/// <summary>
/// The confirm window between Options' "This unit" and the switch. It asks
/// the daemon for the dry run, lists what this layout loses, and switches
/// only from its own button. Cancel, or closing it, changes nothing.
/// </summary>
public partial class HardwareMixingWindow : Window
{
    public HardwareMixingWindow() => InitializeComponent();

    internal HardwareMixingWindow(HardwareMixingConfirmViewModel model) : this() => DataContext = model;

    /// <summary>Open the window over <paramref name="owner"/>, run the dry run in it and wait until it closes.</summary>
    public static async Task ConfirmAsync(Window owner, HardwareMixingViewModel mixing)
    {
        var model = new HardwareMixingConfirmViewModel(mixing);
        var window = new HardwareMixingWindow(model);
        Task shown = window.ShowDialog(owner);
        await model.LoadAsync();
        await shown;
    }

    private async void OnSwitch(object? sender, RoutedEventArgs e)
    {
        if (DataContext is HardwareMixingConfirmViewModel model && await model.SwitchAsync()) Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}

/// <summary>What the confirm window shows: the dry run's lines, its note, and the switch's own state.</summary>
public sealed class HardwareMixingConfirmViewModel(HardwareMixingViewModel mixing) : ViewModelBase
{
    private HardwareMixingPlan? _plan;
    private string? _status = Localizer.Text("HardwareMixingChecking");
    private string? _refusal;
    private bool _busy = true;

    /// <summary>Checking, or the dry run did not come back; null once it did.</summary>
    public string? Status { get => _status; private set => Set(ref _status, value); }
    public string? Refusal { get => _refusal; private set => Set(ref _refusal, value); }
    public bool HasPlan => _plan is not null;

    /// <summary>What this layout loses, then how far each jack goes down; one line each.</summary>
    public IReadOnlyList<string> Lines => _plan is null ? []
        : _plan.Losses.Count == 0 && _plan.JackDrops.Count == 0 ? [Localizer.Text("HardwareMixingConfirmNoLosses")]
        : [.. _plan.Lines()];

    /// <summary>Why the mode would not run now: the budget refuses the switch, anything else waits for the unit.</summary>
    public string? Note => _plan is { Active: false } plan
        ? plan.CanSwitch ? Localizer.Format("HardwareMixingConfirmWaits", plan.Note ?? Localizer.Text("HardwareMixingWaiting"))
        : plan.Note ?? Localizer.Text("HardwareMixingWaiting")
        : null;

    /// <summary>The layout has more channels than USB pairs: nothing can be switched.</summary>
    public bool Refused => _plan is { CanSwitch: false };

    public bool CanSwitch => !_busy && _plan is { CanSwitch: true };

    /// <summary>Set the plan as if the dry run returned it; the window tests use this.</summary>
    internal void Show(HardwareMixingPlan? plan)
    {
        _plan = plan;
        _busy = false;
        Status = plan is null ? Localizer.Text("HardwareMixingCheckFailed") : null;
        Raise(nameof(HasPlan)); Raise(nameof(Lines)); Raise(nameof(Note)); Raise(nameof(Refused)); Raise(nameof(CanSwitch));
    }

    internal async Task LoadAsync() => Show(await mixing.CheckAsync());

    /// <summary>Send the switch; true when the daemon took it and the window can close.</summary>
    internal async Task<bool> SwitchAsync()
    {
        if (!CanSwitch) return false;
        _busy = true;
        Raise(nameof(CanSwitch));
        string? error = await mixing.SwitchAsync(hardware: true);
        _busy = false;
        Refusal = error;
        Raise(nameof(CanSwitch));
        return error is null;
    }
}

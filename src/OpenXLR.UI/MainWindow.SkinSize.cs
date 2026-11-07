using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI;

/// <summary>
/// The size a skin gives the UI. A skin that names one in its <c>window</c>
/// section is put on at that size: when the UI starts with it and whenever the
/// user changes to it. Moving from such a skin to one that names none brings
/// back the UI's usual size; between two skins that name none, the size the
/// user gave the UI stays. The user's own resizing is never undone while the
/// skin stays on, and a maximised or full screen UI is left as it is.
/// </summary>
public partial class MainWindow
{
    private string? _sizedSkin;
    private SkinWindowSize? _sizedBy;

    /// <summary>Stands in for the screen's working area, in device independent pixels, for the tests.</summary>
    internal static Func<Window, Rect?>? WorkingAreaProbe { get; set; }

    private void SetupSkinSize()
    {
        ApplySkinSize(starting: true);
        SkinService.Changed += OnSkinSize;
        Closed += (_, _) => SkinService.Changed -= OnSkinSize;
    }

    private void OnSkinSize() => ApplySkinSize(starting: false);

    private void ApplySkinSize(bool starting)
    {
        SkinPackage skin = SkinService.Current.Package;
        SkinWindowSize? wanted = skin.WindowSize;
        if (!starting && skin.Id == _sizedSkin && wanted == _sizedBy) return;
        SkinWindowSize? before = _sizedBy;
        _sizedSkin = skin.Id;
        _sizedBy = wanted;
        SkinWindowSize? size = wanted ?? (before is null ? null : SkinWindowSize.Default);
        if (size is not { } target || WindowState != WindowState.Normal) return;

        Rect? area = WorkingArea();
        Size fitted = target.FitIn(area, Decorations());
        Width = fitted.Width;
        Height = fitted.Height;
        if (area is { } screen && IsVisible) KeepOn(screen, fitted);
    }

    /// <summary>The working area of the screen the UI is on, or the primary one before it is shown.</summary>
    private Rect? WorkingArea()
    {
        if (WorkingAreaProbe is { } probe) return probe(this);
        Screen? screen = (IsVisible ? Screens.ScreenFromWindow(this) : null) ?? Screens.Primary;
        if (screen is null) return null;
        PixelRect pixels = screen.WorkingArea;
        double scale = screen.Scaling > 0 ? screen.Scaling : 1;
        return new Rect(pixels.X / scale, pixels.Y / scale, pixels.Width / scale, pixels.Height / scale);
    }

    /// <summary>The desktop's frame round the content, once the UI has been shown and it is known.</summary>
    private Size Decorations() =>
        FrameSize is { } frame && frame.Width >= ClientSize.Width && frame.Height >= ClientSize.Height
            ? new Size(frame.Width - ClientSize.Width, frame.Height - ClientSize.Height)
            : default;

    /// <summary>Moves a shown UI back onto its screen when the new size took it past an edge.</summary>
    private void KeepOn(Rect area, Size size)
    {
        double scale = DesktopScaling > 0 ? DesktopScaling : 1;
        Size outer = size + Decorations();
        double left = Position.X / scale, top = Position.Y / scale;
        double x = Math.Max(area.X, Math.Min(left, area.Right - outer.Width));
        double y = Math.Max(area.Y, Math.Min(top, area.Bottom - outer.Height));
        if (x != left || y != top) Position = new PixelPoint((int)Math.Round(x * scale), (int)Math.Round(y * scale));
    }
}

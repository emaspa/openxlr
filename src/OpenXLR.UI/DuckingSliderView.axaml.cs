using Avalonia.Controls;

namespace OpenXLR.UI;

/// <summary>One ducking slider with its name, value and preset marks; its DataContext is a <see cref="DuckingSliderViewModel"/>.</summary>
public partial class DuckingSliderView : UserControl
{
    public DuckingSliderView()
    {
        InitializeComponent();
        Slider slider = this.FindControl<Slider>("DuckingSlider")!;
        this.FindControl<DuckingMarks>("DuckingSliderMarks")!.Slider = slider;
        DuckingSliderInput.Attach(this, slider);
    }
}

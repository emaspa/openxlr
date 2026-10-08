using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// Called inside the window layout fixture once its platform is up: a
/// binding that formats with catalogue text gets the text.
/// </summary>
internal static class LocalizationRenderingTests
{
    internal static void Check()
    {
        var inserts = new InsertsViewModel(new DaemonClient(), "xlr1", 1, "XLR 1");
        var chain = new MixInsertsWindow { DataContext = inserts };
        try
        {
            chain.Show();
            Assert.Equal(Localizer.Format("InsertsTitle", "XLR 1"), chain.Title);
        }
        finally { chain.Close(); }
    }
}

using System.Xml.Linq;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class ButtonLabelTests
{
    [Fact]
    public void ButtonLabelsDoNotContainEllipses()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "OpenXLR.UI"))) root = root.Parent;
        Assert.NotNull(root);
        string ui = Path.Combine(root.FullName, "src", "OpenXLR.UI");
        foreach (string path in Directory.EnumerateFiles(ui, "*.axaml"))
        {
            var document = XDocument.Load(path);
            foreach (var button in document.Descendants().Where(e => e.Name.LocalName.EndsWith("Button", StringComparison.Ordinal)))
            {
                if (button.Attribute("Content")?.Value is string content)
                    AssertPlain(content, Path.GetFileName(path));
                foreach (var text in button.Descendants().Where(e => e.Name.LocalName == "TextBlock"))
                    if (text.Attribute("Text")?.Value is string label) AssertPlain(label, Path.GetFileName(path));
            }
        }
    }

    [Fact]
    public void DynamicInsertButtonLabelsHaveNoEllipsis()
    {
        var inserts = new InsertsViewModel(new DaemonClient(), "xlr1");
        Assert.Equal("Inserts", inserts.ButtonText);
        inserts.Items.Add(new InsertViewModel(inserts, "test", "test-plugin", "Test"));
        Assert.Equal("Inserts (1)", inserts.ButtonText);
    }

    private static void AssertPlain(string text, string file)
        => Assert.True(!text.Contains('…') && !text.Contains("...", StringComparison.Ordinal),
            $"{file}: button label '{text}' contains an ellipsis.");
}

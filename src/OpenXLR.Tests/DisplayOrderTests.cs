using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class DisplayOrderTests
{
    [Theory]
    [InlineData("a", "c", true, "b,c,a,d")]
    [InlineData("a", "c", false, "b,a,c,d")]
    [InlineData("d", "a", false, "d,a,b,c")]
    [InlineData("d", "a", true, "a,d,b,c")]
    [InlineData("a", "a", true, "a,b,c,d")]
    [InlineData("missing", "a", true, "a,b,c,d")]
    [InlineData("a", "removed", false, "a,b,c,d")]
    [InlineData("a", "b", false, "a,b,c,d")]
    [InlineData("b", "a", true, "a,b,c,d")]
    public void PlacementInsertsWithoutSwappingInterveningItems(string from, string target, bool after, string expected)
    {
        string[] initial = ["a", "b", "c", "d"];
        Assert.Equal(expected.Split(','), DisplayOrder.Place(initial, from, target, after));
        Assert.Equal(["a", "b", "c", "d"], initial);
    }

    [Fact]
    public void RestoredOrderKeepsMissingTilesAndDiscardsUnknownAndRepeatedIds()
    {
        Assert.Equal(["c", "a", "b"], DisplayOrder.Complete(["unknown", "c", "c", null!, "a"], ["a", "b", "c"]));
        Assert.Equal(["a", "b"], DisplayOrder.Complete(null, ["a", "b"]));
        Assert.Empty(DisplayOrder.Place([], "a", "b", true));
    }

}

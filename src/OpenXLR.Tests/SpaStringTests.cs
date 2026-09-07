using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class SpaStringTests
{
    [Theory]
    [InlineData("http://lsp-plug.in/plugins/lv2/compressor_mono", "http://lsp-plug.in/plugins/lv2/compressor_mono")]
    [InlineData("http://gareus.org/oss/lv2/darc#mono", "http://gareus.org/oss/lv2/darc\\u0023mono")]
    [InlineData("OpenXLR Mic #2 Inserts", "OpenXLR Mic \\u0023" + "2 Inserts")]
    [InlineData("say \"hi\" \\ back", "say \\\"hi\\\" \\\\ back")]
    public void HashesQuotesAndBackslashesSurviveThePropertyParser(string input, string expected)
        => Assert.Equal(expected, PipeWireAdapter.SpaString(input));
}

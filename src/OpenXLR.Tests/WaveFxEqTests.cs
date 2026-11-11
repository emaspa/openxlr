using System.Globalization;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The Wave FX EQ codec and its mapping to the microphone EQ, held to Wave
/// Link 3.5's own writes. Each fixture is one write of block 0x0004 from a
/// USB capture of Wave Link driving a unit on firmware 1.0.15 (09 the Wave
/// XLR MK.2's launch, 10 its EQ switch and a band 2 drag, 13 its switch, 14
/// the XLR Dock MK.2 after its update: switch, relaunch, a band 2 drag),
/// with the block read just before it and the values Wave Link logged for
/// it. Encoding the logged values onto the block read before reproduces the
/// write byte for byte; only byte 1 bit 3, which Wave Link sets at launch
/// and OpenXLR never writes, is left out of the two launch writes. Values
/// only: no Elgato code.
/// </summary>
public sealed class WaveFxEqTests
{
    private sealed record Capture(string Name, string Before, string Write, string Logged);

    private static readonly Capture[] Writes =
    [
        new("09", "251000d4200a00c8001800f4040a0064000601003c00f6030de3000005f45c03f22507fd27e8", "251800d4200a00c8001800f4040a0064000601003c00f6030de300ff05f45c03f22507fd27e8", "Band1_QFactor=-0.15199995;Band2_QFactor=-0.007999957"),
        new("10", "251800d4200a00c8001800f4040a0064000601003c00f6030de300ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f6030de300ff05f45c03f22507fd27e8", "Eq_Enabled=True"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f6030de300ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f6030fdb00ff05f45c03f22507fd27e8", "Band2_FrequencyHz=219;Band2_GainDb=3.75"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f6030fdb00ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f60312dd00ff05f45c03f22507fd27e8", "Band2_FrequencyHz=221;Band2_GainDb=4.5"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f60312dd00ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f60314dd00ff05f45c03f22507fd27e8", "Band2_GainDb=5"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f60314dd00ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f60318e100ff05f45c03f22507fd27e8", "Band2_FrequencyHz=225;Band2_GainDb=6"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f60318e100ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f6031be800ff05f45c03f22507fd27e8", "Band2_FrequencyHz=232;Band2_GainDb=6.75"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f6031be800ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f6031ee300ff05f45c03f22507fd27e8", "Band2_FrequencyHz=227;Band2_GainDb=7.5"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f6031ee300ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f60320dd00ff05f45c03f22507fd27e8", "Band2_FrequencyHz=221;Band2_GainDb=8"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f60320dd00ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f60321dd00ff05f45c03f22507fd27e8", "Band2_GainDb=8.25"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f60321dd00ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f60323dd00ff05f45c03f22507fd27e8", "Band2_GainDb=8.75"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f60323dd00ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f60324df00ff05f45c03f22507fd27e8", "Band2_FrequencyHz=223;Band2_GainDb=9"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f60324df00ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f60324dd00ff05f45c03f22507fd27e8", "Band2_FrequencyHz=221"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f60324dd00ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f60320d900ff05f45c03f22507fd27e8", "Band2_FrequencyHz=217;Band2_GainDb=8"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f60320d900ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f6031dd500ff05f45c03f22507fd27e8", "Band2_FrequencyHz=213;Band2_GainDb=7.25"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f6031dd500ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f6031ace00ff05f45c03f22507fd27e8", "Band2_FrequencyHz=206;Band2_GainDb=6.5"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f6031ace00ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f60316c400ff05f45c03f22507fd27e8", "Band2_FrequencyHz=196;Band2_GainDb=5.5"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f60316c400ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f60311c100ff05f45c03f22507fd27e8", "Band2_FrequencyHz=193;Band2_GainDb=4.25"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f60311c100ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f6030ec300ff05f45c03f22507fd27e8", "Band2_FrequencyHz=195;Band2_GainDb=3.5"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f6030ec300ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f6030bc400ff05f45c03f22507fd27e8", "Band2_FrequencyHz=196;Band2_GainDb=2.75"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f6030bc400ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f60309c400ff05f45c03f22507fd27e8", "Band2_GainDb=2.25"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f60309c400ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f60306c400ff05f45c03f22507fd27e8", "Band2_GainDb=1.5"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f60306c400ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f60304c600ff05f45c03f22507fd27e8", "Band2_FrequencyHz=198;Band2_GainDb=1"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f60304c600ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f60303c600ff05f45c03f22507fd27e8", "Band2_GainDb=0.75"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f60303c600ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f60302c600ff05f45c03f22507fd27e8", "Band2_GainDb=0.5"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f60302c600ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f60300c600ff05f45c03f22507fd27e8", "Band2_GainDb=0"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f60300c600ff05f45c03f22507fd27e8", "251800d4200a00c8001800f4040a0064000601003c00f60300c600ff05f45c03f22507fd27e8", "Eq_Enabled=False"),
        new("10", "251800d4200a00c8001800f4040a0064000601003c00f60300c600ff05f45c03f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f60300c600ff05f45c03f22507fd27e8", "Eq_Enabled=True"),
        new("10", "251801d4200a00c8001800f4040a0064000601003c00f60300c600ff05f45c03f22507fd27e8", "251800d4200a00c8001800f4040a0064000601003c00f60300c600ff05f45c03f22507fd27e8", "Eq_Enabled=False"),
        new("13", "251801d4200a00c8001800f4040a0064000601003c00f60300c600ff0530bf02f22507fd27e8", "251800d4200a00c8001800f4040a0064000601003c00f60300c600ff0530bf02f22507fd27e8", "Eq_Enabled=False"),
        new("13", "251800d4200a00c8001800f4040a0064000601003c00f60300c600ff0530bf02f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f60300c600ff0530bf02f22507fd27e8", "Band1_QFactor=-0.15199995;Band2_QFactor=-0.007999957;Band3_QFactor=-0.21599996;Band4_QFactor=-0.376;Eq_Enabled=True"),
        new("13", "251801d4200a00c8001800f4040a0064000601003c00f60300c600ff0530bf02f22507fd27e8", "251800d4200a00c8001800f4040a0064000601003c00f60300c600ff0530bf02f22507fd27e8", "Eq_Enabled=False"),
        new("13", "251800d4200a00c8001800f4040a0064000601003c00f60300c600ff0530bf02f22507fd27e8", "251801d4200a00c8001800f4040a0064000601003c00f60300c600ff0530bf02f22507fd27e8", "Eq_Enabled=True"),
        new("13", "251801d4200a00c8001800f4040a0064000601003c00f60300c600ff0530bf02f22507fd27e8", "251800d4200a00c8001800f4040a0064000601003c00f60300c600ff0530bf02f22507fd27e8", "Eq_Enabled=False"),
        new("14", "1e7200d4200a00c8001800f4040a0064000601003c00f6030de3000005f45c03f22507fd27e8", "1e7201d4200a00c8001800f4040a0064000601003c00f6030de3000005f45c03f22507fd27e8", "Eq_Enabled=True"),
        new("14", "1e7201d4200a00c8001800f4040a0064000601003c00f6030de3000005f45c03f22507fd27e8", "1e7200d4200a00c8001800f4040a0064000601003c00f6030de3000005f45c03f22507fd27e8", "Eq_Enabled=False"),
        new("14", "1e7200d4200a00c8001800f4040a0064000601003c00f6030de3000005f45c03f22507fd27e8", "1e7a00d4200a00c8001800f4040a0064000601003c00f6030de300ff05f45c03f22507fd27e8", "Band1_QFactor=-0.15199995;Band2_QFactor=-0.007999957"),
        new("14", "1e7a00d4200a00c8001800f4040a0064000601003c00f6030de300ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f6030de300ff05f45c03f22507fd27e8", "Band3_QFactor=-0.21599996;Band4_QFactor=-0.376;Eq_Enabled=True"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f6030de300ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f6030fe800ff05f45c03f22507fd27e8", "Band2_FrequencyHz=232;Band2_GainDb=3.75"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f6030fe800ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f60315b800ff05f45c03f22507fd27e8", "Band2_FrequencyHz=184;Band2_GainDb=5.25"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f60315b800ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f6031aa500ff05f45c03f22507fd27e8", "Band2_FrequencyHz=165;Band2_GainDb=6.5"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f6031aa500ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f6031da800ff05f45c03f22507fd27e8", "Band2_FrequencyHz=168;Band2_GainDb=7.25"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f6031da800ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f60322c300ff05f45c03f22507fd27e8", "Band2_FrequencyHz=195;Band2_GainDb=8.5"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f60322c300ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f60323c800ff05f45c03f22507fd27e8", "Band2_FrequencyHz=200;Band2_GainDb=8.75"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f60323c800ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f60324cc00ff05f45c03f22507fd27e8", "Band2_FrequencyHz=204;Band2_GainDb=9"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f60324cc00ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f60326bf00ff05f45c03f22507fd27e8", "Band2_FrequencyHz=191;Band2_GainDb=9.5"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f60326bf00ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f60327b600ff05f45c03f22507fd27e8", "Band2_FrequencyHz=182;Band2_GainDb=9.75"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f60327b600ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f60329b600ff05f45c03f22507fd27e8", "Band2_GainDb=10.25"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f60329b600ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f6032db200ff05f45c03f22507fd27e8", "Band2_FrequencyHz=178;Band2_GainDb=11.25"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f6032db200ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f6032fad00ff05f45c03f22507fd27e8", "Band2_FrequencyHz=173;Band2_GainDb=11.75"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f6032fad00ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f60330a800ff05f45c03f22507fd27e8", "Band2_FrequencyHz=168;Band2_GainDb=12"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f60330a800ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f60330aa00ff05f45c03f22507fd27e8", "Band2_FrequencyHz=170"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f60330aa00ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f6032ead00ff05f45c03f22507fd27e8", "Band2_FrequencyHz=173;Band2_GainDb=11.5"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f6032ead00ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f60329ae00ff05f45c03f22507fd27e8", "Band2_FrequencyHz=174;Band2_GainDb=10.25"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f60329ae00ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f60323b000ff05f45c03f22507fd27e8", "Band2_FrequencyHz=176;Band2_GainDb=8.75"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f60323b000ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f6031db200ff05f45c03f22507fd27e8", "Band2_FrequencyHz=178;Band2_GainDb=7.25"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f6031db200ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f60313b000ff05f45c03f22507fd27e8", "Band2_FrequencyHz=176;Band2_GainDb=4.75"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f60313b000ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f6030bae00ff05f45c03f22507fd27e8", "Band2_FrequencyHz=174;Band2_GainDb=2.75"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f6030bae00ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f60307ae00ff05f45c03f22507fd27e8", "Band2_GainDb=1.75"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f60307ae00ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f60304ae00ff05f45c03f22507fd27e8", "Band2_GainDb=1"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f60304ae00ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f60302ae00ff05f45c03f22507fd27e8", "Band2_GainDb=0.5"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f60302ae00ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f60301ae00ff05f45c03f22507fd27e8", "Band2_GainDb=0.25"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f60301ae00ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f603ffad00ff05f45c03f22507fd27e8", "Band2_FrequencyHz=173;Band2_GainDb=-0.25"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f603ffad00ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f603fead00ff05f45c03f22507fd27e8", "Band2_GainDb=-0.5"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f603fead00ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f603ffad00ff05f45c03f22507fd27e8", "Band2_GainDb=-0.25"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f603ffad00ff05f45c03f22507fd27e8", "1e7a01d4200a00c8001800f4040a0064000601003c00f60300ab00ff05f45c03f22507fd27e8", "Band2_FrequencyHz=171;Band2_GainDb=0"),
        new("14", "1e7a01d4200a00c8001800f4040a0064000601003c00f60300ab00ff05f45c03f22507fd27e8", "1e7a00d4200a00c8001800f4040a0064000601003c00f60300ab00ff05f45c03f22507fd27e8", "Eq_Enabled=False"),
    ];

    public static TheoryData<int> WriteIndexes() => [.. Enumerable.Range(0, Writes.Length)];

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    /// <summary>One logged Wave Link value applied to the unit's raw EQ, through the microphone EQ's own snapping and mapping.</summary>
    private static DeviceEq Apply(DeviceEq eq, string name, string value)
    {
        if (name == "Eq_Enabled") return eq with { Enabled = value == "True" };
        int index = name[4] - '1';
        double number = double.Parse(value, CultureInfo.InvariantCulture);
        MicEq mapped = DeviceEqMapping.FromDevice(eq)!;
        EqBand band = mapped.Bands[index];
        band = name[6..] switch
        {
            "GainDb" => band with { Gain = MicEq.SnapGain(number) },
            "FrequencyHz" => band with { Frequency = MicEq.SnapFrequency(number) },
            "QFactor" => band with { Q = MicEq.SnapQ(Math.Pow(10, number)) },
            _ => throw new ArgumentException(name),
        };
        var bands = eq.Bands!.ToArray();
        bands[index] = DeviceEqMapping.ToDevice(band);
        return eq with { Bands = bands };
    }

    [Theory]
    [MemberData(nameof(WriteIndexes))]
    public void EncodingWaveLinksLoggedValuesReproducesItsWrite(int index)
    {
        Capture capture = Writes[index];
        byte[] before = Hex(capture.Before), written = Hex(capture.Write);
        DeviceEq eq = WaveFxEq.Decode(before)!;
        Assert.True(WaveFxEq.Decodes(eq), $"capture {capture.Name}: the block read before does not decode");
        foreach (string pair in capture.Logged.Split(';'))
        {
            string[] parts = pair.Split('=');
            eq = Apply(eq, parts[0], parts[1]);
        }
        byte[] encoded = (byte[])before.Clone();
        WaveFxEq.Encode(encoded, eq);
        if ((before[1] ^ written[1]) == 0x08)
        {
            // Wave Link's launch write: byte 1 bit 3, which is not the EQ's.
            Assert.Equal(before[1], encoded[1]);
            encoded[1] = written[1];
        }
        Assert.Equal(capture.Write, Convert.ToHexString(encoded).ToLowerInvariant());
        // What was written reads back as the values Wave Link logged.
        Assert.Equal(eq, WaveFxEq.Decode(written));
    }

    [Fact]
    public void TheFixturesCoverEveryKindOfWriteTheCapturesHold()
    {
        Assert.Equal(1, Writes.Count(w => w.Name == "09"));
        Assert.Equal(28, Writes.Count(w => w.Name == "10"));
        Assert.Equal(5, Writes.Count(w => w.Name == "13"));
        Assert.Equal(33, Writes.Count(w => w.Name == "14"));
        Assert.Contains(Writes, w => w.Logged.Contains("Band2_GainDb=-0.25"));
        Assert.Contains(Writes, w => w.Logged.Contains("Band2_GainDb=12"));
        Assert.Equal(11, Writes.Count(w => w.Logged.Split(';').All(p => p.StartsWith("Eq_Enabled", StringComparison.Ordinal))));
    }

    [Fact]
    public void RecordedBlocksDecodeToTheValuesWaveLinkLogged()
    {
        // Capture 09, the Wave XLR MK.2 before Wave Link wrote it.
        DeviceEq mk2 = WaveFxEq.Decode(Hex("251000d4200a00c8001800f4040a0064000601003c00f6030de3000005f45c03f22507fd27e8"))!;
        Assert.False(mk2.Enabled);
        Assert.Equal(new DeviceEqBand(true, 0, 0, 60, -10), mk2.Bands![0]);
        Assert.Equal(new DeviceEqBand(true, 1, 13, 227, 0), mk2.Bands[1]);
        Assert.Equal(new DeviceEqBand(true, 2, -12, 860, -14), mk2.Bands[2]);
        // Band 4's flags 0x25: on, bell, and bit 5, which is not the EQ's.
        Assert.Equal(new DeviceEqBand(true, 2, 7, 10237, -24), mk2.Bands[3]);
        // Capture 11: band 3 at +12 dB and 703 Hz, the switch on.
        DeviceEq boosted = WaveFxEq.Decode(Hex("251801d4200a00c8001800f4040a0064000601003c00f60300c600ff0530bf02f22507fd27e8"))!;
        Assert.True(boosted.Enabled);
        Assert.Equal(new DeviceEqBand(true, 2, 48, 703, -14), boosted.Bands![2]);
        MicEq mapped = DeviceEqMapping.FromDevice(boosted)!;
        Assert.Equal(12, mapped.Bands[2].Gain);
        Assert.Equal(703, mapped.Bands[2].Frequency);
        Assert.Equal(MicEq.Peak, mapped.Bands[2].Type);
        // The dock on firmware 1.0.0: flags 0x32 are band off, low shelf, bits 4 and 5 set.
        DeviceEq dock = WaveFxEq.Decode(Hex("50f800d4200a00c8001814ff02220090010632006400f63400f401f63400cc07f62800e913f6"))!;
        Assert.Equal(new DeviceEqBand(false, 1, 0, 100, -10), dock.Bands![0]);
        Assert.Equal(new DeviceEqBand(false, 2, 0, 500, -10), dock.Bands[1]);
        Assert.Equal(new DeviceEqBand(false, 2, 0, 1996, -10), dock.Bands[2]);
        Assert.Equal(new DeviceEqBand(false, 4, 0, 5097, -10), dock.Bands[3]);
        // Capture 14: the dock on 1.0.15 holds the MK.2's factory bands.
        DeviceEq updated = WaveFxEq.Decode(Hex("1e7200d4200a00c8001800f4040a0064000601003c00f6030de3000005f45c03f22507fd27e8"))!;
        Assert.Equal(mk2, updated);
        Assert.True(WaveFxEq.Decodes(updated));
    }

    [Fact]
    public void EveryWriteKeepsEveryByteAndBitTheEqDoesNotOwn()
    {
        var random = new Random(4);
        byte[] owned = new byte[WaveFxEq.StructLength];
        owned[2] = 0x01;
        for (int band = 0; band < 4; band++)
        {
            int at = 18 + 5 * band;
            owned[at] = 0x0F;
            owned[at + 1] = owned[at + 2] = owned[at + 3] = owned[at + 4] = 0xFF;
        }
        for (int run = 0; run < 500; run++)
        {
            byte[] block = new byte[WaveFxEq.StructLength];
            random.NextBytes(block);
            byte[] before = (byte[])block.Clone();
            DeviceEq eq = run % 3 == 0 ? (run % 2 == 0 ? DeviceEq.Off : new DeviceEq(true, null))
                : new DeviceEq(random.Next(2) == 1, [.. Enumerable.Range(0, 4).Select(_ => new DeviceEqBand(random.Next(2) == 1,
                    random.Next(6), random.Next(-80, 81), random.Next(20, 20001), random.Next(-64, 65)))]);
            WaveFxEq.Encode(block, eq);
            for (int i = 0; i < block.Length; i++)
                Assert.Equal(before[i] & ~owned[i], block[i] & ~owned[i]);
            DeviceEq read = WaveFxEq.Decode(block)!;
            Assert.Equal(eq.Enabled, read.Enabled);
            if (eq.Bands is null) Assert.Equal(WaveFxEq.Decode(before)!.Bands, read.Bands);
            else Assert.Equal(eq, read);
        }
    }

    [Fact]
    public void SwitchingOffClearsByteTwoBitZeroAndNothingElse()
    {
        byte[] block = Hex("251801d4200a00c8001800f4040a0064000601003c00f60300c600ff0530bf02f22507fd27e8");
        byte[] before = (byte[])block.Clone();
        WaveFxEq.Encode(block, DeviceEq.Off);
        Assert.Equal(0x00, block[2]);
        block[2] = before[2];
        Assert.Equal(before, block);
        // ClipGuard's bit on the same byte stays.
        block[2] = 0x05;
        WaveFxEq.Encode(block, DeviceEq.Off);
        Assert.Equal(0x04, block[2]);
    }

    [Fact]
    public void AValueOutsideTheUnitsRangesIsRefusedAndABlockHoldingOneDoesNotDecode()
    {
        byte[] block = new byte[WaveFxEq.StructLength];
        DeviceEqBand ok = new(true, 2, 0, 1000, -10);
        Assert.Throws<ArgumentException>(() => WaveFxEq.Encode(block, new DeviceEq(true, [ok, ok, ok, ok with { Type = 6 }])));
        Assert.Throws<ArgumentException>(() => WaveFxEq.Encode(block, new DeviceEq(true, [ok, ok, ok, ok with { Gain = 81 }])));
        Assert.Throws<ArgumentException>(() => WaveFxEq.Encode(block, new DeviceEq(true, [ok, ok, ok, ok with { Gain = -81 }])));
        Assert.Throws<ArgumentException>(() => WaveFxEq.Encode(block, new DeviceEq(true, [ok, ok, ok, ok with { Frequency = 19 }])));
        Assert.Throws<ArgumentException>(() => WaveFxEq.Encode(block, new DeviceEq(true, [ok, ok, ok, ok with { Q = 65 }])));
        Assert.Throws<ArgumentException>(() => WaveFxEq.Encode(block, new DeviceEq(true, [ok, ok, ok])));
        Assert.Throws<ArgumentException>(() => WaveFxEq.Encode(new byte[37], DeviceEq.Off));
        Assert.Null(WaveFxEq.Decode(new byte[37]));
        // An all-zero block holds frequency 0: it does not decode.
        Assert.False(WaveFxEq.Decodes(WaveFxEq.Decode(block)));
        Assert.All(block, b => Assert.Equal(0, b));
    }

    [Fact]
    public void BandGainsDecodeFromMinusToPlusTwentyDecibelsOnEveryModel()
    {
        // XLR Dock MK.2 capture 14's block, band 3 at +12 dB (byte 19 + 10 = 29).
        byte[] block = Hex("251801d4200a00c8001800f4040a0064000601003c00f60300c600ff0530bf02f22507fd27e8");
        const int band3Gain = WaveFxEq.FirstBand + 2 * WaveFxEq.BandStride + 1;
        Assert.Equal(0x30, block[band3Gain]);
        // +15 dB, as another program may store it, and the ends of the range
        // the Wave XLR MK.2 was measured applying: 0x50 and 0xb0.
        foreach ((byte raw, double db) in new[] { ((byte)0x3c, 15.0), ((byte)0x50, 20.0), ((byte)0xb0, -20.0), ((byte)0xc4, -15.0) })
        {
            block[band3Gain] = raw;
            DeviceEq eq = WaveFxEq.Decode(block)!;
            Assert.True(WaveFxEq.Decodes(eq), $"byte {raw:x2}");
            Assert.Equal(db, DeviceEqMapping.FromDevice(eq)!.Bands[2].Gain);
            Assert.Equal(Math.Abs(db) <= 12, WaveFxEq.FitsGain(eq, WaveFxEq.DefaultMaxGainDb));
            Assert.True(WaveFxEq.FitsGain(eq, WaveFxEq.MeasuredMaxGainDb));
            byte[] written = new byte[WaveFxEq.StructLength];
            WaveFxEq.Encode(written, eq);
            Assert.Equal(raw, written[band3Gain]);
        }
        // Beyond 20 dB a block still does not decode.
        foreach (byte raw in new byte[] { 0x51, 0xaf, 0x7f, 0x80 })
        {
            block[band3Gain] = raw;
            Assert.False(WaveFxEq.Decodes(WaveFxEq.Decode(block)), $"byte {raw:x2}");
        }
        Assert.True(WaveFxEq.FitsGain(DeviceEq.Off, 0));
        // The microphone EQ maps its ends to those bytes.
        Assert.Equal(80, DeviceEqMapping.ToDevice(new EqBand { Gain = MicEq.MaxGain }).Gain);
        Assert.Equal(-80, DeviceEqMapping.ToDevice(new EqBand { Gain = MicEq.MinGain }).Gain);
    }

    [Fact]
    public void EveryQGridPointHasItsOwnByteAndComesBack()
    {
        var bytes = new HashSet<int>();
        for (int k = 0; k <= 125; k++)
        {
            int b = DeviceEqMapping.QByte(k);
            Assert.InRange(b, -64, 64);
            Assert.True(bytes.Add(b), $"step {k} shares byte {b}");
            Assert.Equal(k, DeviceEqMapping.QStep(b));
            // Through the microphone EQ's own Q: the value on the grid maps to the same byte.
            double q = Math.Pow(10, -1 + MicEq.QLog10Step * k);
            Assert.Equal(b, DeviceEqMapping.ToDevice(new EqBand { Q = q }).Q);
        }
        Assert.Equal(126, bytes.Count);
        Assert.Equal([-43, 0, 43], Enumerable.Range(-64, 129).Where(b => !bytes.Contains(b)));
        // 0 (Q 1.0, which the factory block holds) is the one exact tie and goes to step 63.
        Assert.Equal(63, DeviceEqMapping.QStep(0));
        // Wave Link's logged QFactors.
        foreach ((double log, int expected) in new[] { (-0.152, -10), (-0.008, -1), (-0.216, -14), (-0.376, -24) })
            Assert.Equal(expected, DeviceEqMapping.ToDevice(new EqBand { Q = MicEq.SnapQ(Math.Pow(10, log)) }).Q);
        Assert.Equal(-10, DeviceEqMapping.ToDevice(new EqBand { Q = MicEq.DefaultQ }).Q);
    }

    [Fact]
    public void TheMicrophoneEqMapsToTheUnitAndBackExactly()
    {
        MicEq eq = MicEq.Snap(new MicEq
        {
            Enabled = true,
            Bands =
            [
                new() { Enabled = true, Type = MicEq.LowCut, Frequency = 120, Q = 4 },
                new() { Enabled = true, Type = MicEq.LowShelf, Frequency = 100, Gain = 6, Q = 0.5 },
                new() { Enabled = true, Type = MicEq.Peak, Frequency = 1000, Gain = 9.25, Q = 2 },
                new() { Enabled = false, Type = MicEq.HighShelf, Frequency = 5000, Gain = -6, Q = 3 },
            ],
        });
        DeviceEq raw = DeviceEqMapping.ToDevice(eq);
        Assert.Equal(37, raw.Bands![2].Gain);
        Assert.Equal(-24, raw.Bands[3].Gain);
        Assert.Equal(eq, DeviceEqMapping.FromDevice(raw));
        // A gainless type keeps its gain on the unit too.
        MicEq notch = eq.WithBand(3, eq.Bands[2] with { Type = MicEq.Notch });
        Assert.Equal(37, DeviceEqMapping.ToDevice(notch).Bands![2].Gain);
        Assert.Equal(3, DeviceEqMapping.ToDevice(notch).Bands![2].Type);
        Assert.Null(DeviceEqMapping.FromDevice(new DeviceEq(true, null)));
    }

    [Fact]
    public void TheFirmwareVersionIsBlockZerosBytesFiveToSeven()
    {
        Assert.Equal(new Version(1, 0, 15), WaveFxEq.FirmwareVersion(Hex("0205040000" + "01000f")));
        Assert.Equal(new Version(1, 0, 0), WaveFxEq.FirmwareVersion(Hex("010d000000" + "010000")));
        Assert.Null(WaveFxEq.FirmwareVersion(Hex("0205040000" + "0100")));
        Assert.True(new Version(1, 0, 16) >= WaveFxEq.Mk2EqFirmware);
        Assert.True(new Version(1, 0, 0) < WaveFxEq.Mk2EqFirmware);
    }
}

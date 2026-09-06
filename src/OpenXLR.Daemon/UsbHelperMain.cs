using OpenXLR.Core;

namespace OpenXLR.Daemon;

/// <summary>
/// The daemon binary started with --usb-helper: the process that actually
/// holds libusb. It answers <see cref="UsbHelperProtocol"/> requests from
/// its parent on stdin and stdout until the parent closes the pipe or kills
/// it. Nothing else runs here, so killing it costs the daemon nothing but a
/// reconnect.
/// </summary>
internal static class UsbHelperMain
{
    public static int Run()
    {
        using Stream stdin = Console.OpenStandardInput();
        using Stream stdout = Console.OpenStandardOutput();
        using var backend = new InProcessUsbTransport();
        try
        {
            UsbHelperProtocol.Serve(stdin, stdout, backend);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"usb helper: {ex.Message}");
            return 1;
        }
    }
}

namespace OpenXLR.Daemon;

/// <summary>
/// The system default sink/source as they stood before this process touched
/// anything, captured at the very top of <c>Main</c>. <see cref="MixerService"/>
/// used to take this snapshot itself, inside its own <c>StartAsync</c>, but
/// <see cref="DeviceManager"/> is a separate hosted service racing it on the
/// same startup, and connecting the device switches the card to the pro-audio
/// profile, which makes WirePlumber re-create its ALSA nodes and, some time
/// after that, silently reassign the system defaults to them. If that
/// reassignment lands before MixerService takes its snapshot, the "before"
/// value it captures is already the device's own node, and every later
/// defend pass (<see cref="Core.Mixing.Mixer.EnforceDefaults"/> and the
/// delayed re-checks in <see cref="MixerService.StartAsync"/>) spends the
/// rest of the session re-asserting the wrong default: worked once a user
/// fixed it by hand mid-session, silently reverted on the next boot, exactly
/// when the race is most likely to go the other way. Capturing it here, before
/// any hosted service exists, removes the race outright.
/// </summary>
public sealed record StartupDefaults(string? Sink, string? Source)
{
    public static StartupDefaults Capture()
    {
        string? sink = null, source = null;
        try { sink = Run("pactl", "get-default-sink"); } catch (Exception) { /* best effort */ }
        try { source = Run("pactl", "get-default-source"); } catch (Exception) { /* best effort */ }
        return new StartupDefaults(sink, source);
    }

    private static string Run(string exe, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { RedirectStandardOutput = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(3000);
        return output.Trim();
    }
}

using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;

namespace OpenXLR.UI;

internal static class SettingsBackupCommands
{
    internal const string Usage = """
        openxlr backup export FILE.openxlr-backup [--profiles-only]
        openxlr backup preview FILE.openxlr-backup
        openxlr backup import FILE.openxlr-backup [--replace]
        """;

    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args is [] or ["--help"]) { output.WriteLine(Usage); return 0; }
        try
        {
            switch (args)
            {
                case ["export", _] or ["export", _, "--profiles-only"]:
                    output.WriteLine($"Exported {SettingsBackup.Export(args[1], args.Length == 3)} saved files.");
                    return 0;
                case ["preview", var path]:
                    Describe(SettingsBackup.Preview(path), output);
                    return 0;
                case ["import", _] or ["import", _, "--replace"]:
                    using (var window = ReserveWindow())
                    using (var daemon = DaemonLock.TryAcquire() ?? throw new IOException(
                        "Stop the OpenXLR daemon before importing a backup."))
                    {
                        var changes = SettingsBackup.Preview(args[1]);
                        if (args.Length == 2 && changes.Any(change => change.Conflict))
                            Describe(changes, output);
                        string? safety = SettingsBackup.Apply(changes, replace: args.Length == 3);
                        output.WriteLine($"Imported {changes.Count} files. OpenXLR will read them on its next start.");
                        if (safety is not null) output.WriteLine("Previous files saved to " + safety);
                    }
                    return 0;
                default:
                    error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (Exception ex) when (SettingsBackup.IsFileFailure(ex) || ex is SocketException or TimeoutException)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static ControlSocketListener ReserveWindow()
    {
        try
        {
            var listener = ControlSocketListener.Bind(SingleInstance.DefaultPath);
            try { listener.Socket.Listen(4); return listener; }
            catch { listener.Dispose(); throw; }
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException)
        {
            throw new IOException("Quit the OpenXLR window, including its tray instance, before importing. " + ex.Message, ex);
        }
    }

    private static void Describe(System.Collections.Generic.IReadOnlyList<SettingsBackup.Change> changes, TextWriter output)
    {
        if (changes.Count == 0) output.WriteLine("All files in this backup are already present.");
        foreach (var change in changes)
            output.WriteLine((change.Conflict ? "Replace " : "Add     ") + change.Name);
    }
}

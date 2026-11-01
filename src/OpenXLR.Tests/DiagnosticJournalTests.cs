using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>
/// The journals and the kernel log in the diagnostics archive: the whole
/// current boot within a size limit, newest lines kept, and a kernel log
/// that says where it came from or why it is missing.
/// </summary>
public sealed class DiagnosticJournalTests
{
    private static string Masked(string text) => text.Replace("kestrel7", "<redacted>", StringComparison.Ordinal);

    [Fact]
    public void AShortJournalIsKeptWhole()
    {
        string text = "first\nsecond\n";
        Assert.Equal(text, Diagnostics.KeepNewest(text, out string? cut));
        Assert.Null(cut);
        Assert.Equal("", Diagnostics.KeepNewest("", out cut));
        Assert.Null(cut);
    }

    [Fact]
    public void ALongJournalKeepsItsNewestLinesAndSaysItWasCut()
    {
        string text = string.Concat(Enumerable.Range(0, Diagnostics.JournalLineLimit + 1).Select(i => $"line {i}\n"));
        string kept = Diagnostics.KeepNewest(text, out string? cut);
        string[] lines = kept.TrimEnd('\n').Split('\n');
        Assert.Equal(Diagnostics.JournalLineLimit, lines.Length);
        Assert.Equal("line 1", lines[0]);
        Assert.Equal($"line {Diagnostics.JournalLineLimit}", lines[^1]);
        Assert.NotNull(cut);
        Assert.StartsWith("[cut: older lines were left out", cut);
    }

    [Fact]
    public void AJournalOfLongLinesStaysWithinItsByteLimitInWholeLines()
    {
        string line = new('x', 999);  // a thousand bytes with its newline
        int count = Diagnostics.JournalByteLimit / 1000 + 50;
        string text = string.Concat(Enumerable.Range(0, count).Select(i => $"{i:D6}{line[6..]}\n"));
        string kept = Diagnostics.KeepNewest(text, out string? cut);
        Assert.NotNull(cut);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(kept), Diagnostics.JournalByteLimit - 1000, Diagnostics.JournalByteLimit);
        string[] lines = kept.TrimEnd('\n').Split('\n');
        Assert.All(lines, l => Assert.Equal(999, l.Length));
        Assert.Equal($"{count - 1:D6}", lines[^1][..6]);
    }

    [Fact]
    public async Task TheJournalCoversTheCurrentBootAndKeepsTheNewestLines()
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Directory.CreateTempSubdirectory("openxlr-diagnostic-journal-").FullName;
        try
        {
            // The fake prints its arguments, then one line more than the
            // archive keeps, so the file has to be cut at its oldest end.
            string journalctl = ExecutableScript.Write(Path.Combine(root, "journalctl"), $$"""
                echo "args: $*"
                i=1
                while [ $i -le {{Diagnostics.JournalLineLimit}} ]; do echo "2026-11-01T03:12:00+00:00 host pipewire[1]: line $i for kestrel7@host"; i=$((i+1)); done
                echo 'journal note' >&2
                """);
            await Diagnostics.WriteJournalAsync(Masked, root, "audio-journal.txt", journalctl,
                ["--user", "-u", "pipewire.service"]);
            string text = File.ReadAllText(Path.Combine(root, "audio-journal.txt"));
            string[] lines = text.Split('\n');
            Assert.StartsWith("[cut: older lines were left out", lines[0]);
            // The arguments were the oldest line, so they went first; the
            // request itself is checked on a short journal below.
            Assert.DoesNotContain("args:", text);
            Assert.Contains($"line {Diagnostics.JournalLineLimit} for <redacted>@host", text);
            Assert.Contains("line 1 for <redacted>@host", text);
            Assert.DoesNotContain("kestrel7", text);
            Assert.EndsWith("journal note", text.TrimEnd());

            ExecutableScript.Write(journalctl, "echo \"args: $*\"\n");
            await Diagnostics.WriteJournalAsync(Masked, root, "journal.txt", journalctl,
                ["--user", "-u", "openxlr-daemon.service"]);
            text = File.ReadAllText(Path.Combine(root, "journal.txt"));
            Assert.Equal($"args: --user -u openxlr-daemon.service -b -o short-iso-precise --no-pager -n {Diagnostics.JournalLineLimit + 1}\n", text);
            Assert.DoesNotContain("--since", text);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TheKernelLogIsTheCurrentBootFilteredForUsbAndSound()
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Directory.CreateTempSubdirectory("openxlr-diagnostic-kernel-").FullName;
        try
        {
            string journalctl = ExecutableScript.Write(Path.Combine(root, "journalctl"), """
                [ "$1 $2" = "-k -b" ] || { echo "unexpected request: $*" >&2; exit 91; }
                for a in "$@"; do last=$a; done
                printf 'pattern: %s\n' "$last"
                echo '2026-11-01T03:12:00.000001+00:00 host kernel: usb 3-2: USB disconnect, device number 4 on kestrel7'
                """);
            string dmesg = ExecutableScript.Write(Path.Combine(root, "dmesg"), "echo 'dmesg must not run' >&2\nexit 9\n");
            await Diagnostics.WriteKernelLogAsync(Masked, root, journalctl, dmesg);
            string text = File.ReadAllText(Path.Combine(root, "kernel-log.txt"));
            Assert.StartsWith("Kernel messages of the current boot about USB, sound and power management (journalctl -k).", text);
            Assert.Contains($"pattern: {Diagnostics.KernelLogPattern}", text);
            Assert.Contains("usb 3-2: USB disconnect, device number 4 on <redacted>", text);
            Assert.DoesNotContain("dmesg", text);
            Assert.DoesNotContain("unexpected request", text);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ARefusedSystemJournalFallsBackToTheKernelBufferAndSaysSo()
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Directory.CreateTempSubdirectory("openxlr-diagnostic-kernel-").FullName;
        try
        {
            // What journalctl prints for -k to an account outside the
            // journal's groups: no entries, and the hint on stderr.
            string journalctl = ExecutableScript.Write(Path.Combine(root, "journalctl"), """
                echo '-- No entries --'
                printf '%s\n' 'Hint: You are currently not seeing messages from other users and the system.' \
                  "      Users in groups 'adm', 'systemd-journal', 'wheel' can see all messages." >&2
                """);
            string dmesg = ExecutableScript.Write(Path.Combine(root, "dmesg"), """
                [ "$*" = "--time-format=iso" ] || exit 91
                printf '%s\n' '2026-11-01T03:12:00,000001+00:00 usb 3-2: reset high-speed USB device number 4 using xhci_hcd' \
                  '2026-11-01T03:12:01,000001+00:00 EXT4-fs (nvme0n1p2): mounted filesystem' \
                  '2026-11-01T03:12:02,000001+00:00 PM: suspend entry (s2idle)' \
                  '2026-11-01T03:12:03,000001+00:00 snd-usb-audio 3-2:1.0: cannot set freq 48000 to ep 0x1'
                """);
            await Diagnostics.WriteKernelLogAsync(Masked, root, journalctl, dmesg);
            string text = File.ReadAllText(Path.Combine(root, "kernel-log.txt"));
            Assert.Contains("this account may not read it", text);
            Assert.Contains("systemd-journal, adm or wheel", text);
            Assert.Contains("not seeing messages from other users", text);
            Assert.Contains("Read from the kernel's own buffer instead (dmesg)", text);
            Assert.Contains("reset high-speed USB device number 4", text);
            Assert.Contains("PM: suspend entry (s2idle)", text);
            Assert.Contains("cannot set freq 48000", text);
            Assert.DoesNotContain("EXT4-fs", text);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AKernelLogNeitherSourceWillShowLeavesANoteAndNoError()
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Directory.CreateTempSubdirectory("openxlr-diagnostic-kernel-").FullName;
        try
        {
            string journalctl = ExecutableScript.Write(Path.Combine(root, "journalctl"), """
                echo 'No journal files were opened due to insufficient permissions.' >&2
                exit 1
                """);
            string dmesg = ExecutableScript.Write(Path.Combine(root, "dmesg"), """
                echo 'dmesg: read kernel buffer failed: Operation not permitted' >&2
                exit 1
                """);
            await Diagnostics.WriteKernelLogAsync(Masked, root, journalctl, dmesg);
            string text = File.ReadAllText(Path.Combine(root, "kernel-log.txt"));
            Assert.Contains("this account may not read it", text);
            Assert.Contains("insufficient permissions", text);
            Assert.Contains("could not be read either", text);
            Assert.Contains("Operation not permitted", text);
            Assert.Contains("dmesg exited with status 1", text);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AJournalctlWithoutPatternMatchingIsFilteredHere()
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Directory.CreateTempSubdirectory("openxlr-diagnostic-kernel-").FullName;
        try
        {
            string journalctl = ExecutableScript.Write(Path.Combine(root, "journalctl"), """
                case "$*" in
                  *' -g '*) echo 'Compiled without pattern matching support' >&2; exit 1 ;;
                esac
                printf '%s\n' '2026-11-01T03:12:00.000001+00:00 host kernel: xhci_hcd 0000:7a:00.0: bad transfer trb length' \
                  '2026-11-01T03:12:01.000001+00:00 host kernel: x86/split lock detection: bus_lock trap'
                """);
            string dmesg = ExecutableScript.Write(Path.Combine(root, "dmesg"), "exit 9\n");
            await Diagnostics.WriteKernelLogAsync(Masked, root, journalctl, dmesg);
            string text = File.ReadAllText(Path.Combine(root, "kernel-log.txt"));
            Assert.Contains("cannot filter by pattern", text);
            Assert.Contains("bad transfer trb length", text);
            Assert.DoesNotContain("split lock", text);
        }
        finally { Directory.Delete(root, true); }
    }
}

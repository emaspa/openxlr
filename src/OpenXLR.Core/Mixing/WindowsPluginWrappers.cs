namespace OpenXLR.Core.Mixing;

/// <summary>Only yabridge wrappers whose Windows targets belong to a removed source folder.</summary>
internal static class WindowsPluginWrappers
{
    internal sealed record Wrapper(string Path, string? WindowsLink, IReadOnlyList<string> Targets)
    {
        public void Remove()
        {
            if (WindowsLink is null) Directory.Delete(Path, recursive: true);
            else
            {
                File.Delete(Path);
                File.Delete(WindowsLink);
            }
        }
    }

    internal static string Normalize(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));

    internal static bool Under(string path, string directory)
        => path == directory || path.StartsWith(directory == "/" ? "/" : directory + "/", StringComparison.Ordinal);

    internal static IReadOnlyList<Wrapper> Find(string vst3, string clap, string removed, IReadOnlyCollection<string> retained)
        => Select(Read(vst3, clap), targets => targets.All(t => Under(t, removed))
            && !targets.Any(t => retained.Any(d => Under(t, d))));

    internal static IReadOnlyList<Wrapper> ForPlugin(IReadOnlyList<Wrapper> wrappers, string plugin)
    {
        string canonical = Canonical(plugin);
        return Select(wrappers, targets =>
        {
            int matching = targets.Count(t => Under(Canonical(t), canonical));
            if (matching > 0 && matching != targets.Count)
                throw new IOException("A bridge wrapper is shared by multiple source plugins; its files were kept.");
            return matching == targets.Count;
        });
    }

    internal static IReadOnlyList<Wrapper> Missing(string vst3, string clap, IReadOnlyCollection<string> folders)
        => Select(Read(vst3, clap), targets => targets.All(t => !File.Exists(t) && !Directory.Exists(t)
            && folders.Any(d => Under(t, d) || Under(Canonical(t), Canonical(d)))));

    internal static bool InUse(Wrapper wrapper, IReadOnlyCollection<string>? paths)
        => paths?.Any(p => Under(Normalize(p), wrapper.Path)) == true;

    private static IReadOnlyList<Wrapper> Select(IReadOnlyList<Wrapper> wrappers, Func<IReadOnlyList<string>, bool> matches)
    {
        var selected = new List<Wrapper>();
        foreach (Wrapper wrapper in wrappers)
        {
            if (wrapper.Targets.Count == 0 || !matches(wrapper.Targets)) continue;
            if (wrapper.WindowsLink is null) CheckBundle(wrapper.Path);
            else if (!IsElf(wrapper.Path)) throw new IOException($"The CLAP file {wrapper.Path} is not a Linux bridge wrapper.");
            selected.Add(wrapper);
        }
        return selected;
    }

    internal static IReadOnlyList<Wrapper> Read(string vst3, string clap)
    {
        var wrappers = new List<Wrapper>();
        foreach (string bundle in Entries(vst3, vst3: true))
        {
            string contents = System.IO.Path.Combine(bundle, "Contents");
            EnsureNotLink(contents);
            if (!Directory.Exists(contents)) continue;
            var targets = new List<string>();
            foreach (string architecture in Directory.EnumerateDirectories(contents))
            {
                if (!System.IO.Path.GetFileName(architecture).EndsWith("-win", StringComparison.Ordinal)) continue;
                EnsureNotLink(architecture);
                foreach (string module in Directory.EnumerateFileSystemEntries(architecture))
                {
                    if (!module.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase)) continue;
                    targets.Add(LinkTarget(module) ?? throw new IOException($"The Windows module in {bundle} is not a yabridge link."));
                }
            }
            if (targets.Count > 0) wrappers.Add(new(bundle, null, targets));
        }
        foreach (string module in Entries(clap, vst3: false))
        {
            string link = System.IO.Path.ChangeExtension(module, ".clap-win");
            string? target = LinkTarget(link);
            if (target is not null) wrappers.Add(new(module, link, [target]));
        }
        return wrappers;
    }

    private static IEnumerable<string> Entries(string root, bool vst3)
    {
        root = Normalize(root);
        // A moved ~/.vst3 or an aliased wrapper root must not turn cleanup
        // into a traversal of an unrelated tree.
        for (string? path = root; path is not null; path = System.IO.Path.GetDirectoryName(path)) EnsureNotLink(path);
        if (!Directory.Exists(root)) yield break;
        foreach (string entry in Walk(root))
            yield return entry;

        IEnumerable<string> Walk(string directory)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (!vst3 && entry.EndsWith(".clap-win", StringComparison.OrdinalIgnoreCase)) continue;
                EnsureNotLink(entry);
                bool isDirectory = Directory.Exists(entry);
                if (vst3 && isDirectory && entry.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase)) yield return entry;
                else if (isDirectory)
                    foreach (string nested in Walk(entry)) yield return nested;
                else if (!vst3 && entry.EndsWith(".clap", StringComparison.OrdinalIgnoreCase)) yield return entry;
            }
        }
    }

    private static void CheckBundle(string bundle)
    {
        string contents = System.IO.Path.Combine(bundle, "Contents");
        if (Directory.EnumerateFileSystemEntries(bundle).Any(p => p != contents)) throw Unexpected();
        string name = System.IO.Path.GetFileNameWithoutExtension(bundle);
        foreach (string entry in Directory.EnumerateFileSystemEntries(contents))
        {
            string part = System.IO.Path.GetFileName(entry);
            if (part == "Resources" && new FileInfo(entry).LinkTarget is not null) continue;
            if (part is not ("x86_64-win" or "i386-win" or "x86_64-linux" or "i386-linux")) throw Unexpected();
            EnsureNotLink(entry);
            if (!Directory.Exists(entry)) throw Unexpected();
            bool windows = part.EndsWith("-win", StringComparison.Ordinal);
            foreach (string module in Directory.EnumerateFileSystemEntries(entry))
            {
                if (System.IO.Path.GetFileName(module) != name + (windows ? ".vst3" : ".so")) throw Unexpected();
                if (windows ? new FileInfo(module).LinkTarget is null : !IsElf(module)) throw Unexpected();
            }
        }
        IOException Unexpected() => new($"The bundle {bundle} contains files outside the expected yabridge layout.");
    }

    private static bool IsElf(string path)
    {
        EnsureNotLink(path);
        using FileStream file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[4];
        return file.Read(header) == 4 && header.SequenceEqual(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' });
    }

    private static string? LinkTarget(string path)
    {
        string? target = new FileInfo(path).LinkTarget;
        return target is null ? null : Normalize(System.IO.Path.GetFullPath(target, System.IO.Path.GetDirectoryName(path)!));
    }

    // yabridgectl stores canonical blacklist paths. Resolve ancestor links
    // too, and retain a missing tail so an uninstalled path can be matched.
    internal static string Canonical(string path)
    {
        int links = 0;
        return Resolve(System.IO.Path.IsPathFullyQualified(path) ? path : System.IO.Path.GetFullPath(path));

        string Resolve(string full)
        {
            string root = System.IO.Path.GetPathRoot(full)!;
            string current = root;
            foreach (string part in full[root.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".") continue;
                if (part == "..") { current = System.IO.Path.GetDirectoryName(current) ?? root; continue; }
                current = System.IO.Path.Combine(current, part);
                string? target = new FileInfo(current).LinkTarget;
                if (target is null) continue;
                if (++links > 40) throw new IOException($"Too many symbolic links in {path}.");
                current = Resolve(System.IO.Path.IsPathFullyQualified(target) ? target
                    : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(current)!, target));
            }
            return Normalize(current);
        }
    }

    internal static bool HasLink(string path)
    {
        for (string? current = Normalize(path); current is not null; current = System.IO.Path.GetDirectoryName(current))
            if (new FileInfo(current).LinkTarget is not null) return true;
        return false;
    }

    private static void EnsureNotLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
            throw new IOException($"Cannot safely clean wrappers through the symbolic link {path}.");
    }
}

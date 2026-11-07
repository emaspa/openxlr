using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace OpenXLR.UI.Skinning;

/// <summary>
/// Where a skin's own files come from: the folder an installed skin lives in,
/// or the resources compiled in beside a built-in skin's document. Both answer
/// the same two questions, so the reader holds a file name to one set of rules
/// and the image realizer reads a file one way, whichever kind of skin it is.
///
/// A file name is checked twice. <see cref="Check"/> runs when the document is
/// read and refuses a name that could ever leave the skin; <see cref="Open"/>
/// runs when the skin is put on, since a folder can change under us, and
/// answers null for a file that is missing or that a link takes elsewhere.
/// </summary>
public abstract class SkinAssets
{
    /// <summary>
    /// The name, checked: relative, with forward slashes, no root, no empty,
    /// <c>.</c> or <c>..</c> segment, no colon, no control character and no URL.
    /// A skin is data a user downloaded, so this is the boundary that keeps it
    /// from naming <c>../../.ssh/id_ed25519</c>.
    /// </summary>
    internal string Check(string source)
    {
        if (source.Length == 0 || source.Length > 256)
            throw new SkinValueException("a file name is between 1 and 256 characters.");
        if (source.Contains("://", StringComparison.Ordinal))
            throw new SkinValueException("a skin loads files from its own folder only, never from a URL.");
        if (source.Contains('\\'))
            throw new SkinValueException("use forward slashes in a file name.");
        if (Path.IsPathRooted(source))
            throw new SkinValueException("a file name is relative to the skin's folder.");
        string[] segments = source.Split('/');
        if (segments.Any(s => s.Length == 0 || s == "." || s == ".."))
            throw new SkinValueException("a file name has no empty, \".\" or \"..\" segments.");
        if (segments.Any(s => s.Any(c => char.IsControl(c) || c == ':')))
            throw new SkinValueException("a file name has no control characters or colons.");
        CheckContained(source);
        return source;
    }

    /// <summary>A further check of a name that passed the common rules, for a source that has one.</summary>
    protected virtual void CheckContained(string relative) { }

    /// <summary>
    /// A seekable stream over the file, positioned at its start, or null when
    /// there is no such file in the skin. The caller disposes it.
    /// </summary>
    internal abstract Stream? Open(string relative);
}

/// <summary>An installed skin's files: its own folder, and nothing a link out of it reaches.</summary>
public sealed class SkinFolderAssets(string directory) : SkinAssets
{
    public string Directory { get; } = directory;

    protected override void CheckContained(string relative)
    {
        string full = Path.GetFullPath(Path.Combine(Directory, relative));
        string root = Path.GetFullPath(Directory) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal))
            throw new SkinValueException("that file is outside the skin's folder.");
    }

    internal override Stream? Open(string relative)
    {
        if (SkinReader.ResolveAsset(Directory, relative) is not { } file) return null;
        try { return file.OpenRead(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// A built-in skin's files, compiled into an assembly beside its document.
/// The project embeds every PNG and JPEG under <c>Assets/Skins/&lt;id&gt;/</c>
/// with its path below the skin's folder as part of its name, so a name can
/// reach this skin's files only: a resource name is not a path, and the
/// common rules have already refused every segment that could climb out.
/// </summary>
public sealed class SkinEmbeddedAssets(Assembly assembly, string prefix) : SkinAssets
{
    /// <summary>What every embedded skin file's resource name starts with, before the skin's id.</summary>
    public const string ResourcePrefix = "OpenXLR.UI.Assets.SkinFiles/";

    /// <summary>The files of the built-in skin with this id in this assembly.</summary>
    public static SkinEmbeddedAssets For(Assembly assembly, string id) => new(assembly, ResourcePrefix + id + "/");

    internal override Stream? Open(string relative)
    {
        Stream? stream = assembly.GetManifestResourceStream(prefix + relative);
        if (stream is { CanSeek: true }) return stream;
        stream?.Dispose();
        return null;
    }
}

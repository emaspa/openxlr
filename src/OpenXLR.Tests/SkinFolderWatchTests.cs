using OpenXLR.Core;

namespace OpenXLR.Tests;

/// <summary>
/// The watch that reads the chosen skin again when its files change: what
/// counts as a change to it, and that a burst of writes is one read.
/// </summary>
public sealed class SkinFolderWatchTests : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    // Long enough for a stray event to have settled and fired.
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(800);

    private readonly string _user = Directory.CreateTempSubdirectory("openxlr-skin-watch-user-").FullName;
    private readonly string _system = Directory.CreateTempSubdirectory("openxlr-skin-watch-system-").FullName;
    private readonly SemaphoreSlim _fired = new(0);
    private int _count;

    public void Dispose()
    {
        Directory.Delete(_user, recursive: true);
        Directory.Delete(_system, recursive: true);
        _fired.Dispose();
    }

    private SkinFolderWatch Watch(string? id) =>
        new([_user, _system, Path.Combine(_user, "missing")], () => { Interlocked.Increment(ref _count); _fired.Release(); }, Settle) { Id = id };

    private static string Folder(string root, string id) => Directory.CreateDirectory(Path.Combine(root, id)).FullName;

    private async Task Fires()
    {
        Assert.True(await _fired.WaitAsync(Wait), "the watch did not report the change");
        // Nothing else follows from the same change.
        Assert.False(await _fired.WaitAsync(Quiet));
    }

    private async Task Silent() => Assert.False(await _fired.WaitAsync(Quiet), "the watch reported a change it should not");

    [Fact]
    public async Task AnEditToTheFollowedSkinIsOneReadAfterTheWritesSettle()
    {
        string folder = Folder(_user, "mine");
        using SkinFolderWatch watch = Watch("mine");
        Assert.Equal(2, watch.Watching);
        string file = Path.Combine(folder, "skin.json");
        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(file, "{\"schema\":1,\"tokens\":{}}" + new string(' ', i));
            await Task.Delay(20);
        }
        await Fires();
        Assert.Equal(1, Volatile.Read(ref _count));
        // An image beside the document counts too.
        File.WriteAllBytes(Path.Combine(folder, "back.png"), [1, 2, 3]);
        await Fires();
    }

    [Fact]
    public async Task AFileRenamedOverTheDocumentIsSeen()
    {
        string folder = Folder(_user, "mine");
        File.WriteAllText(Path.Combine(folder, "skin.json"), "{}");
        string staged = Path.Combine(_user, "staged.tmp");
        using SkinFolderWatch watch = Watch("mine");
        File.WriteAllText(staged, "{\"schema\":1}");
        // Written outside the folder: not the skin yet.
        await Silent();
        File.Move(staged, Path.Combine(folder, "skin.json"), overwrite: true);
        await Fires();
    }

    [Fact]
    public async Task TheFolderReplacedOrPutBackIsSeenInEveryDirectory()
    {
        using SkinFolderWatch watch = Watch("mine");
        // A folder that did not exist when the watch began.
        string folder = Folder(_system, "mine");
        File.WriteAllText(Path.Combine(folder, "skin.json"), "{}");
        await Fires();
        Directory.Delete(folder, recursive: true);
        await Fires();
        // Built elsewhere and moved in whole, as some tools do.
        string built = Folder(_user, "mine.new");
        File.WriteAllText(Path.Combine(built, "skin.json"), "{}");
        await Silent();
        Directory.Move(built, Path.Combine(_user, "mine"));
        await Fires();
    }

    [Fact]
    public async Task OtherSkinsMaterialAndNoChoiceAreNotFollowed()
    {
        string other = Folder(_user, "other");
        string prefixed = Folder(_user, "mine2");
        using SkinFolderWatch watch = Watch("mine");
        File.WriteAllText(Path.Combine(other, "skin.json"), "{}");
        File.WriteAllText(Path.Combine(prefixed, "skin.json"), "{}");
        await Silent();

        watch.Id = "default";
        Assert.Null(watch.Id);
        File.WriteAllText(Path.Combine(Folder(_user, "default"), "skin.json"), "{}");
        await Silent();

        // A read already pending is dropped when the choice goes.
        watch.Id = "mine";
        File.WriteAllText(Path.Combine(Folder(_user, "mine"), "skin.json"), "{}");
        watch.Id = null;
        await Silent();
    }

    [Fact]
    public async Task NothingIsReportedAfterTheWatchIsDisposed()
    {
        string folder = Folder(_user, "mine");
        SkinFolderWatch watch = Watch("mine");
        File.WriteAllText(Path.Combine(folder, "skin.json"), "{}");
        watch.Dispose();
        await Silent();
        File.WriteAllText(Path.Combine(folder, "skin.json"), "{ }");
        await Silent();
    }
}

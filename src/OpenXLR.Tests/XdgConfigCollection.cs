namespace OpenXLR.Tests;

// PATH and XDG changes affect every test in the process, not just this collection.
// Keep these tests away from parallel helpers that inherit that environment.
[CollectionDefinition("xdg-config", DisableParallelization = true)]
public sealed class XdgConfigCollection;

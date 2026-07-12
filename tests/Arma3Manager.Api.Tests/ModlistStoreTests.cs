using Arma3Manager.Api.Contracts;
using Arma3Manager.Api.Infrastructure.Persistence;
using Xunit;

namespace Arma3Manager.Api.Tests;

public sealed class ModlistStoreTests
{
    [Fact]
    public async Task OnlyOneSavedModlistCanBeActiveAndInvalidIdsDoNotReplaceIt()
    {
        using var fixture = new TemporaryDirectory();
        var store = new SqliteStore(Path.Combine(fixture.Path, "manager.sqlite3"));
        await store.InitAsync();
        var first = await store.SaveModlistAsync(new("First", [new("Mod 1", "100")], false));
        var second = await store.SaveModlistAsync(new("Second", [new("Mod 2", "200")], false));

        Assert.Equal(first.Id, (await store.ActivateModlistAsync(first.Id)).ActiveModlistId);
        Assert.Equal(second.Id, (await store.ActivateModlistAsync(second.Id)).ActiveModlistId);
        Assert.Equal(second.Id, (await store.ActivateModlistAsync("missing")).ActiveModlistId);
    }

    [Fact]
    public async Task DeletingActiveModlistClearsActiveSelection()
    {
        using var fixture = new TemporaryDirectory();
        var store = new SqliteStore(Path.Combine(fixture.Path, "manager.sqlite3"));
        await store.InitAsync();
        var list = await store.SaveModlistAsync(new("Active", [], true));

        await store.DeleteModlistAsync(list.Id);

        Assert.Null((await store.GetModlistsAsync()).ActiveModlistId);
    }

    [Fact]
    public async Task DeactivatingModlistClearsSelectionAndOnlyDisablesItsMods()
    {
        using var fixture = new TemporaryDirectory();
        var store = new SqliteStore(Path.Combine(fixture.Path, "manager.sqlite3"));
        await store.InitAsync();
        var firstPath = Path.Combine(fixture.Path, "@100");
        var unrelatedPath = Path.Combine(fixture.Path, "@300");
        await store.UpsertModAsync(new("one", "One", firstPath, true, "100"));
        await store.UpsertModAsync(new("other", "Manual server mod", unrelatedPath, true, null));
        var list = await store.SaveModlistAsync(new("Active", [new("One", "100")], true));

        var state = await store.DeactivateModlistAsync(list.Id);

        Assert.Null(state.ActiveModlistId);
        var activePaths = await store.GetActiveModsAsync();
        Assert.DoesNotContain(firstPath, activePaths);
        Assert.Contains(unrelatedPath, activePaths);
    }

    sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"a3mgr-modlists-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}

using Arma3Manager.Api.Application;
using Arma3Manager.Api.Infrastructure.Persistence;
using Xunit;

namespace Arma3Manager.Api.Tests;

/// <summary>
/// Reproduces the exact user-reported flow that produced two "Respuesta biologica" rows: upload an HTML
/// Workshop collection export, parse it into mods, then save it as a modlist — done twice in a row, the
/// way a double-click (or Save followed by Install, which also saves) would. Uses a real HTML file on disk
/// so PresetParser.Parse is exercised too, not just SaveModlistAsync in isolation.
/// </summary>
public sealed class PresetImportTests
{
    // A trimmed but structurally real Steam Workshop collection export: each mod is an <a> pointing at
    // sharedfiles/filedetails/?id=<workshopId>, the same markup PresetParser's regex targets. One id
    // (110000001) is linked twice, like a collection page's thumbnail + title links to the same item —
    // PresetParser must de-duplicate that on its own, independent of the modlist-save fix.
    const string CollectionHtml = """
        <html><body>
        <div class="collectionItem">
          <a class="title" href="https://steamcommunity.com/sharedfiles/filedetails/?id=110000001">CBA_A3</a>
          <a class="thumb" href="https://steamcommunity.com/sharedfiles/filedetails/?id=110000001"><img/></a>
        </div>
        <div class="collectionItem">
          <a class="title" href="https://steamcommunity.com/sharedfiles/filedetails/?id=110000002">ACE3</a>
        </div>
        <div class="collectionItem">
          <a class="title" href="https://steamcommunity.com/sharedfiles/filedetails/?id=110000003">Respuesta Biologica</a>
        </div>
        </body></html>
        """;

    [Fact]
    public async Task LoadingAndSavingTheSamePresetTwiceStillProducesOnlyOneModlist()
    {
        using var fixture = new TemporaryDirectory();
        var presetPath = Path.Combine(fixture.Path, "respuesta-biologica.html");
        await File.WriteAllTextAsync(presetPath, CollectionHtml);

        // "Load Preset": parse the uploaded HTML exactly like POST /api/mods/preset does.
        var parsedMods = PresetParser.Parse(await File.ReadAllTextAsync(presetPath));
        Assert.Equal(3, parsedMods.Count); // the duplicated 110000001 link collapses to one entry

        var store = new SqliteStore(Path.Combine(fixture.Path, "manager.sqlite3"));
        await store.InitAsync();

        // "Save Modlist" fired twice under the same name — the reported bug.
        var firstSave = await store.SaveModlistAsync(new("Respuesta biologica", parsedMods, false));
        var secondSave = await store.SaveModlistAsync(new("Respuesta biologica", parsedMods, false));

        Assert.Equal(firstSave.Id, secondSave.Id);
        var state = await store.GetModlistsAsync();
        var saved = Assert.Single(state.Lists);
        Assert.Equal("Respuesta biologica", saved.Name);
        Assert.Equal(3, saved.Mods.Count);
        Assert.Equal(["110000001", "110000002", "110000003"], saved.Mods.Select(mod => mod.WorkshopId).Order());
    }

    sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"a3mgr-preset-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}

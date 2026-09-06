using BattlegroundsVisionAgent.Vision.Catalog;
using Microsoft.Data.Sqlite;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class CardCatalogUpdaterTests
{
    [Fact]
    public async Task UpdateFromDownloadAsync_RejectsReparsePointStagingRootAndPreservesCurrentCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), $"catalog-reparse-{Guid.NewGuid():N}");
        var current = Path.Combine(root, "catalog");
        Directory.CreateDirectory(current);
        await File.WriteAllTextAsync(Path.Combine(current, "sentinel.txt"), "old");
        try
        {
            var updater = new CardCatalogUpdater(isReparsePoint: path => path.EndsWith(".staging", StringComparison.Ordinal));
            await Assert.ThrowsAsync<InvalidDataException>(() => updater.UpdateFromDownloadAsync(current,
                async (staging, _) => await CreateStagingCatalog(staging, "CARD_A", "a.png"),
                new CatalogManifest("v1", ["CARD_A"], ["a.png"])));
            Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(current, "sentinel.txt")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task UpdateFromDownloadAsync_KeepsCommittedNewDirectory_WhenBackupCleanupFails()
    {
        var root = Path.Combine(Path.GetTempPath(), $"catalog-rollback-{Guid.NewGuid():N}");
        var current = Path.Combine(root, "catalog");
        Directory.CreateDirectory(current);
        await File.WriteAllTextAsync(Path.Combine(current, "sentinel.txt"), "old");
        try
        {
            var updater = new CardCatalogUpdater(path =>
            {
                if (path.EndsWith(".backup", StringComparison.Ordinal)) throw new IOException("cleanup failed");
                Directory.Delete(path, true);
            });
            await updater.UpdateFromDownloadAsync(current,
                async (staging, _) => await CreateStagingCatalog(staging, "CARD_A", "a.png"),
                new CatalogManifest("v1", ["CARD_A"], ["a.png"]));

            Assert.True(File.Exists(Path.Combine(current, "catalog.db")));
            Assert.True(Directory.Exists(current + ".backup"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UpdateFromDownloadAsync_RejectsMetadataVersionMismatch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"catalog-version-{Guid.NewGuid():N}");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => new CardCatalogUpdater().UpdateFromDownloadAsync(Path.Combine(root, "catalog"),
                async (staging, _) => await CreateStagingCatalog(staging, "CARD_A", "a.png", "v2"),
                new CatalogManifest("v1", ["CARD_A"], ["a.png"])));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task UpdateFromDownloadAsync_RejectsManifestThatDoesNotExactlyMatchStagingCards()
    {
        var root = Path.Combine(Path.GetTempPath(), $"catalog-manifest-{Guid.NewGuid():N}");
        var current = Path.Combine(root, "catalog");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => new CardCatalogUpdater().UpdateFromDownloadAsync(current,
                async (staging, _) => { await CreateStagingCatalog(staging, "CARD_A", "a.png"); },
                new CatalogManifest("v1", ["CARD_B"], ["a.png"])));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ReplaceFromStaging_RejectsStagingOutsideTheCatalogDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"catalog-volume-{Guid.NewGuid():N}");
        var catalog = Path.Combine(root, "catalog.db");
        var staging = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.db");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(catalog, "old");
            File.WriteAllText(staging, "new");
            Assert.Throws<ArgumentException>(() => new CardCatalogUpdater().ReplaceFromStaging(catalog, staging));
            Assert.Equal("old", File.ReadAllText(catalog));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); if (File.Exists(staging)) File.Delete(staging); }
    }

    [Fact]
    public async Task UpdateFromDownloadAsync_PreservesCurrentCatalogAndCleansStaging_WhenDownloadFails()
    {
        var root = Path.Combine(Path.GetTempPath(), $"catalog-update-{Guid.NewGuid():N}");
        var current = Path.Combine(root, "catalog");
        Directory.CreateDirectory(current);
        await File.WriteAllTextAsync(Path.Combine(current, "sentinel.txt"), "old");
        try
        {
            var updater = new CardCatalogUpdater();

            await Assert.ThrowsAsync<InvalidOperationException>(() => updater.UpdateFromDownloadAsync(
                current,
                (_, _) => throw new InvalidOperationException("download failed"),
                new CatalogManifest("v1", [], [])));

            Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(current, "sentinel.txt")));
            Assert.False(Directory.Exists(current + ".staging"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static Task CreateStagingCatalog(string staging, string cardId, string imagePath, string version = "v1")
    {
        Directory.CreateDirectory(staging);
        var database = Path.Combine(staging, "catalog.db");
        new CardCatalog(database).Initialize();
        new CardCatalog(database).SetMetadata(version, DateTimeOffset.UnixEpoch);
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO cards(card_id, name_zh_cn, tier, image_path) VALUES($id, 'test', 1, $path)";
            command.Parameters.AddWithValue("$id", cardId);
            command.Parameters.AddWithValue("$path", imagePath);
            command.ExecuteNonQuery();
        }
        using var image = new Mat(10, 10, MatType.CV_8UC1, Scalar.All(10));
        Cv2.ImWrite(Path.Combine(staging, imagePath), image);
        return Task.CompletedTask;
    }
}

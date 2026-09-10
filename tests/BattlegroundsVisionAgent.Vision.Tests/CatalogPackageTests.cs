using System.IO.Compression;
using System.Text.Json;
using BattlegroundsVisionAgent.Vision.Catalog;
using Microsoft.Data.Sqlite;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class CatalogPackageTests
{
    [Fact]
    public void CardCatalog_ReadsVersionAndEntriesFromTheActiveDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), $"catalog-read-{Guid.NewGuid():N}");
        var database = Path.Combine(root, "catalog.db");
        try
        {
            Directory.CreateDirectory(root);
            var catalog = new CardCatalog(database);
            catalog.Initialize();
            catalog.SetMetadata("2026.09.05", DateTimeOffset.Parse("2026-09-05T08:00:00+08:00"));
            InsertCard(database, "CARD_B", "测试乙", 3, "b.png");
            InsertCard(database, "CARD_A", "测试甲", 2, "a.png");

            var snapshot = catalog.ReadSnapshot();

            Assert.Equal("2026.09.05", snapshot.Metadata?.Version);
            Assert.Equal(["CARD_A", "CARD_B"], snapshot.Entries.Select(card => card.CardId));
            Assert.Equal(2, snapshot.Entries[0].Tier);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateFromPackageAsync_AppliesNewerVersionAndSkipsSameVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), $"catalog-package-{Guid.NewGuid():N}");
        var catalogDirectory = Path.Combine(root, "catalog");
        var firstPackage = Path.Combine(root, "catalog-v1.zip");
        var secondPackage = Path.Combine(root, "catalog-v2.zip");
        try
        {
            await CreatePackageAsync(firstPackage, "2026.09.05", [Card("CARD_A", "测试甲", 2, "cards/a.png")]);
            await CreatePackageAsync(secondPackage, "2026.09.06", [
                Card("CARD_A", "测试甲（更新）", 2, "cards/a.png"),
                Card("CARD_B", "测试乙", 3, "cards/b.png")]);
            var updater = new CardCatalogUpdater();

            var first = await updater.UpdateFromPackageAsync(catalogDirectory, firstPackage);
            var second = await updater.UpdateFromPackageAsync(catalogDirectory, secondPackage);
            var same = await updater.UpdateFromPackageAsync(catalogDirectory, secondPackage);
            var snapshot = new CardCatalog(Path.Combine(catalogDirectory, "catalog.db")).ReadSnapshot();

            Assert.True(first.Applied);
            Assert.True(second.Applied);
            Assert.False(same.Applied);
            Assert.Equal("2026.09.06", snapshot.Metadata?.Version);
            Assert.Equal(["CARD_A", "CARD_B"], snapshot.Entries.Select(card => card.CardId));
            Assert.Equal("测试甲（更新）", snapshot.Entries[0].NameZhCn);
            Assert.False(Directory.Exists(catalogDirectory + ".staging"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateFromPackageAsync_RejectsOlderVersionAndPreservesCurrentCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), $"catalog-downgrade-{Guid.NewGuid():N}");
        var catalogDirectory = Path.Combine(root, "catalog");
        var currentPackage = Path.Combine(root, "current.zip");
        var olderPackage = Path.Combine(root, "older.zip");
        try
        {
            await CreatePackageAsync(currentPackage, "2026.09.06", [Card("CARD_CURRENT", "当前版本", 4, "cards/current.png")]);
            await CreatePackageAsync(olderPackage, "2026.09.05", [Card("CARD_OLD", "旧版本", 1, "cards/old.png")]);
            var updater = new CardCatalogUpdater();
            await updater.UpdateFromPackageAsync(catalogDirectory, currentPackage);

            await Assert.ThrowsAsync<InvalidDataException>(() => updater.UpdateFromPackageAsync(catalogDirectory, olderPackage));

            var snapshot = new CardCatalog(Path.Combine(catalogDirectory, "catalog.db")).ReadSnapshot();
            Assert.Equal("2026.09.06", snapshot.Metadata?.Version);
            Assert.Equal("CARD_CURRENT", Assert.Single(snapshot.Entries).CardId);
            Assert.False(Directory.Exists(catalogDirectory + ".staging"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateFromPackageAsync_RejectsZipSlipEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), $"catalog-zipslip-{Guid.NewGuid():N}");
        var catalogDirectory = Path.Combine(root, "catalog");
        var packagePath = Path.Combine(root, "unsafe.zip");
        var escaped = Path.Combine(root, "escaped.txt");
        try
        {
            Directory.CreateDirectory(root);
            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../escaped.txt");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("must not be extracted");
            }

            await Assert.ThrowsAsync<InvalidDataException>(() => new CardCatalogUpdater()
                .UpdateFromPackageAsync(catalogDirectory, packagePath));

            Assert.False(File.Exists(escaped));
            Assert.False(Directory.Exists(catalogDirectory + ".staging"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateFromPackageAsync_RebuildsNormalFeatureWhenArtworkChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), $"catalog-feature-refresh-{Guid.NewGuid():N}");
        var catalogDirectory = Path.Combine(root, "catalog");
        var firstPackage = Path.Combine(root, "catalog-v1.zip");
        var secondPackage = Path.Combine(root, "catalog-v2.zip");
        try
        {
            var cards = new[] { Card("CARD_A", "测试甲", 2, "cards/a.png") };
            using var oldArtwork = CreateArtwork(1);
            await CreatePackageWithFeatureAsync(firstPackage, "2026.09.05", cards, artworkVariant: 1);
            await new CardCatalogUpdater().UpdateFromPackageAsync(catalogDirectory, firstPackage);

            await CreatePackageWithFeatureAsync(secondPackage, "2026.09.06", cards, artworkVariant: 2, staleFeatureImage: oldArtwork);
            await new CardCatalogUpdater().UpdateFromPackageAsync(catalogDirectory, secondPackage);

            using var newArtwork = CreateArtwork(2);
            var expected = CardFeatureFactory.Create("CARD_A", newArtwork, isGolden: false);
            var actual = Assert.Single(new CardFeatureStore(Path.Combine(catalogDirectory, "catalog.db")).GetAll());

            Assert.Equal(expected.PerceptualHash, actual.PerceptualHash);
            Assert.Equal(expected.Descriptor, actual.Descriptor);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static CatalogPackageCard Card(string id, string name, int tier, string imagePath) =>
        new(id, name, tier, imagePath);

    private static void InsertCard(string database, string id, string name, int tier, string imagePath)
    {
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO cards(card_id, name_zh_cn, tier, image_path) VALUES($id, $name, $tier, $imagePath)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$tier", tier);
        command.Parameters.AddWithValue("$imagePath", imagePath);
        command.ExecuteNonQuery();
    }

    private static async Task CreatePackageAsync(string packagePath, string version, IReadOnlyCollection<CatalogPackageCard> cards)
    {
        var packageRoot = Path.Combine(Path.GetDirectoryName(packagePath)!, Path.GetFileNameWithoutExtension(packagePath));
        Directory.CreateDirectory(Path.Combine(packageRoot, "cards"));
        var updatedAt = DateTimeOffset.Parse($"{version}T08:00:00+08:00");
        var database = Path.Combine(packageRoot, "catalog.db");
        var catalog = new CardCatalog(database);
        catalog.Initialize();
        foreach (var card in cards)
        {
            InsertCard(database, card.CardId, card.NameZhCn, card.Tier, card.ImagePath);
            var imagePath = Path.Combine(packageRoot, card.ImagePath.Replace('/', Path.DirectorySeparatorChar));
            using var image = new Mat(20, 20, MatType.CV_8UC1, Scalar.All((byte)(card.Tier * 20 + 10)));
            Cv2.ImWrite(imagePath, image);
        }
        catalog.SetMetadata(version, updatedAt);
        var manifest = new CatalogPackageManifest(version, updatedAt, cards);

        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            AddFile(archive, database, "catalog.db");
            foreach (var card in cards)
                AddFile(archive, Path.Combine(packageRoot, card.ImagePath.Replace('/', Path.DirectorySeparatorChar)), card.ImagePath.Replace('\\', '/'));
            var manifestEntry = archive.CreateEntry(CatalogPackageManifest.FileName);
            await using var stream = manifestEntry.Open();
            await JsonSerializer.SerializeAsync(stream, manifest);
        }

        Directory.Delete(packageRoot, recursive: true);
    }

    private static async Task CreatePackageWithFeatureAsync(
        string packagePath,
        string version,
        IReadOnlyCollection<CatalogPackageCard> cards,
        int artworkVariant,
        Mat? staleFeatureImage = null)
    {
        var packageRoot = Path.Combine(Path.GetDirectoryName(packagePath)!, Path.GetFileNameWithoutExtension(packagePath));
        Directory.CreateDirectory(Path.Combine(packageRoot, "cards"));
        var updatedAt = DateTimeOffset.Parse($"{version}T08:00:00+08:00");
        var database = Path.Combine(packageRoot, "catalog.db");
        var catalog = new CardCatalog(database);
        catalog.Initialize();
        foreach (var card in cards)
        {
            InsertCard(database, card.CardId, card.NameZhCn, card.Tier, card.ImagePath);
            using var image = CreateArtwork(artworkVariant);
            Cv2.ImWrite(Path.Combine(packageRoot, card.ImagePath.Replace('/', Path.DirectorySeparatorChar)), image);
        }

        if (staleFeatureImage is not null)
            new CardFeatureStore(database).Upsert(CardFeatureFactory.Create(cards.First().CardId, staleFeatureImage, isGolden: false));

        catalog.SetMetadata(version, updatedAt);
        var manifest = new CatalogPackageManifest(version, updatedAt, cards);
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            AddFile(archive, database, "catalog.db");
            foreach (var card in cards)
                AddFile(archive, Path.Combine(packageRoot, card.ImagePath.Replace('/', Path.DirectorySeparatorChar)), card.ImagePath.Replace('\\', '/'));
            var manifestEntry = archive.CreateEntry(CatalogPackageManifest.FileName);
            await using var stream = manifestEntry.Open();
            await JsonSerializer.SerializeAsync(stream, manifest);
        }

        Directory.Delete(packageRoot, recursive: true);
    }

    private static Mat CreateArtwork(int variant)
    {
        var image = new Mat(420, 320, MatType.CV_8UC1, Scalar.All(18 + variant * 8));
        Cv2.Rectangle(image, new Rect(18 + variant * 3, 20, 270, 370), Scalar.All(200 - variant * 15), 4);
        Cv2.Circle(image, new Point(160, 200), 70 + variant * 8, Scalar.All(130 + variant * 12), 5);
        Cv2.Line(image, new Point(30, 60 + variant * 20), new Point(280, 340), Scalar.All(240), 4);
        Cv2.PutText(image, $"C{variant}", new Point(70, 230), HersheyFonts.HersheySimplex, 2, Scalar.All(255), 4);
        return image;
    }

    private static void AddFile(ZipArchive archive, string path, string entryName)
    {
        var entry = archive.CreateEntry(entryName);
        using var input = File.OpenRead(path);
        using var output = entry.Open();
        input.CopyTo(output);
    }
}

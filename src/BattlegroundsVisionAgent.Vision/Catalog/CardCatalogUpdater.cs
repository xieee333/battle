using System.IO.Compression;
using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenCvSharp;
using BattlegroundsVisionAgent.Core.Domain;

namespace BattlegroundsVisionAgent.Vision.Catalog;

public sealed record CatalogManifest(string Version, IReadOnlyCollection<string> CardIds, IReadOnlyCollection<string> ImagePaths);

public sealed class CardCatalogUpdater
{
    private readonly Action<string> _deleteDirectory;
    private readonly Func<string, bool> _isReparsePoint;

    public CardCatalogUpdater(Action<string>? deleteDirectory = null, Func<string, bool>? isReparsePoint = null)
    {
        _deleteDirectory = deleteDirectory ?? (path => Directory.Delete(path, recursive: true));
        _isReparsePoint = isReparsePoint ?? IsReparsePoint;
    }

    public async Task<CatalogUpdateResult> UpdateFromPackageAsync(
        string catalogDirectory,
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        if (!File.Exists(packagePath))
            throw new FileNotFoundException("Catalog update package was not found.", packagePath);

        var stagingDirectory = catalogDirectory + ".staging";
        if (Directory.Exists(stagingDirectory))
        {
            EnsureNotReparsePoint(stagingDirectory);
            Directory.Delete(stagingDirectory, recursive: true);
        }

        try
        {
            await ExtractPackageToStagingAsync(packagePath, stagingDirectory, cancellationToken);
            EnsureNotReparsePoint(stagingDirectory);
            var manifest = CatalogPackageManifest.Load(Path.Combine(stagingDirectory, CatalogPackageManifest.FileName));
            ValidatePackageDirectory(stagingDirectory, manifest);
            BuildMissingNormalFeatures(stagingDirectory, manifest);

            var currentDatabase = Path.Combine(catalogDirectory, "catalog.db");
            var currentMetadata = new CardCatalog(currentDatabase).GetMetadata();
            if (currentMetadata is not null)
            {
                var comparison = CompareVersions(manifest.Version, currentMetadata.Version);
                if (comparison == 0)
                {
                    DeleteStagingDirectory(stagingDirectory);
                    return new CatalogUpdateResult(false, currentMetadata.Version, currentMetadata.UpdatedAt,
                        new CardCatalog(currentDatabase).GetEntries().Count, "Catalog is already up to date.");
                }

                if (comparison < 0)
                    throw new InvalidDataException($"Catalog package version {manifest.Version} is older than the current version {currentMetadata.Version}.");
            }

            ReplaceDirectory(catalogDirectory, stagingDirectory);
            return new CatalogUpdateResult(true, manifest.Version, manifest.UpdatedAt, manifest.Cards.Count,
                "Catalog update applied.");
        }
        catch
        {
            if (Directory.Exists(stagingDirectory) && !_isReparsePoint(stagingDirectory))
                _deleteDirectory(stagingDirectory);
            throw;
        }
    }

    public async Task UpdateFromDownloadAsync(string catalogDirectory,
        Func<string, CancellationToken, Task> downloadToStaging,
        CatalogManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDirectory);
        ArgumentNullException.ThrowIfNull(downloadToStaging);
        ArgumentNullException.ThrowIfNull(manifest);
        var stagingDirectory = catalogDirectory + ".staging";
        if (Directory.Exists(stagingDirectory))
        {
            EnsureNotReparsePoint(stagingDirectory);
            Directory.Delete(stagingDirectory, recursive: true);
        }
        try
        {
            await downloadToStaging(stagingDirectory, cancellationToken);
            EnsureNotReparsePoint(stagingDirectory);
            ValidateDirectory(stagingDirectory, manifest);
            ReplaceDirectory(catalogDirectory, stagingDirectory);
        }
        catch
        {
            if (Directory.Exists(stagingDirectory) && !_isReparsePoint(stagingDirectory)) _deleteDirectory(stagingDirectory);
            throw;
        }
    }

    private void EnsureNotReparsePoint(string path)
    {
        if (_isReparsePoint(path))
            throw new InvalidDataException("The staging root must not be a reparse point.");
    }

    private static bool IsReparsePoint(string path) => Directory.Exists(path)
        && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    public void ReplaceFromStaging(string catalogPath, string stagingPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingPath);
        if (!string.Equals(Path.GetFullPath(stagingPath), Path.GetFullPath(catalogPath + ".staging"), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Staging must be the catalog's adjacent .staging path.", nameof(stagingPath));
        var backupPath = catalogPath + ".backup";
        var replaced = false;
        try
        {
            Validate(stagingPath);
            if (File.Exists(backupPath))
                File.Delete(backupPath);
            if (File.Exists(catalogPath))
            {
                File.Replace(stagingPath, catalogPath, backupPath, ignoreMetadataErrors: true);
                replaced = true;
            }
            else
                File.Move(stagingPath, catalogPath);
            if (File.Exists(backupPath))
                File.Delete(backupPath);
        }
        catch
        {
            if (replaced && File.Exists(backupPath))
            {
                try { File.Replace(backupPath, catalogPath, null, ignoreMetadataErrors: true); }
                catch { /* preserve the original failure; callers still receive an update failure. */ }
            }
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);
            throw;
        }
    }

    private static void Validate(string stagingPath)
    {
        if (!File.Exists(stagingPath))
            throw new InvalidDataException("The staging catalog is missing.");
        using var connection = new SqliteConnection($"Data Source={stagingPath};Pooling=False");
        connection.Open();
        using var duplicateCheck = connection.CreateCommand();
        duplicateCheck.CommandText = "SELECT card_id FROM cards GROUP BY card_id HAVING COUNT(*) > 1";
        using var duplicates = duplicateCheck.ExecuteReader();
        if (duplicates.Read())
            throw new InvalidDataException("Card ids must be unique.");
        using var images = connection.CreateCommand();
        images.CommandText = "SELECT image_path FROM cards";
        using var reader = images.ExecuteReader();
        while (reader.Read())
        {
            var relativePath = reader.GetString(0);
            if (!TryResolveStagingPath(Path.GetDirectoryName(Path.GetFullPath(stagingPath))!, relativePath, out var path))
                throw new InvalidDataException($"Card image path escapes staging: {relativePath}");
            if (!File.Exists(path))
                throw new InvalidDataException($"Card image is missing: {path}");
            using var decoded = Cv2.ImRead(path, ImreadModes.Grayscale);
            if (decoded.Empty())
                throw new InvalidDataException($"Card image cannot be decoded: {path}");
        }
    }

    private static void ValidateDirectory(string stagingDirectory, CatalogManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version)
            || manifest.CardIds.Count != manifest.CardIds.Distinct(StringComparer.Ordinal).Count()
            || manifest.ImagePaths.Count != manifest.ImagePaths.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidDataException("The catalog manifest is invalid.");
        var database = Path.Combine(stagingDirectory, "catalog.db");
        Validate(database);
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT card_id, image_path FROM cards";
        using var reader = command.ExecuteReader();
        var cards = new List<(string CardId, string ImagePath)>();
        while (reader.Read())
            cards.Add((reader.GetString(0), reader.GetString(1)));
        reader.Close();
        using var metadata = connection.CreateCommand();
        metadata.CommandText = "SELECT version FROM catalog_meta";
        using var versions = metadata.ExecuteReader();
        var metadataVersions = new List<string>();
        while (versions.Read()) metadataVersions.Add(versions.GetString(0));
        if (metadataVersions.Count != 1 || !string.Equals(metadataVersions[0], manifest.Version, StringComparison.Ordinal))
            throw new InvalidDataException("Catalog metadata version does not match the manifest.");
        if (!manifest.CardIds.Order(StringComparer.Ordinal).SequenceEqual(cards.Select(card => card.CardId).Order(StringComparer.Ordinal))
            || !manifest.ImagePaths.Order(StringComparer.Ordinal).SequenceEqual(cards.Select(card => card.ImagePath).Order(StringComparer.Ordinal)))
            throw new InvalidDataException("The manifest does not match staging cards.");
        foreach (var imagePath in manifest.ImagePaths)
        {
            if (!TryResolveStagingPath(stagingDirectory, imagePath, out var fullPath))
                throw new InvalidDataException($"Manifest image path escapes staging: {imagePath}");
            using var image = Cv2.ImRead(fullPath, ImreadModes.Grayscale);
            if (image.Empty())
                throw new InvalidDataException($"Manifest image cannot be decoded: {imagePath}");
        }
    }

    private static void ValidatePackageDirectory(string stagingDirectory, CatalogPackageManifest manifest)
    {
        Validate(Path.Combine(stagingDirectory, "catalog.db"));

        using var connection = new SqliteConnection($"Data Source={Path.Combine(stagingDirectory, "catalog.db")};Pooling=False");
        connection.Open();
        using var cardsCommand = connection.CreateCommand();
        cardsCommand.CommandText = "SELECT card_id, name_zh_cn, tier, image_path, kind FROM cards";
        using var reader = cardsCommand.ExecuteReader();
        var actualCards = new List<CatalogPackageCard>();
        while (reader.Read())
        {
            actualCards.Add(new CatalogPackageCard(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
                CardCatalog.ParseKind(reader.GetString(4))));
        }

        var expectedCards = manifest.Cards
            .OrderBy(card => card.CardId, StringComparer.Ordinal)
            .ToArray();
        if (!expectedCards.SequenceEqual(actualCards.OrderBy(card => card.CardId, StringComparer.Ordinal)))
            throw new InvalidDataException("The package manifest does not match card metadata.");

        using var metadataCommand = connection.CreateCommand();
        metadataCommand.CommandText = "SELECT version, updated_at FROM catalog_meta ORDER BY rowid DESC LIMIT 1";
        using var metadata = metadataCommand.ExecuteReader();
        if (!metadata.Read()
            || !string.Equals(metadata.GetString(0), manifest.Version, StringComparison.Ordinal)
            || !DateTimeOffset.TryParse(metadata.GetString(1), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var updatedAt)
            || updatedAt != manifest.UpdatedAt)
            throw new InvalidDataException("The package metadata does not match the manifest.");
    }

    private static void BuildMissingNormalFeatures(string stagingDirectory, CatalogPackageManifest manifest)
    {
        var databasePath = Path.Combine(stagingDirectory, "catalog.db");
        new CardCatalog(databasePath).Initialize();
        var store = new CardFeatureStore(databasePath);

        foreach (var card in manifest.Cards)
        {
            if (!TryResolveStagingPath(stagingDirectory, card.ImagePath, out var imagePath))
                throw new InvalidDataException($"Card image path escapes staging: {card.ImagePath}");

            using var image = Cv2.ImRead(imagePath, ImreadModes.Grayscale);
            if (image.Empty())
                throw new InvalidDataException($"Card image cannot be decoded: {card.ImagePath}");
            store.Upsert(CardFeatureFactory.Create(card.CardId, image, isGolden: false, card.Kind));
        }
    }

    private static async Task ExtractPackageToStagingAsync(
        string packagePath,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        const int maxEntryCount = 100_000;
        const long maxUncompressedBytes = 512L * 1024 * 1024;
        Directory.CreateDirectory(stagingDirectory);
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count > maxEntryCount)
            throw new InvalidDataException("The catalog package contains too many files.");

        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.FullName)
                || entry.FullName.EndsWith("/", StringComparison.Ordinal)
                || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                continue;

            if (!TryResolveStagingPath(stagingDirectory, entry.FullName, out var destination))
                throw new InvalidDataException($"Catalog package entry escapes staging: {entry.FullName}");

            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > maxUncompressedBytes)
                throw new InvalidDataException("The catalog package is too large.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private void DeleteStagingDirectory(string stagingDirectory)
    {
        if (Directory.Exists(stagingDirectory) && !_isReparsePoint(stagingDirectory))
            _deleteDirectory(stagingDirectory);
    }

    private static int CompareVersions(string candidate, string current)
    {
        if (candidate.StartsWith("blizzard-", StringComparison.OrdinalIgnoreCase)
            && current.StartsWith("blizzard-", StringComparison.OrdinalIgnoreCase))
            return string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase) ? 0 : 1;

        var candidateVersion = ParseVersion(candidate);
        var currentVersion = ParseVersion(current);
        return candidateVersion is not null && currentVersion is not null
            ? candidateVersion.CompareTo(currentVersion)
            : StringComparer.Ordinal.Compare(candidate, current);
    }

    private static Version? ParseVersion(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            normalized = normalized[1..];
        return Version.TryParse(normalized, out var parsed) ? parsed : null;
    }

    private static bool TryResolveStagingPath(string root, string path, out string fullPath)
    {
        fullPath = string.Empty;
        if (Path.IsPathRooted(path)) return false;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, path));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return false;
        var current = fullRoot.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var part in Path.GetRelativePath(fullRoot, candidate).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return false;
        }
        fullPath = candidate;
        return true;
    }

    private void ReplaceDirectory(string catalogDirectory, string stagingDirectory)
    {
        var backupDirectory = catalogDirectory + ".backup";
        if (Directory.Exists(backupDirectory))
            Directory.Delete(backupDirectory, recursive: true);
        var movedCurrent = false;
        CatalogAccessGate.Instance.EnterWriteLock();
        try
        {
            if (Directory.Exists(catalogDirectory))
            {
                Directory.Move(catalogDirectory, backupDirectory);
                movedCurrent = true;
            }
            Directory.Move(stagingDirectory, catalogDirectory);
            if (Directory.Exists(backupDirectory))
            {
                try { _deleteDirectory(backupDirectory); }
                catch { return; }
            }
        }
        catch
        {
            if (movedCurrent && Directory.Exists(backupDirectory))
            {
                if (Directory.Exists(catalogDirectory))
                    Directory.Move(catalogDirectory, stagingDirectory);
                Directory.Move(backupDirectory, catalogDirectory);
            }
            throw;
        }
        finally { CatalogAccessGate.Instance.ExitWriteLock(); }
    }
}

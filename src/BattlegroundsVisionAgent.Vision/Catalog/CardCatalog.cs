using Microsoft.Data.Sqlite;
using System.Globalization;
using BattlegroundsVisionAgent.Core.Domain;

namespace BattlegroundsVisionAgent.Vision.Catalog;

public sealed record CardCatalogEntry(
    string CardId,
    string NameZhCn,
    int Tier,
    string ImagePath,
    CardKind Kind = CardKind.Minion);
public sealed record CatalogMetadata(string Version, DateTimeOffset UpdatedAt);
public sealed record CardCatalogSnapshot(CatalogMetadata? Metadata, IReadOnlyList<CardCatalogEntry> Entries);

public sealed class CardCatalog
{
    public CardCatalog(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DatabasePath))!);
        using var connection = Open();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS cards(card_id TEXT PRIMARY KEY, name_zh_cn TEXT NOT NULL, tier INTEGER NOT NULL, image_path TEXT NOT NULL, kind TEXT NOT NULL DEFAULT 'Minion');
            CREATE TABLE IF NOT EXISTS features(card_id TEXT NOT NULL, variant TEXT NOT NULL, phash TEXT NOT NULL, descriptor BLOB NOT NULL, descriptor_rows INTEGER NOT NULL, descriptor_columns INTEGER NOT NULL, keypoints BLOB NOT NULL, kind TEXT NOT NULL DEFAULT 'Minion', PRIMARY KEY(card_id, variant));
            CREATE TABLE IF NOT EXISTS catalog_meta(version TEXT NOT NULL, updated_at TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "cards", "kind", "TEXT NOT NULL DEFAULT 'Minion'");
        EnsureColumn(connection, "features", "kind", "TEXT NOT NULL DEFAULT 'Minion'");
    }

    public void SetMetadata(string version, DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Initialize();
        CatalogAccessGate.Instance.EnterWriteLock();
        try
        {
            using var connection = Open();
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM catalog_meta";
            clear.ExecuteNonQuery();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO catalog_meta(version, updated_at) VALUES($version, $updatedAt)";
            insert.Parameters.AddWithValue("$version", version);
            insert.Parameters.AddWithValue("$updatedAt", updatedAt.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
            transaction.Commit();
        }
        finally { CatalogAccessGate.Instance.ExitWriteLock(); }
    }

    public IReadOnlyList<CardCatalogEntry> GetEntries() => ReadSnapshot().Entries;

    public CatalogMetadata? GetMetadata() => ReadSnapshot().Metadata;

    public CardCatalogSnapshot ReadSnapshot()
    {
        if (!File.Exists(DatabasePath))
            return new CardCatalogSnapshot(null, []);

        // Catalogs packaged before card kinds were introduced are migrated on
        // first read, so offline recognition can use the new reader directly.
        Initialize();

        CatalogAccessGate.Instance.EnterReadLock();
        try
        {
            using var connection = Open();
            connection.Open();

            CatalogMetadata? metadata = null;
            using (var metadataCommand = connection.CreateCommand())
            {
                metadataCommand.CommandText = "SELECT version, updated_at FROM catalog_meta ORDER BY rowid DESC LIMIT 1";
                using var reader = metadataCommand.ExecuteReader();
                if (reader.Read())
                {
                    var version = reader.GetString(0);
                    if (!DateTimeOffset.TryParse(reader.GetString(1), CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out var updatedAt))
                        throw new InvalidDataException("Catalog metadata updated_at is invalid.");
                    metadata = new CatalogMetadata(version, updatedAt);
                }
            }

            using var cardsCommand = connection.CreateCommand();
            cardsCommand.CommandText = "SELECT card_id, name_zh_cn, tier, image_path, kind FROM cards ORDER BY card_id";
            using var cards = cardsCommand.ExecuteReader();
            var entries = new List<CardCatalogEntry>();
            while (cards.Read())
            {
                entries.Add(new CardCatalogEntry(
                    cards.GetString(0),
                    cards.GetString(1),
                    cards.GetInt32(2),
                    cards.GetString(3),
                    ParseKind(cards.GetString(4))));
            }

            return new CardCatalogSnapshot(metadata, entries);
        }
        finally { CatalogAccessGate.Instance.ExitReadLock(); }
    }

    private SqliteConnection Open() => new($"Data Source={DatabasePath};Pooling=False");

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    internal static CardKind ParseKind(string? value) =>
        Enum.TryParse<CardKind>(value, ignoreCase: true, out var kind)
            ? kind
            : CardKind.Minion;
}

using OpenCvSharp;
using System.Runtime.InteropServices;
using BattlegroundsVisionAgent.Core.Domain;

namespace BattlegroundsVisionAgent.Vision.Catalog;

internal static class CatalogAccessGate
{
    internal static readonly ReaderWriterLockSlim Instance = new();
}

public sealed record CardFeature(
    string CardId,
    bool IsGolden,
    ulong PerceptualHash,
    byte[] Descriptor,
    int DescriptorRows,
    int DescriptorColumns,
    Point2f[] Keypoints,
    CardKind Kind = CardKind.Minion)
{
    public Mat ToDescriptorMat()
    {
        var descriptor = new Mat(DescriptorRows, DescriptorColumns, MatType.CV_8UC1);
        if (Descriptor.Length > 0)
            Marshal.Copy(Descriptor, 0, descriptor.Data, Descriptor.Length);
        return descriptor;
    }
}

public interface ICardFeatureStore
{
    IReadOnlyList<CardFeature> GetAll();
}

public sealed class InMemoryFeatureStore : ICardFeatureStore
{
    private readonly IReadOnlyList<CardFeature> _features;

    public InMemoryFeatureStore(IEnumerable<CardFeature> features) =>
        _features = features?.ToArray() ?? throw new ArgumentNullException(nameof(features));

    public IReadOnlyList<CardFeature> GetAll() => _features;

    public static InMemoryFeatureStore WithCard(string cardId, Mat image, bool isGolden = false,
        CardKind kind = CardKind.Minion) =>
        new([CardFeatureFactory.Create(cardId, image, isGolden, kind)]);
}

public sealed class CardFeatureStore(string databasePath) : ICardFeatureStore
{
    public IReadOnlyList<CardFeature> GetAll()
    {
        if (File.Exists(databasePath))
            new CardCatalog(databasePath).Initialize();
        CatalogAccessGate.Instance.EnterReadLock();
        try { return GetAllCore(); }
        finally { CatalogAccessGate.Instance.ExitReadLock(); }
    }

    private IReadOnlyList<CardFeature> GetAllCore()
    {
        if (!File.Exists(databasePath))
            return [];
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT card_id, variant, phash, descriptor, descriptor_rows, descriptor_columns, keypoints, kind FROM features";
        using var reader = command.ExecuteReader();
        var features = new List<CardFeature>();
        while (reader.Read())
        {
            features.Add(new CardFeature(
                reader.GetString(0),
                string.Equals(reader.GetString(1), "golden", StringComparison.OrdinalIgnoreCase),
                Convert.ToUInt64(reader.GetString(2), 16),
                (byte[])reader[3],
                reader.GetInt32(4),
                reader.GetInt32(5),
                DecodeKeypoints((byte[])reader[6]),
                CardCatalog.ParseKind(reader.GetString(7))));
        }
        return features;
    }

    public void Upsert(CardFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        CatalogAccessGate.Instance.EnterWriteLock();
        try
        {
        new CardCatalog(databasePath).Initialize();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO features(card_id, variant, phash, descriptor, descriptor_rows, descriptor_columns, keypoints, kind)
            VALUES($cardId, $variant, $hash, $descriptor, $rows, $columns, $keypoints, $kind)
            ON CONFLICT(card_id, variant) DO UPDATE SET phash=excluded.phash, descriptor=excluded.descriptor,
              descriptor_rows=excluded.descriptor_rows, descriptor_columns=excluded.descriptor_columns,
              keypoints=excluded.keypoints, kind=excluded.kind;
            """;
        command.Parameters.AddWithValue("$cardId", feature.CardId);
        command.Parameters.AddWithValue("$variant", feature.IsGolden ? "golden" : "normal");
        command.Parameters.AddWithValue("$hash", feature.PerceptualHash.ToString("X16"));
        command.Parameters.AddWithValue("$descriptor", feature.Descriptor);
        command.Parameters.AddWithValue("$rows", feature.DescriptorRows);
        command.Parameters.AddWithValue("$columns", feature.DescriptorColumns);
        command.Parameters.AddWithValue("$keypoints", EncodeKeypoints(feature.Keypoints));
        command.Parameters.AddWithValue("$kind", feature.Kind.ToString());
        command.ExecuteNonQuery();
        }
        finally { CatalogAccessGate.Instance.ExitWriteLock(); }
    }

    private static byte[] EncodeKeypoints(IReadOnlyList<Point2f> points)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(points.Count);
        foreach (var point in points) { writer.Write(point.X); writer.Write(point.Y); }
        return stream.ToArray();
    }

    private static Point2f[] DecodeKeypoints(byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        var count = reader.ReadInt32();
        return Enumerable.Range(0, count).Select(_ => new Point2f(reader.ReadSingle(), reader.ReadSingle())).ToArray();
    }
}

public static class CardFeatureFactory
{
    public static CardFeature Create(string cardId, Mat image, bool isGolden, CardKind kind = CardKind.Minion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        ArgumentNullException.ThrowIfNull(image);
        var hash = PerceptualHash.Create(image);
        using var orb = ORB.Create();
        using var descriptor = new Mat();
        orb.DetectAndCompute(image, null, out var keypoints, descriptor);
        {
            var bytes = new byte[checked(descriptor.Rows * descriptor.Cols)];
            if (bytes.Length > 0)
                Marshal.Copy(descriptor.Data, bytes, 0, bytes.Length);
            return new CardFeature(cardId, isGolden, hash, bytes, descriptor.Rows, descriptor.Cols,
                keypoints.Select(point => point.Pt).ToArray(), kind);
        }
    }
}

public static class PerceptualHash
{
    public static ulong Create(Mat image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var gray = new Mat();
        if (image.Channels() == 1)
            image.CopyTo(gray);
        else
            Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        using var resized = new Mat();
        Cv2.Resize(gray, resized, new Size(32, 32));
        using var floats = new Mat();
        resized.ConvertTo(floats, MatType.CV_32FC1);
        using var dct = new Mat();
        Cv2.Dct(floats, dct);
        var values = new List<float>(63);
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                if (x != 0 || y != 0)
                    values.Add(dct.At<float>(y, x));
        var median = values.Order().ElementAt(values.Count / 2);
        ulong hash = 0;
        for (var index = 0; index < values.Count; index++)
            if (values[index] >= median)
                hash |= 1UL << index;
        return hash;
    }
}

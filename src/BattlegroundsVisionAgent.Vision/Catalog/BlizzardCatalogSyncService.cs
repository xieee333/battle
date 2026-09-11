using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using BattlegroundsVisionAgent.Core.Domain;

namespace BattlegroundsVisionAgent.Vision.Catalog;

public sealed record OfficialCatalogSyncOptions(
    string? AccessToken = null,
    string? ClientId = null,
    string? ClientSecret = null,
    string Region = "us",
    string Locale = "zh_CN")
{
    public const string AccessTokenEnvironmentVariable = "BVA_BLIZZARD_ACCESS_TOKEN";
    public const string ClientIdEnvironmentVariable = "BVA_BLIZZARD_CLIENT_ID";
    public const string ClientSecretEnvironmentVariable = "BVA_BLIZZARD_CLIENT_SECRET";
    public const string RegionEnvironmentVariable = "BVA_BLIZZARD_REGION";
    public const string LocaleEnvironmentVariable = "BVA_BLIZZARD_LOCALE";

    public static OfficialCatalogSyncOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable(AccessTokenEnvironmentVariable),
        Environment.GetEnvironmentVariable(ClientIdEnvironmentVariable),
        Environment.GetEnvironmentVariable(ClientSecretEnvironmentVariable),
        Environment.GetEnvironmentVariable(RegionEnvironmentVariable) ?? "us",
        Environment.GetEnvironmentVariable(LocaleEnvironmentVariable) ?? "zh_CN");

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AccessToken)
            && (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret)))
            throw new InvalidOperationException(
                $"未配置暴雪 API 凭据。请设置 {AccessTokenEnvironmentVariable}，或同时设置 {ClientIdEnvironmentVariable} 和 {ClientSecretEnvironmentVariable}。\n"
                + "凭据只在本机内存中使用，不会写入项目设置文件。");

        if (string.IsNullOrWhiteSpace(Region)
            || Region.Any(character => !char.IsLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("Blizzard API region is invalid.", nameof(Region));
        if (string.IsNullOrWhiteSpace(Locale)
            || Locale.Any(character => !char.IsLetterOrDigit(character) && character != '_' && character != '-'))
            throw new ArgumentException("Blizzard API locale is invalid.", nameof(Locale));
    }
}

public sealed class BlizzardCatalogSyncService : IDisposable
{
    private const int PageSize = 500;
    private const int MaxPages = 100;
    private readonly HttpClient _httpClient;
    private readonly CardCatalogUpdater _catalogUpdater;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public const string ChinaEndpoint = "https://webapi.blizzard.cn/hs-cards-api-server/api/web/cards/tavern";

    public async Task<CatalogUpdateResult> SyncChinaAsync(string catalogDirectory, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var cards = new Dictionary<int, BlizzardCard>();
        var seenIds = new HashSet<int>();
        int? total = null;
        for (var page = 1; page <= MaxPages; page++)
        {
            using var response = await _httpClient.PostAsJsonAsync(ChinaEndpoint, new
            {
                page, page_size = 200, bg_card_type = "", bg_game_mode = "",
                minion_type = "", tier = Array.Empty<int>(), text_filter = "",
                attack = -1, health = -1, sort = "tier:asc"
            }, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var root = json.RootElement;
            if (root.GetProperty("code").GetInt32() != 0)
                throw new InvalidDataException("国服官网返回数据错误，保留原有卡库。");
            var data = root.GetProperty("data");
            var count = data.GetProperty("total").GetInt32();
            if (total.HasValue && total != count)
                throw new InvalidDataException("同步过程中官网卡池发生变化，请重新同步。");
            total = count;
            var list = data.GetProperty("list");
            foreach (var item in list.EnumerateArray())
            {
                var card = item.Deserialize<BlizzardCard>(JsonOptions)
                    ?? throw new InvalidDataException("国服卡牌数据为空。");
                if (card.Id <= 0 || !seenIds.Add(card.Id))
                    throw new InvalidDataException("国服卡牌缺少必要信息或分页重复，保留原有卡库。");
                var kind = ResolveChinaKind(EffectiveCardTypeId(card));
                if (kind is not (CardKind.Minion or CardKind.Spell))
                    continue;
                if (string.IsNullOrWhiteSpace(card.Name)
                    || card.Battlegrounds is not { Hero: false, Tier: > 0 }
                    || string.IsNullOrWhiteSpace(card.Battlegrounds.Image))
                    throw new InvalidDataException("国服卡牌缺少必要信息，保留原有卡库。");
                cards.Add(card.Id, card with
                {
                    Tier = card.Battlegrounds.Tier,
                    ImageUrl = card.Battlegrounds.Image,
                    Kind = kind
                });
            }
            if (seenIds.Count == total && cards.Count > 0)
                return await ApplyCardsAsync(catalogDirectory, cards.Values.OrderBy(card => card.Id).ToArray(), cancellationToken).ConfigureAwait(false);
            if (list.GetArrayLength() == 0 || seenIds.Count > total)
                break;
        }
        throw new InvalidDataException("未能完整获取国服卡库，保留原有卡库。");
    }

    public BlizzardCatalogSyncService(HttpClient? httpClient = null, CardCatalogUpdater? catalogUpdater = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(5);
        _catalogUpdater = catalogUpdater ?? new CardCatalogUpdater();
        _ownsHttpClient = httpClient is null;
    }

    public async Task<CatalogUpdateResult> SyncBattlegroundsAsync(
        string catalogDirectory,
        OfficialCatalogSyncOptions options,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDirectory);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var accessToken = await GetAccessTokenAsync(options, cancellationToken).ConfigureAwait(false);
        var cards = await FetchBattlegroundCardsAsync(options, accessToken, cancellationToken).ConfigureAwait(false);
        if (cards.Count == 0)
            throw new InvalidDataException("暴雪接口没有返回可用的酒馆战棋卡牌。");

        return await ApplyCardsAsync(catalogDirectory, cards, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CatalogUpdateResult> ApplyCardsAsync(string catalogDirectory, IReadOnlyList<BlizzardCard> cards, CancellationToken cancellationToken)
    {
        var fingerprint = CreateFingerprint(cards);
        var version = $"blizzard-{fingerprint}";
        var updatedAt = DateTimeOffset.UtcNow;
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"bva-blizzard-catalog-{Guid.NewGuid():N}");
        var packageRoot = Path.Combine(temporaryRoot, "package");
        var packagePath = Path.Combine(temporaryRoot, "catalog.zip");

        try
        {
            Directory.CreateDirectory(Path.Combine(packageRoot, "cards"));
            var catalogDatabase = Path.Combine(packageRoot, "catalog.db");
            var catalog = new CardCatalog(catalogDatabase);
            catalog.Initialize();

            var packageCards = new List<CatalogPackageCard>(cards.Count);
            foreach (var card in cards)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var imagePath = $"cards/{card.Id.ToString(CultureInfo.InvariantCulture)}.png";
                var localImagePath = Path.Combine(packageRoot, imagePath.Replace('/', Path.DirectorySeparatorChar));
                await DownloadImageAsync(card.ImageUrl!, localImagePath, cancellationToken).ConfigureAwait(false);
                packageCards.Add(new CatalogPackageCard(
                    card.Id.ToString(CultureInfo.InvariantCulture),
                    card.Name!,
                    card.Tier,
                    imagePath,
                    card.Kind));
            }

            InsertCards(catalogDatabase, packageCards);
            catalog.SetMetadata(version, updatedAt);
            var manifest = new CatalogPackageManifest(version, updatedAt, packageCards);
            await WriteManifestAsync(Path.Combine(packageRoot, CatalogPackageManifest.FileName), manifest, cancellationToken)
                .ConfigureAwait(false);
            ZipFile.CreateFromDirectory(packageRoot, packagePath, CompressionLevel.Fastest, includeBaseDirectory: false);

            return await _catalogUpdater.UpdateFromPackageAsync(catalogDirectory, packagePath, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(temporaryRoot);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private async Task<string> GetAccessTokenAsync(
        OfficialCatalogSyncOptions options,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.AccessToken))
            return options.AccessToken.Trim();

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth.battle.net/token")
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("grant_type", "client_credentials")])
        };
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.ClientId}:{options.ClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"暴雪 OAuth 获取令牌失败：HTTP {(int)response.StatusCode}。");

        var token = await response.Content.ReadFromJsonAsync<OAuthToken>(JsonOptions, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token?.AccessToken))
            throw new InvalidDataException("暴雪 OAuth 响应中没有 access_token。");
        return token.AccessToken;
    }

    private async Task<IReadOnlyList<BlizzardCard>> FetchBattlegroundCardsAsync(
        OfficialCatalogSyncOptions options,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var cards = new Dictionary<int, BlizzardCard>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var builder = new UriBuilder($"https://{options.Region}.api.blizzard.com/hearthstone/cards")
            {
                Query = $"gameMode=battlegrounds&tier=all&pageSize={PageSize}&page={page}&locale={Uri.EscapeDataString(options.Locale)}"
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"暴雪酒馆战棋卡库请求失败：HTTP {(int)response.StatusCode}。");

            var result = await response.Content.ReadFromJsonAsync<BlizzardCardsPage>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (result?.Cards is null || result.Cards.Count == 0)
                break;

            foreach (var card in result.Cards)
            {
                if (card.Id > 0 && !cards.ContainsKey(card.Id))
                    cards.Add(card.Id, card);
            }

            if (result.PageCount > 0 && page >= result.PageCount)
                break;
            if (result.PageCount == 0 && result.Cards.Count < PageSize)
                break;
        }

        return cards.Values
            .Where(card => !string.IsNullOrWhiteSpace(card.Name)
                && card.Battlegrounds is { Hero: false, Tier: > 0 }
                && ResolveGlobalKind(EffectiveCardTypeId(card)) is (CardKind.Minion or CardKind.Spell)
                && !string.IsNullOrWhiteSpace(card.Battlegrounds.Image ?? card.Image))
            .Select(card => card with
            {
                Tier = card.Battlegrounds!.Tier,
                ImageUrl = card.Battlegrounds.Image ?? card.Image,
                Kind = ResolveGlobalKind(EffectiveCardTypeId(card))
            })
            .OrderBy(card => card.Id)
            .ToArray();
    }

    private async Task DownloadImageAsync(string imageUrl, string destination, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException($"暴雪卡图地址不是 HTTPS：{imageUrl}");

        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"下载暴雪卡图失败：HTTP {(int)response.StatusCode}。");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, useAsync: true);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static void InsertCards(string databasePath, IReadOnlyList<CatalogPackageCard> cards)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var card in cards)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO cards(card_id, name_zh_cn, tier, image_path, kind) VALUES($id, $name, $tier, $image, $kind)";
            command.Parameters.AddWithValue("$id", card.CardId);
            command.Parameters.AddWithValue("$name", card.NameZhCn);
            command.Parameters.AddWithValue("$tier", card.Tier);
            command.Parameters.AddWithValue("$image", card.ImagePath);
            command.Parameters.AddWithValue("$kind", card.Kind.ToString());
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static async Task WriteManifestAsync(
        string path,
        CatalogPackageManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static string CreateFingerprint(IReadOnlyList<BlizzardCard> cards)
    {
        var canonical = string.Join('\n', cards.OrderBy(card => card.Id).Select(card => string.Join('|',
            card.Id.ToString(CultureInfo.InvariantCulture),
            card.Name,
            card.Tier.ToString(CultureInfo.InvariantCulture),
            card.ImageUrl,
            card.Kind.ToString())));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..16];
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record OAuthToken([property: JsonPropertyName("access_token")] string? AccessToken);

    private sealed record BlizzardCardsPage(
        [property: JsonPropertyName("cards")] List<BlizzardCard>? Cards,
        [property: JsonPropertyName("pageCount")] int PageCount);

    private sealed record BlizzardCard(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("image")] string? Image,
        [property: JsonPropertyName("battlegrounds")] BlizzardBattlegroundInfo? Battlegrounds,
        int Tier = 0,
        string? ImageUrl = null,
        [property: JsonPropertyName("card_type_id")] int CardTypeId = 4,
        [property: JsonPropertyName("cardTypeId")] int? CardTypeIdCamel = null,
        CardKind Kind = CardKind.Minion);

    private sealed record BlizzardBattlegroundInfo(
        [property: JsonPropertyName("tier")] int Tier,
        [property: JsonPropertyName("hero")] bool Hero,
        [property: JsonPropertyName("image")] string? Image);

    private static CardKind ResolveChinaKind(int cardTypeId) => cardTypeId switch
    {
        4 => CardKind.Minion,
        42 => CardKind.Spell,
        _ => CardKind.Unknown
    };

    private static CardKind ResolveGlobalKind(int cardTypeId) => cardTypeId switch
    {
        42 => CardKind.Spell,
        4 or 0 => CardKind.Minion,
        _ => CardKind.Unknown
    };

    private static int EffectiveCardTypeId(BlizzardCard card) => card.CardTypeIdCamel ?? card.CardTypeId;
}

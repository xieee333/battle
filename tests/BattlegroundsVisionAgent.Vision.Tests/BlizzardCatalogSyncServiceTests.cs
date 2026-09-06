using System.Net;
using System.Text.Json;
using BattlegroundsVisionAgent.Vision.Catalog;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class BlizzardCatalogSyncServiceTests
{
    [Fact]
    public async Task SyncBattlegroundsAsync_UsesOAuthDownloadsCardsAndSkipsSameFingerprint()
    {
        var root = Path.Combine(Path.GetTempPath(), $"blizzard-sync-{Guid.NewGuid():N}");
        var imageBytes = CreateImageBytes();
        using var httpClient = new HttpClient(new BlizzardApiHandler(imageBytes));
        using var service = new BlizzardCatalogSyncService(httpClient);
        var options = new OfficialCatalogSyncOptions(ClientId: "client-id", ClientSecret: "client-secret");
        try
        {
            var first = await service.SyncBattlegroundsAsync(Path.Combine(root, "catalog"), options);
            var second = await service.SyncBattlegroundsAsync(Path.Combine(root, "catalog"), options);
            var snapshot = new CardCatalog(Path.Combine(root, "catalog", "catalog.db")).ReadSnapshot();

            Assert.True(first.Applied);
            Assert.False(second.Applied);
            Assert.StartsWith("blizzard-", snapshot.Metadata?.Version);
            Assert.Equal(["1001", "1002"], snapshot.Entries.Select(card => card.CardId));
            Assert.Equal("测试随从", snapshot.Entries[0].NameZhCn);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OptionsFromEnvironment_UsesAccessTokenWithoutRequiringClientSecret()
    {
        const string token = "temporary-token";
        var originalToken = Environment.GetEnvironmentVariable(OfficialCatalogSyncOptions.AccessTokenEnvironmentVariable);
        var originalClientId = Environment.GetEnvironmentVariable(OfficialCatalogSyncOptions.ClientIdEnvironmentVariable);
        var originalClientSecret = Environment.GetEnvironmentVariable(OfficialCatalogSyncOptions.ClientSecretEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(OfficialCatalogSyncOptions.AccessTokenEnvironmentVariable, token);
            Environment.SetEnvironmentVariable(OfficialCatalogSyncOptions.ClientIdEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(OfficialCatalogSyncOptions.ClientSecretEnvironmentVariable, null);

            var options = OfficialCatalogSyncOptions.FromEnvironment();

            Assert.Equal(token, options.AccessToken);
            options.Validate();
        }
        finally
        {
            Environment.SetEnvironmentVariable(OfficialCatalogSyncOptions.AccessTokenEnvironmentVariable, originalToken);
            Environment.SetEnvironmentVariable(OfficialCatalogSyncOptions.ClientIdEnvironmentVariable, originalClientId);
            Environment.SetEnvironmentVariable(OfficialCatalogSyncOptions.ClientSecretEnvironmentVariable, originalClientSecret);
        }
    }

    private static byte[] CreateImageBytes()
    {
        using var image = new Mat(48, 48, MatType.CV_8UC1);
        Cv2.Randu(image, Scalar.All(0), Scalar.All(255));
        Cv2.ImEncode(".png", image, out var encoded);
        return encoded.ToArray();
    }

    private sealed class BlizzardApiHandler(byte[] imageBytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.Host == "oauth.battle.net")
            {
                return Task.FromResult(JsonResponse(new { access_token = "api-token" }));
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/hearthstone/cards")
            {
                var payload = new
                {
                    pageCount = 1,
                    cards = new[]
                    {
                        new
                        {
                            id = 1001,
                            name = "测试随从",
                            image = "https://cdn.example/normal.png",
                            battlegrounds = new { tier = 2, hero = false, image = "https://cdn.example/bg-1001.png" }
                        },
                        new
                        {
                            id = 1002,
                            name = "测试随从二",
                            image = "https://cdn.example/normal-2.png",
                            battlegrounds = new { tier = 3, hero = false, image = "https://cdn.example/bg-1002.png" }
                        }
                    }
                };
                return Task.FromResult(JsonResponse(payload));
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.Host == "cdn.example")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(imageBytes)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json")
        };
    }
}

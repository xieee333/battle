using System.Net;
using System.Text;
using BattlegroundsVisionAgent.Vision.Catalog;

namespace BattlegroundsVisionAgent.Vision.Tests;

public sealed class ChinaCatalogTests
{
    [Theory]
    [InlineData("{\"code\":20002,\"data\":null}")]
    [InlineData("{\"code\":0,\"data\":{\"total\":274,\"list\":[]}}")]
    public async Task RejectsErrorsAndIncompleteListsBeforeReplacingCatalog(string response)
    {
        using var client = new HttpClient(new Handler(response));
        using var service = new BlizzardCatalogSyncService(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SyncChinaAsync("unused-catalog"));
    }

    private sealed class Handler(string response) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(BlizzardCatalogSyncService.ChinaEndpoint, request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Null(request.Headers.Authorization);
            Assert.Contains("\"bg_card_type\":\"\"", await request.Content!.ReadAsStringAsync(token));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}

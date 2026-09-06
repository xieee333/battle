using BattlegroundsVisionAgent.Core.Configuration;
using BattlegroundsVisionAgent.Core.Domain;

namespace BattlegroundsVisionAgent.Core.Tests;

public sealed class SettingsRepositoryTests
{
    [Fact]
    public async Task SaveThenLoad_PreservesRuleAndSafetyDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bva-{Guid.NewGuid():N}.json");
        var repository = new SettingsRepository(path);
        var settings = AppSettings.CreateDefault() with
        {
            ReservedHandSlots = 2,
            MinimumGold = 2,
            Rules = [new CardRule("CARD_001", PurchaseLimit.Unlimited, CardDisposition.Keep,
                CardDisposition.PlayThenSell, 1, true)]
        };

        await repository.SaveAsync(settings, CancellationToken.None);
        var loaded = await repository.LoadAsync(CancellationToken.None);

        Assert.Equal(settings, loaded);
        File.Delete(path);
    }
}

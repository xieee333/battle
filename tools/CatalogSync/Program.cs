using BattlegroundsVisionAgent.Vision.Catalog;

if (args.Length != 1) throw new ArgumentException("Usage: CatalogSync <catalog directory>");
using var service = new BlizzardCatalogSyncService();
var result = await service.SyncChinaAsync(Path.GetFullPath(args[0]));
Console.WriteLine($"Source: https://hs.blizzard.cn/battlegrounds/; Cards: {result.CardCount}; Version: {result.Version}; Applied: {result.Applied}");

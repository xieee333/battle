using BattlegroundsVisionAgent.App.ViewModels;
using BattlegroundsVisionAgent.Vision.Catalog;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var application = new BattlegroundsVisionAgent.App.App();
        application.InitializeComponent();
        var preview = new BattlegroundsVisionAgent.App.Views.CapturePreviewWindow();
        preview.Close();
        Console.WriteLine("PASS: Application resources and capture preview XAML load");
        var vm = new MainViewModel(cardCatalog: new CardCatalog(args[0]));
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            Console.WriteLine("PASS: " + message);
        }
        Check(vm.CatalogCards.Count > 0, "Live catalog loaded");
        Check(vm.CatalogCards.All(c => c.CardImage is not null), "All local thumbnails decoded");
        Check(vm.CatalogCards.Select(c => c.Tier).SequenceEqual(vm.CatalogCards.Select(c => c.Tier).Order()), "Tier ascending order");
        var target = vm.CatalogCards[0];
        target.IsTargeted = true;
        foreach (var tier in vm.TierFilters.Where(t => t > 0).ToArray())
        {
            vm.SelectedTier = tier;
            Check(vm.FilteredCatalogCards.Cast<CardRuleEditorViewModel>().All(c => c.Tier == tier), $"Tier {tier} filter");
            Check(vm.FilteredCatalogCards.Cast<object>().Count() == vm.CatalogCards.Count(c => c.Tier == tier), "Filter count");
        }
        vm.SelectedTier = target.Tier;
        vm.CardSearchText = target.CardId;
        Check(vm.FilteredCatalogCards.Cast<CardRuleEditorViewModel>().Contains(target), "Search combines with tier");
        vm.CardSearchText = "__no_match__";
        Check(!vm.FilteredCatalogCards.Cast<object>().Any(), "Empty search result");
        Check(target.IsTargeted && vm.Settings.Rules.Any(r => r.CardId == target.CardId), "Hidden target rule preserved");
        vm.CardSearchText = "";
        vm.SelectedTier = 0;
        Check(vm.FilteredCatalogCards.Cast<object>().Count() == vm.CatalogCards.Count, "All tiers restored");
        vm.ToggleTierSortCommand.Execute(null);
        var descending = vm.FilteredCatalogCards.Cast<CardRuleEditorViewModel>().Select(c => c.Tier).ToArray();
        Check(descending.SequenceEqual(descending.OrderDescending()), "Toggle sorts tiers descending");
        Check(vm.TierSortText == "本数 ↓", "Descending arrow label");
        vm.ToggleTierSortCommand.Execute(null);
        var ascending = vm.FilteredCatalogCards.Cast<CardRuleEditorViewModel>().Select(c => c.Tier).ToArray();
        Check(ascending.SequenceEqual(ascending.Order()), "Toggle restores ascending order");
        var missing = new CardRuleEditorViewModel(new("missing", "missing", 1, "absent.png"), null, _ => {}, System.IO.Path.GetDirectoryName(args[0]));
        Check(missing.CardImage is null, "Missing image fallback");
    }
}

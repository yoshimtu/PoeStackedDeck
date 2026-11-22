using System.Reflection;
using System.Text.Json.Nodes;
using StackedDeckTracker.Models;
using StackedDeckTracker.Services;
using StackedDeckTracker.UI;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static void Main()
    {
        var now = DateTimeOffset.Now;
        var market = new PriceSnapshot
        {
            League = "Standard", UpdatedAt = now, DivineChaosPrice = 200, StackedDeckChaosPrice = 3,
            CardPrices = new Dictionary<string, double> { ["The Doctor"] = 100, ["The Fortunate"] = 10 }
        };
        var settings = new TrackerSettings
        {
            CustomCardPrices = new(StringComparer.OrdinalIgnoreCase)
            {
                ["the doctor"] = 250.25, ["The Fortunate"] = 0, ["New Card"] = 20
            }
        };
        var effective = PriceSnapshot.WithCustomCardPrices(market, settings)!;
        Check(effective.GetCardPrice("The Doctor") == 250.25, "Case-insensitive override");
        Check(effective.GetCardPrice("The Fortunate") == 0, "Zero is a valid override");
        Check(effective.GetCardPrice("New Card") == 20, "Custom-only card");
        Check(market.GetCardPrice("The Doctor") == 100, "Market snapshot remains unchanged");
        Check(effective.ToDivines(400) == 2 && effective.StackedDeckChaosPrice == 3, "Exchange rate and deck price preserved");
        Check(PriceSnapshot.WithCustomCardPrices(market, new TrackerSettings())!.GetCardPrice("The Doctor") == 100,
            "Removing overrides restores market prices");
        var offline = PriceSnapshot.WithCustomCardPrices(null, settings)!;
        Check(offline.GetCardPrice("The Doctor") == 250.25 && offline.ToDivines(250.25) == 0,
            "Offline overrides without invented exchange rate");
        Check(PriceSnapshot.WithCustomCardPrices(null, new TrackerSettings()) == null, "No prices remains null");

        using var host = new FakeHost();
        var store = new SessionStore(host);
        Check(store.LoadSettings().CustomCardPrices.Count == 0, "Legacy settings default to no overrides");
        host.Settings["unrelated"] = "preserved";
        store.SaveSettings(settings);
        var loaded = store.LoadSettings();
        Check(loaded.CustomCardPrices["THE DOCTOR"] == 250.25 && loaded.CustomCardPrices["The Fortunate"] == 0,
            "Settings JSON round trip preserves zero, decimals and case-insensitive lookup");
        Check(host.Settings["unrelated"]!.GetValue<string>() == "preserved", "Unrelated settings preserved");
        host.Settings["customCardPrices"] = JsonNode.Parse("{\"\":12,\"Bad\":-2,\"Wrong\":\"abc\",\"Null\":null,\" Valid \":1.5}");
        loaded = store.LoadSettings();
        Check(loaded.CustomCardPrices.Count == 1 && loaded.CustomCardPrices["valid"] == 1.5,
            "Malformed custom entries ignored and names trimmed");

        var tracker = new DeckSessionTracker();
        tracker.SetEnabled(true, now);
        GameSnapshot Snapshot(int decks, bool card) => new("Standard", false,
            card ? [new("Stacked Deck", decks, "Currency"), new("The Doctor", 1, "Divination")]
                 : [new("Stacked Deck", decks, "Currency")], []);
        tracker.ProcessSnapshot(Snapshot(2, false), effective, now, out _);
        tracker.ProcessSnapshot(Snapshot(1, true), effective, now.AddSeconds(1), out var drops);
        Check(drops.Count == 1 && drops[0].UnitChaosAtPull == 250.25, "Recorded drop uses override");
        var metrics = tracker.ComputeMetrics(effective, settings, now.AddSeconds(2));
        Check(metrics.CountedValueChaos == 250.25 && metrics.NetProfitChaos == 247.25, "Session values and profit use override");
        Check(DropAlertService.Classify(drops[0].UnitChaosAtPull, settings) == DropAlertTier.High,
            "Alert tier uses custom value");
        settings.MinCardValueChaos = 300;
        Check(tracker.ComputeMetrics(effective, settings, now).CountedValueChaos == 0, "Minimum-value filter uses override");
        settings.MinCardValueChaos = 0;
        Check(tracker.ComputeOdds(effective, settings).TotalEvPerDeckChaos == 250.25, "Observed EV uses override");
        var history = tracker.CreateHistoryEntry("Standard", effective, settings, now);
        Check(history.NetProfitChaos == 247.25, "Archived totals capture effective value");
        var refreshed = new PriceSnapshot { CardPrices = new Dictionary<string, double> { ["The Doctor"] = 150 } };
        Check(PriceSnapshot.WithCustomCardPrices(refreshed, settings)!.GetCardPrice("The Doctor") == 250.25,
            "Market refresh retains override");
        settings.CustomCardPrices.Clear();
        Check(tracker.ComputeMetrics(PriceSnapshot.WithCustomCardPrices(market, settings), settings, now).CountedValueChaos == 100,
            "Removing override recalculates existing pulls");
        Check(history.NetProfitChaos == 247.25, "Archived totals stay fixed");

        ApplicationConfiguration.Initialize();
        using var view = new TrackerView(new TrackerSettings(), "test hotkey");
        using var form = new Form { Width = 1200, Height = 900 };
        form.Controls.Add(view);
        // Create controls without showing a window or starting game polling.
        form.CreateControl();
        InvokeClick(Descendants(view).OfType<Button>().Single(b => b.Text == "Settings"));
        var editor = Descendants(view).OfType<CustomCardPricesEditor>().Single();
        var name = Descendants(editor).OfType<ComboBox>().Single();
        var price = Descendants(editor).OfType<NumericUpDown>().Single();
        var grid = Descendants(editor).OfType<DataGridView>().Single();
        TrackerSettings? saved = null;
        view.SettingsSaved += s => saved = s;
        void Button(string text) => InvokeClick(Descendants(view).OfType<Button>().Single(b => b.Text == text));
        Button("Add / Update");
        Check(editor.ExportPrices().Count == 0, "Empty name rejected in editor");
        name.Text = " The Doctor "; price.Value = 123.45m; Button("Add / Update");
        Check(editor.ExportPrices()["the doctor"] == 123.45 && saved == null, "Editor trims names and stages changes");
        Button("Save Settings");
        Check(saved!.CustomCardPrices["THE DOCTOR"] == 123.45, "Save Settings exports custom values");
        name.Text = "the doctor"; price.Value = 0; Button("Add / Update");
        Check(editor.ExportPrices().Count == 1 && editor.ExportPrices()["The Doctor"] == 0, "Editor updates existing card to zero");
        grid.CurrentCell = grid.Rows[0].Cells[0]; Button("Remove Selected"); Button("Save Settings");
        Check(saved!.CustomCardPrices.Count == 0, "Editor removes and saves override");
        Check(editor.Height >= 300 && editor.Controls.OfType<DataGridView>().Single().Height > 50,
            "Settings editor has room for price rows");
        Console.WriteLine($"Passed {_checks} checks.");
    }

    private static void Check(bool success, string description)
    {
        if (!success) throw new InvalidOperationException(description);
        _checks++;
        Console.WriteLine($"PASS: {description}");
    }

    private static IEnumerable<Control> Descendants(Control parent) =>
        parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));

    private static void InvokeClick(Button button) => typeof(Control)
        .GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(button, [EventArgs.Empty]);

    private sealed class FakeHost : IPluginHostBridge
    {
        public JsonObject Settings = new();
        public string DataDirectory => Path.GetTempPath();
        public Action Show => () => { };
        public void Log(string message) { }
        public void Notify(string message) { }
        public JsonObject ReadSettings() => (JsonObject)Settings.DeepClone();
        public void SaveSettings(JsonObject settings) => Settings = (JsonObject)settings.DeepClone();
        public bool TryRegisterHotkey(Keys chord, Action action) => false;
        public void Dispose() { }
    }
}

namespace StackedDeckTracker.Models;

/// <summary>
/// Represents a point-in-time snapshot of poe.ninja prices for a specific league.
/// </summary>
public sealed class PriceSnapshot
{
    public string League { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }
    public double DivineChaosPrice { get; init; }
    public double StackedDeckChaosPrice { get; init; }
    public IReadOnlyDictionary<string, double> CardPrices { get; init; } =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    public double GetCardPrice(string cardName) =>
        CardPrices.TryGetValue(cardName, out var price) ? price : 0.0;

    public double ToDivines(double chaos) =>
        DivineChaosPrice > 0 ? chaos / DivineChaosPrice : 0.0;

    // Build a fresh snapshot so a market refresh or removed override never mutates
    // the original market prices, including while the poller reads a snapshot.
    public static PriceSnapshot? WithCustomCardPrices(PriceSnapshot? market, TrackerSettings settings)
    {
        if (settings.CustomCardPrices.Count == 0) return market;
        var cards = market == null
            ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, double>(market.CardPrices, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, price) in settings.CustomCardPrices)
        {
            if (!string.IsNullOrWhiteSpace(name) && double.IsFinite(price) && price >= 0)
                cards[name.Trim()] = price;
        }
        return new PriceSnapshot
        {
            League = market?.League ?? settings.League,
            UpdatedAt = market?.UpdatedAt ?? default,
            DivineChaosPrice = market?.DivineChaosPrice ?? 0,
            StackedDeckChaosPrice = market?.StackedDeckChaosPrice ?? 0,
            CardPrices = cards
        };
    }
}

/// <summary>
/// A single recorded divination card pull event.
/// </summary>
public sealed record DropEvent(
    DateTimeOffset Timestamp,
    string CardName,
    int Count,
    double UnitChaosAtPull);

/// <summary>
/// Aggregated row for a divination card pulled during the current session.
/// </summary>
public sealed record CardDropSummary(
    string CardName,
    int Count,
    double UnitChaos,
    double TotalChaos,
    double TotalDivines,
    bool IncludedInProfit,
    DateTimeOffset LastPulled);

/// <summary>
/// Snapshot of computed session metrics for UI display.
/// </summary>
public sealed record SessionMetrics(
    int DecksOpened,
    int TotalCardsPulled,
    double UnitDeckCostChaos,
    double TotalCostChaos,
    double TotalCostDivines,
    double GrossValueChaos,
    double CountedValueChaos,
    double CountedValueDivines,
    double NetProfitChaos,
    double NetProfitDivines,
    double RoiPercent,
    double AvgProfitPerDeckChaos,
    TimeSpan ActiveElapsed,
    double ProfitPerHourChaos,
    double ProfitPerHourDivines,
    double DecksPerHour,
    bool HasPrices,
    IReadOnlyList<CardDropSummary> Cards,
    IReadOnlyList<DropEvent> RecentDrops);

/// <summary>
/// Drop rate and expected-value statistics for a card across a sample of opened decks.
/// </summary>
public sealed record CardOddsSummary(
    string CardName,
    int Count,
    double DropRatePercent,
    double OneInDecks,
    double MarketChaos,
    double EvPerDeckChaos,
    double EffectiveCostChaos);

/// <summary>
/// Archived summary of a completed stacked deck session.
/// </summary>
public sealed class SessionHistoryEntry
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public string League { get; set; } = "";
    public int DecksOpened { get; set; }
    public int TotalCardsPulled { get; set; }
    public double UnitDeckCostChaos { get; set; }
    public double TotalCostChaos { get; set; }
    public double CountedValueChaos { get; set; }
    public double NetProfitChaos { get; set; }
    public double NetProfitDivines { get; set; }
    public double ActiveElapsedSeconds { get; set; }
    public string TopCardName { get; set; } = "";
    public double TopCardChaos { get; set; }
    public Dictionary<string, int> CardCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Minimal game state snapshot extracted from PluginHost.Games.Poe1.GameReader.
/// Decouples tracking logic from Win32/memory reading for easy testing.
/// </summary>
public sealed record GameSnapshot(
    string League,
    bool IsStashOpen,
    IReadOnlyList<ObservedItem> Inventory,
    IReadOnlyList<ObservedItem> Ground,
    IntPtr WindowHandle = default);

/// <summary>
/// Lightweight projection of a PoE item.
/// </summary>
public sealed record ObservedItem(string Name, int Count, string Kind);

/// <summary>
/// Current attachment status of the PoE game reader.
/// </summary>
public enum AttachmentKind
{
    WaitingForGame,
    Attached,
    UnsupportedBuild,
    Error
}

public sealed record GameAttachmentStatus(
    AttachmentKind Kind,
    string Message,
    string? League = null);

/// <summary>
/// User-configurable plugin settings persisted through PluginServices.
/// </summary>
public sealed class TrackerSettings
{
    public bool AutoDetectLeague { get; set; } = true;
    public string League { get; set; } = "Standard";
    public bool UseCustomDeckCost { get; set; } = false;
    public double CustomDeckCostChaos { get; set; } = 3.0;
    public Dictionary<string, double> CustomCardPrices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public double MinCardValueChaos { get; set; } = 0.0;
    public int PriceRefreshMinutes { get; set; } = 15;
    public int PollIntervalMs { get; set; } = 250;
    public bool AlertsEnabled { get; set; } = true;
    public double LowAlertChaos { get; set; } = 10.0;
    public double MidAlertChaos { get; set; } = 60.0;
    public double HighAlertChaos { get; set; } = 100.0;
}

/// <summary>
/// Serializable session state saved under DataDirectory so progress survives restarts.
/// </summary>
public sealed class PersistedSession
{
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public int DecksOpened { get; set; }
    public double ActiveElapsedSeconds { get; set; }
    public Dictionary<string, int> CardCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTimeOffset> CardLastPulled { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DropEvent> RecentDrops { get; set; } = new();
}

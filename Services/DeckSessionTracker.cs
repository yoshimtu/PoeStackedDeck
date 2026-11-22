using StackedDeckTracker.Models;

namespace StackedDeckTracker.Services;

/// <summary>
/// Tracks Stacked Deck openings, divination card pulls, active session time, and profit metrics.
/// Uses combined Inventory + Ground card counts so picking up or dropping cards never double-counts.
/// </summary>
public sealed class DeckSessionTracker
{
    private const string StackedDeckName = "Stacked Deck";
    private const int MaxUnconfirmedDecksPerTick = 10;
    private const int MaxRecentDrops = 200;
    private static readonly TimeSpan PendingDeckGraceWindow = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private readonly Dictionary<string, int> _cardCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _cardLastPulled = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DropEvent> _recentDrops = new();

    private bool _isEnabled;
    private DateTimeOffset _sessionStartedAt = DateTimeOffset.Now;
    private int _decksOpened;
    private TimeSpan _accumulatedElapsed = TimeSpan.Zero;
    private DateTimeOffset? _activeSegmentStart;

    // Frame-to-frame diffing state
    private bool _hasBaseline;
    private int _prevDeckCount;
    private Dictionary<string, int> _prevCombinedCards = new(StringComparer.OrdinalIgnoreCase);
    private int _pendingDecks;
    private DateTimeOffset _pendingExpiresAt;

    public bool IsEnabled
    {
        get { lock (_sync) return _isEnabled; }
    }

    public void SetEnabled(bool enabled, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_isEnabled == enabled) return;

            if (enabled)
            {
                _isEnabled = true;
                _activeSegmentStart = now;
                ResetBaselineLocked();
            }
            else
            {
                if (_activeSegmentStart.HasValue)
                {
                    _accumulatedElapsed += ClampPositive(now - _activeSegmentStart.Value);
                    _activeSegmentStart = null;
                }
                _isEnabled = false;
                ResetBaselineLocked();
            }
        }
    }

    /// <summary>
    /// Clears the diffing baseline (e.g. after game reattach or zone/league switch) without resetting session totals.
    /// </summary>
    public void ResetBaseline()
    {
        lock (_sync) ResetBaselineLocked();
    }

    public void ResetSession(DateTimeOffset now)
    {
        lock (_sync)
        {
            _sessionStartedAt = now;
            _decksOpened = 0;
            _accumulatedElapsed = TimeSpan.Zero;
            _activeSegmentStart = _isEnabled ? now : null;
            _cardCounts.Clear();
            _cardLastPulled.Clear();
            _recentDrops.Clear();
            ResetBaselineLocked();
        }
    }

    public void LoadPersisted(PersistedSession session)
    {
        lock (_sync)
        {
            _sessionStartedAt = session.StartedAt == default ? DateTimeOffset.Now : session.StartedAt;
            _decksOpened = Math.Max(0, session.DecksOpened);
            _accumulatedElapsed = TimeSpan.FromSeconds(Math.Max(0, session.ActiveElapsedSeconds));
            _cardCounts.Clear();
            foreach (var kv in session.CardCounts)
            {
                if (kv.Value > 0) _cardCounts[kv.Key] = kv.Value;
            }
            _cardLastPulled.Clear();
            foreach (var kv in session.CardLastPulled)
            {
                _cardLastPulled[kv.Key] = kv.Value;
            }
            _recentDrops.Clear();
            _recentDrops.AddRange(session.RecentDrops.Take(MaxRecentDrops));
            ResetBaselineLocked();
        }
    }

    public PersistedSession ExportPersisted(DateTimeOffset now)
    {
        lock (_sync)
        {
            return new PersistedSession
            {
                StartedAt = _sessionStartedAt,
                DecksOpened = _decksOpened,
                ActiveElapsedSeconds = GetActiveElapsedLocked(now).TotalSeconds,
                CardCounts = new Dictionary<string, int>(_cardCounts, StringComparer.OrdinalIgnoreCase),
                CardLastPulled = new Dictionary<string, DateTimeOffset>(_cardLastPulled, StringComparer.OrdinalIgnoreCase),
                RecentDrops = new List<DropEvent>(_recentDrops)
            };
        }
    }

    public SessionHistoryEntry CreateHistoryEntry(string league, PriceSnapshot? prices, TrackerSettings settings, DateTimeOffset now)
    {
        var metrics = ComputeMetrics(prices, settings, now);
        var top = metrics.Cards.OrderByDescending(c => c.UnitChaos).FirstOrDefault();
        lock (_sync)
        {
            return new SessionHistoryEntry
            {
                StartedAt = _sessionStartedAt,
                EndedAt = now,
                League = league,
                DecksOpened = metrics.DecksOpened,
                TotalCardsPulled = metrics.TotalCardsPulled,
                UnitDeckCostChaos = metrics.UnitDeckCostChaos,
                TotalCostChaos = metrics.TotalCostChaos,
                CountedValueChaos = metrics.CountedValueChaos,
                NetProfitChaos = metrics.NetProfitChaos,
                NetProfitDivines = metrics.NetProfitDivines,
                ActiveElapsedSeconds = metrics.ActiveElapsed.TotalSeconds,
                TopCardName = top?.CardName ?? "",
                TopCardChaos = top?.UnitChaos ?? 0.0,
                CardCounts = new Dictionary<string, int>(_cardCounts, StringComparer.OrdinalIgnoreCase)
            };
        }
    }

    /// <summary>
    /// Computes observed drop rates, 1-in-N odds, EV contribution per deck, and effective
    /// stacked-deck cost per card across the current session (and optionally all saved history).
    /// </summary>
    public (int TotalDecks, double TotalEvPerDeckChaos, double UnitDeckCostChaos, IReadOnlyList<CardOddsSummary> Rows)
        ComputeOdds(PriceSnapshot? prices, TrackerSettings settings, IReadOnlyList<SessionHistoryEntry>? history = null)
    {
        var unitDeckCost = settings.UseCustomDeckCost
            ? Math.Max(0, settings.CustomDeckCostChaos)
            : (prices?.StackedDeckChaosPrice ?? 0.0);

        int totalDecks;
        var combined = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        lock (_sync)
        {
            totalDecks = _decksOpened;
            foreach (var (name, qty) in _cardCounts)
            {
                if (qty > 0) combined[name] = qty;
            }
        }

        if (history != null)
        {
            foreach (var entry in history)
            {
                totalDecks += Math.Max(0, entry.DecksOpened);
                foreach (var (name, qty) in entry.CardCounts)
                {
                    if (qty > 0) combined[name] = combined.GetValueOrDefault(name, 0) + qty;
                }
            }
        }

        var rows = new List<CardOddsSummary>(combined.Count);
        double totalEv = 0;

        if (totalDecks > 0)
        {
            foreach (var (name, count) in combined)
            {
                double rate = (double)count / totalDecks;
                double dropRatePct = rate * 100.0;
                double oneIn = (double)totalDecks / count;
                double market = prices?.GetCardPrice(name) ?? 0.0;
                double evPerDeck = rate * market;
                double effectiveCost = oneIn * unitDeckCost;

                totalEv += evPerDeck;
                rows.Add(new CardOddsSummary(name, count, dropRatePct, oneIn, market, evPerDeck, effectiveCost));
            }
        }

        rows.Sort((a, b) =>
        {
            var cmp = b.EvPerDeckChaos.CompareTo(a.EvPerDeckChaos);
            return cmp != 0 ? cmp : b.Count.CompareTo(a.Count);
        });

        return (totalDecks, totalEv, unitDeckCost, rows);
    }

    /// <summary>
    /// Processes a new game state snapshot and returns true if any decks or cards were recorded.
    /// </summary>
    public bool ProcessSnapshot(GameSnapshot snapshot, PriceSnapshot? prices, DateTimeOffset now, out IReadOnlyList<DropEvent> newDrops)
    {
        lock (_sync)
        {
            newDrops = Array.Empty<DropEvent>();
            if (!_isEnabled) return false;

            var currDeckCount = CountStackedDecks(snapshot.Inventory);
            var currCombinedCards = CountCombinedDivinationCards(snapshot.Inventory, snapshot.Ground, prices);

            if (!_hasBaseline)
            {
                _prevDeckCount = currDeckCount;
                _prevCombinedCards = currCombinedCards;
                _hasBaseline = true;
                return false;
            }

            if (_pendingDecks > 0 && now > _pendingExpiresAt)
            {
                _pendingDecks = 0;
            }

            var deckDrop = _prevDeckCount - currDeckCount;
            var cardGains = ComputeCardGains(_prevCombinedCards, currCombinedCards);
            var totalCardGain = cardGains.Sum(g => g.Gain);

            bool changed = false;

            if (deckDrop > 0)
            {
                // Ignore bulk stack transfers into stash; allow normal opening or card-confirmed drops.
                int opened = 0;
                if (!snapshot.IsStashOpen && deckDrop <= MaxUnconfirmedDecksPerTick)
                {
                    opened = deckDrop;
                }
                else if (totalCardGain > 0)
                {
                    opened = Math.Min(deckDrop, totalCardGain);
                }

                if (opened > 0)
                {
                    _decksOpened += opened;
                    _pendingDecks += opened;
                    _pendingExpiresAt = now + PendingDeckGraceWindow;
                    changed = true;
                }
            }

            if (_pendingDecks > 0 && cardGains.Count > 0)
            {
                var recorded = new List<DropEvent>();
                foreach (var (cardName, gain) in cardGains)
                {
                    if (_pendingDecks <= 0) break;
                    var accepted = Math.Min(gain, _pendingDecks);
                    if (accepted <= 0) continue;

                    _cardCounts[cardName] = _cardCounts.GetValueOrDefault(cardName, 0) + accepted;
                    _cardLastPulled[cardName] = now;
                    _pendingDecks -= accepted;

                    var unitChaos = prices?.GetCardPrice(cardName) ?? 0.0;
                    var ev = new DropEvent(now, cardName, accepted, unitChaos);
                    _recentDrops.Insert(0, ev);
                    recorded.Add(ev);
                    changed = true;
                }

                if (_recentDrops.Count > MaxRecentDrops)
                {
                    _recentDrops.RemoveRange(MaxRecentDrops, _recentDrops.Count - MaxRecentDrops);
                }

                if (recorded.Count > 0)
                {
                    newDrops = recorded;
                }
            }

            _prevDeckCount = currDeckCount;
            _prevCombinedCards = currCombinedCards;
            return changed;
        }
    }

    public SessionMetrics ComputeMetrics(PriceSnapshot? prices, TrackerSettings settings, DateTimeOffset now)
    {
        lock (_sync)
        {
            var hasPrices = prices is { DivineChaosPrice: > 0 };
            var unitDeckCost = settings.UseCustomDeckCost
                ? Math.Max(0, settings.CustomDeckCostChaos)
                : (prices?.StackedDeckChaosPrice ?? 0.0);

            var totalCostChaos = _decksOpened * unitDeckCost;
            var totalCostDivs = hasPrices ? prices!.ToDivines(totalCostChaos) : 0.0;

            double grossChaos = 0;
            double countedChaos = 0;
            int totalCards = 0;

            var cardSummaries = new List<CardDropSummary>(_cardCounts.Count);
            foreach (var (name, count) in _cardCounts)
            {
                totalCards += count;
                var unitChaos = prices?.GetCardPrice(name) ?? 0.0;
                var lineChaos = unitChaos * count;
                var lineDivs = hasPrices ? prices!.ToDivines(lineChaos) : 0.0;
                var included = unitChaos >= settings.MinCardValueChaos;

                grossChaos += lineChaos;
                if (included) countedChaos += lineChaos;

                var lastPulled = _cardLastPulled.GetValueOrDefault(name, now);
                cardSummaries.Add(new CardDropSummary(name, count, unitChaos, lineChaos, lineDivs, included, lastPulled));
            }

            cardSummaries.Sort((a, b) =>
            {
                var cmp = b.TotalChaos.CompareTo(a.TotalChaos);
                return cmp != 0 ? cmp : string.Compare(a.CardName, b.CardName, StringComparison.OrdinalIgnoreCase);
            });

            var countedDivs = hasPrices ? prices!.ToDivines(countedChaos) : 0.0;
            var netProfitChaos = countedChaos - totalCostChaos;
            var netProfitDivs = hasPrices ? prices!.ToDivines(netProfitChaos) : 0.0;
            var roi = totalCostChaos > 0 ? (netProfitChaos / totalCostChaos) * 100.0 : 0.0;
            var avgPerDeck = _decksOpened > 0 ? netProfitChaos / _decksOpened : 0.0;

            var elapsed = GetActiveElapsedLocked(now);
            var hours = elapsed.TotalSeconds >= 1.0 ? elapsed.TotalHours : 0.0;
            var profitPerHrChaos = hours > 0 ? netProfitChaos / hours : 0.0;
            var profitPerHrDivs = hasPrices && hours > 0 ? prices!.ToDivines(profitPerHrChaos) : 0.0;
            var decksPerHr = hours > 0 ? _decksOpened / hours : 0.0;

            return new SessionMetrics(
                DecksOpened: _decksOpened,
                TotalCardsPulled: totalCards,
                UnitDeckCostChaos: unitDeckCost,
                TotalCostChaos: totalCostChaos,
                TotalCostDivines: totalCostDivs,
                GrossValueChaos: grossChaos,
                CountedValueChaos: countedChaos,
                CountedValueDivines: countedDivs,
                NetProfitChaos: netProfitChaos,
                NetProfitDivines: netProfitDivs,
                RoiPercent: roi,
                AvgProfitPerDeckChaos: avgPerDeck,
                ActiveElapsed: elapsed,
                ProfitPerHourChaos: profitPerHrChaos,
                ProfitPerHourDivines: profitPerHrDivs,
                DecksPerHour: decksPerHr,
                HasPrices: hasPrices,
                Cards: cardSummaries,
                RecentDrops: _recentDrops.ToList());
        }
    }

    private void ResetBaselineLocked()
    {
        _hasBaseline = false;
        _prevDeckCount = 0;
        _prevCombinedCards.Clear();
        _pendingDecks = 0;
        _pendingExpiresAt = DateTimeOffset.MinValue;
    }

    private TimeSpan GetActiveElapsedLocked(DateTimeOffset now)
    {
        var total = _accumulatedElapsed;
        if (_isEnabled && _activeSegmentStart.HasValue)
        {
            total += ClampPositive(now - _activeSegmentStart.Value);
        }
        return total;
    }

    private static TimeSpan ClampPositive(TimeSpan ts) => ts > TimeSpan.Zero ? ts : TimeSpan.Zero;

    private static int CountStackedDecks(IReadOnlyList<ObservedItem> inventory)
    {
        int total = 0;
        foreach (var item in inventory)
        {
            if (string.Equals(item.Name, StackedDeckName, StringComparison.OrdinalIgnoreCase))
            {
                total += Math.Max(1, item.Count);
            }
        }
        return total;
    }

    private static Dictionary<string, int> CountCombinedDivinationCards(
        IReadOnlyList<ObservedItem> inventory,
        IReadOnlyList<ObservedItem> ground,
        PriceSnapshot? prices)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        AccumulateDivinationCards(counts, inventory, prices);
        AccumulateDivinationCards(counts, ground, prices);
        return counts;
    }

    private static void AccumulateDivinationCards(
        Dictionary<string, int> counts,
        IReadOnlyList<ObservedItem> items,
        PriceSnapshot? prices)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Name)) continue;
            if (!IsDivinationCard(item, prices)) continue;

            var qty = Math.Max(1, item.Count);
            counts[item.Name] = counts.GetValueOrDefault(item.Name, 0) + qty;
        }
    }

    private static bool IsDivinationCard(ObservedItem item, PriceSnapshot? prices)
    {
        if (string.Equals(item.Kind, "Divination", StringComparison.OrdinalIgnoreCase) ||
            item.Kind.Contains("Divin", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Fallback: if poe.ninja knows this exact item name as a divination card, treat it as one.
        return prices != null && prices.CardPrices.ContainsKey(item.Name);
    }

    private static List<(string Name, int Gain)> ComputeCardGains(
        Dictionary<string, int> previous,
        Dictionary<string, int> current)
    {
        var gains = new List<(string Name, int Gain)>();
        foreach (var (name, count) in current)
        {
            var prev = previous.GetValueOrDefault(name, 0);
            if (count > prev)
            {
                gains.Add((name, count - prev));
            }
        }
        return gains;
    }
}

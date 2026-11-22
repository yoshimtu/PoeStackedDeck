using System.Net.Http.Headers;
using System.Text.Json;
using StackedDeckTracker.Models;

namespace StackedDeckTracker.Services;

/// <summary>
/// Fetches PoE 1 leagues, currency rates, and divination card prices from poe.ninja.
/// </summary>
public sealed class PoeNinjaClient : IDisposable
{
    private const string IndexStateUrl = "https://poe.ninja/poe1/api/data/index-state";
    private const string ExchangeOverviewUrl = "https://poe.ninja/poe1/api/economy/exchange/current/overview";
    private const string StashCurrencyFallbackUrl = "https://poe.ninja/poe1/api/economy/stash/current/currency/overview";

    private readonly HttpClient _http;

    public PoeNinjaClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PoeStackedDeckTracker", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Returns the list of active PoE 1 economy leagues from poe.ninja.
    /// </summary>
    public async Task<IReadOnlyList<string>> FetchLeaguesAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync(IndexStateUrl, ct).ConfigureAwait(false);
        var leagues = new List<string>();

        if (doc.RootElement.TryGetProperty("economyLeagues", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in arr.EnumerateArray())
            {
                if (entry.TryGetProperty("name", out var nameEl) && nameEl.GetString() is { Length: > 0 } name)
                {
                    leagues.Add(name);
                }
            }
        }

        if (leagues.Count == 0)
        {
            leagues.Add("Standard");
        }

        return leagues;
    }

    /// <summary>
    /// Fetches Divine Orb, Stacked Deck, and Divination Card prices for the specified league.
    /// </summary>
    public async Task<PriceSnapshot> FetchPricesAsync(string league, CancellationToken ct)
    {
        var encodedLeague = Uri.EscapeDataString(league);
        var currencyUrl = $"{ExchangeOverviewUrl}?league={encodedLeague}&type=Currency";
        var cardsUrl = $"{ExchangeOverviewUrl}?league={encodedLeague}&type=DivinationCard";

        var currencyTask = GetJsonAsync(currencyUrl, ct);
        var cardsTask = GetJsonAsync(cardsUrl, ct);
        await Task.WhenAll(currencyTask, cardsTask).ConfigureAwait(false);

        using var currencyDoc = currencyTask.Result;
        using var cardsDoc = cardsTask.Result;

        var currencyParsed = ParseExchangeOverview(currencyDoc.RootElement);
        var cardsParsed = ParseExchangeOverview(cardsDoc.RootElement);

        var divinePrice = currencyParsed.Prices.GetValueOrDefault("Divine Orb", 0.0);
        if (divinePrice <= 0 && currencyParsed.DivineRatePerChaos > 0)
        {
            divinePrice = 1.0 / currencyParsed.DivineRatePerChaos;
        }
        if (divinePrice <= 0 && cardsParsed.DivineRatePerChaos > 0)
        {
            divinePrice = 1.0 / cardsParsed.DivineRatePerChaos;
        }

        var stackedDeckPrice = currencyParsed.Prices.GetValueOrDefault("Stacked Deck", 0.0);

        // Fallback to stash currency overview if exchange response lacked either key currency.
        if (divinePrice <= 0 || stackedDeckPrice <= 0)
        {
            var fallback = await TryFetchStashCurrencyFallbackAsync(encodedLeague, ct).ConfigureAwait(false);
            if (divinePrice <= 0) divinePrice = fallback.DivinePrice;
            if (stackedDeckPrice <= 0) stackedDeckPrice = fallback.StackedDeckPrice;
        }

        return new PriceSnapshot
        {
            League = league,
            UpdatedAt = DateTimeOffset.Now,
            DivineChaosPrice = divinePrice,
            StackedDeckChaosPrice = stackedDeckPrice,
            CardPrices = cardsParsed.Prices
        };
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<(double DivinePrice, double StackedDeckPrice)> TryFetchStashCurrencyFallbackAsync(
        string encodedLeague,
        CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync($"{StashCurrencyFallbackUrl}?league={encodedLeague}&type=Currency", ct)
                .ConfigureAwait(false);
            double div = 0, deck = 0;

            if (doc.RootElement.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in lines.EnumerateArray())
                {
                    var name = line.TryGetProperty("currencyTypeName", out var n) ? n.GetString() : null;
                    var chaos = line.TryGetProperty("chaosEquivalent", out var c) && c.TryGetDouble(out var v) ? v : 0;
                    if (string.Equals(name, "Divine Orb", StringComparison.OrdinalIgnoreCase)) div = chaos;
                    else if (string.Equals(name, "Stacked Deck", StringComparison.OrdinalIgnoreCase)) deck = chaos;
                }
            }

            return (div, deck);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static (Dictionary<string, double> Prices, double DivineRatePerChaos) ParseExchangeOverview(JsonElement root)
    {
        var idToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var idEl) &&
                    item.TryGetProperty("name", out var nameEl) &&
                    idEl.GetString() is { Length: > 0 } id &&
                    nameEl.GetString() is { Length: > 0 } name)
                {
                    idToName[id] = name;
                }
            }
        }

        var prices = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in lines.EnumerateArray())
            {
                if (line.TryGetProperty("id", out var idEl) &&
                    line.TryGetProperty("primaryValue", out var valEl) &&
                    idEl.GetString() is { Length: > 0 } id &&
                    valEl.TryGetDouble(out var primaryValue) &&
                    primaryValue > 0 &&
                    idToName.TryGetValue(id, out var name))
                {
                    prices[name] = primaryValue;
                }
            }
        }

        double divineRate = 0;
        if (root.TryGetProperty("core", out var core) &&
            core.TryGetProperty("rates", out var rates) &&
            rates.TryGetProperty("divine", out var divRateEl) &&
            divRateEl.TryGetDouble(out var r) &&
            r > 0)
        {
            divineRate = r;
        }

        return (prices, divineRate);
    }

    public void Dispose() => _http.Dispose();
}

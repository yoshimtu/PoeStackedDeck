using System.Text.Json;
using System.Text.Json.Nodes;
using StackedDeckTracker.Models;

namespace StackedDeckTracker.Services;

/// <summary>
/// Persists plugin settings via the host bridge and session progress in DataDirectory.
/// </summary>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IPluginHostBridge _host;
    private readonly string _sessionPath;
    private readonly string _historyPath;

    public SessionStore(IPluginHostBridge host)
    {
        _host = host;
        _sessionPath = Path.Combine(host.DataDirectory, "session.json");
        _historyPath = Path.Combine(host.DataDirectory, "history.json");
    }

    public TrackerSettings LoadSettings()
    {
        var obj = _host.ReadSettings();
        var customCardPrices = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (obj["customCardPrices"] is JsonObject overrides)
        {
            foreach (var (name, node) in overrides)
            {
                if (!string.IsNullOrWhiteSpace(name) && node is JsonValue value &&
                    value.TryGetValue<double>(out var price) && double.IsFinite(price) && price >= 0)
                    customCardPrices[name.Trim()] = price;
            }
        }
        return new TrackerSettings
        {
            CustomCardPrices = customCardPrices,
            AutoDetectLeague = obj["autoDetectLeague"]?.GetValue<bool>() ?? true,
            League = obj["league"]?.GetValue<string>() is { Length: > 0 } l ? l : "Standard",
            UseCustomDeckCost = obj["useCustomDeckCost"]?.GetValue<bool>() ?? false,
            CustomDeckCostChaos = obj["customDeckCostChaos"]?.GetValue<double>() ?? 3.0,
            MinCardValueChaos = obj["minCardValueChaos"]?.GetValue<double>() ?? 0.0,
            PriceRefreshMinutes = Math.Clamp(obj["priceRefreshMinutes"]?.GetValue<int>() ?? 15, 5, 120),
            PollIntervalMs = Math.Clamp(obj["pollIntervalMs"]?.GetValue<int>() ?? 250, 100, 2000),
            AlertsEnabled = obj["alertsEnabled"]?.GetValue<bool>() ?? true,
            LowAlertChaos = obj["lowAlertChaos"]?.GetValue<double>() ?? 10.0,
            MidAlertChaos = obj["midAlertChaos"]?.GetValue<double>() ?? 60.0,
            HighAlertChaos = obj["highAlertChaos"]?.GetValue<double>() ?? 100.0
        };
    }

    public void SaveSettings(TrackerSettings settings)
    {
        var obj = _host.ReadSettings();
        obj["autoDetectLeague"] = settings.AutoDetectLeague;
        obj["league"] = settings.League;
        obj["useCustomDeckCost"] = settings.UseCustomDeckCost;
        obj["customDeckCostChaos"] = settings.CustomDeckCostChaos;
        obj["customCardPrices"] = JsonSerializer.SerializeToNode(settings.CustomCardPrices);
        obj["minCardValueChaos"] = settings.MinCardValueChaos;
        obj["priceRefreshMinutes"] = settings.PriceRefreshMinutes;
        obj["pollIntervalMs"] = settings.PollIntervalMs;
        obj["alertsEnabled"] = settings.AlertsEnabled;
        obj["lowAlertChaos"] = settings.LowAlertChaos;
        obj["midAlertChaos"] = settings.MidAlertChaos;
        obj["highAlertChaos"] = settings.HighAlertChaos;
        _host.SaveSettings(obj);
    }

    public PersistedSession? LoadSession()
    {
        try
        {
            if (!File.Exists(_sessionPath)) return null;
            var json = File.ReadAllText(_sessionPath);
            return JsonSerializer.Deserialize<PersistedSession>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _host.Log($"Could not load saved session: {ex.Message}");
            return null;
        }
    }

    public void SaveSession(PersistedSession session)
    {
        try
        {
            Directory.CreateDirectory(_host.DataDirectory);
            var temp = _sessionPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(session, JsonOptions));
            File.Move(temp, _sessionPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _host.Log($"Could not save session: {ex.Message}");
        }
    }

    public List<SessionHistoryEntry> LoadHistory()
    {
        try
        {
            if (!File.Exists(_historyPath)) return new List<SessionHistoryEntry>();
            var json = File.ReadAllText(_historyPath);
            return JsonSerializer.Deserialize<List<SessionHistoryEntry>>(json, JsonOptions) ?? new List<SessionHistoryEntry>();
        }
        catch (Exception ex)
        {
            _host.Log($"Could not load session history: {ex.Message}");
            return new List<SessionHistoryEntry>();
        }
    }

    public void SaveHistory(List<SessionHistoryEntry> history)
    {
        try
        {
            Directory.CreateDirectory(_host.DataDirectory);
            var temp = _historyPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(history, JsonOptions));
            File.Move(temp, _historyPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _host.Log($"Could not save session history: {ex.Message}");
        }
    }
}

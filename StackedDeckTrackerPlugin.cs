using PluginHost;
using StackedDeckTracker.Models;
using StackedDeckTracker.Services;
using StackedDeckTracker.UI;

namespace StackedDeckTracker;

/// <summary>
/// PluginHost entry point for the Path of Exile Stacked Deck Tracker.
/// Constructor is strictly side-effect free; all timers, hotkeys, and background tasks
/// start in Start() and are torn down in Dispose().
/// </summary>
public sealed class StackedDeckTrackerPlugin : IPackagedPlugin
{
    public string Name => "Stacked Deck Tracker";

    private IPluginHostBridge? _host;
    private SessionStore? _store;
    private PoeNinjaClient? _ninja;
    private DeckSessionTracker? _tracker;
    private DropAlertService? _alerts;
    private GamePoller? _poller;
    private TrackerView? _view;
    private System.Windows.Forms.Timer? _uiTickTimer;
    private System.Windows.Forms.Timer? _priceRefreshTimer;
    private CancellationTokenSource? _cts;

    private TrackerSettings _settings = new();
    private List<SessionHistoryEntry> _history = new();
    private PriceSnapshot? _prices;
    private PriceSnapshot? _marketPrices;
    private string? _detectedGameLeague;
    private IntPtr _lastPoeWindow;
    private DateTimeOffset _lastSessionSave = DateTimeOffset.MinValue;
    private bool _disposed;

    public Control Start(PluginServices services) =>
        StartWithBridge(new PackagedHostBridge(services));

    Control IDesktopPlugin.Start(Action show, string dataRoot) =>
        StartWithBridge(new LooseDllHostBridge(show, dataRoot));

    private Control StartWithBridge(IPluginHostBridge host)
    {
        _host = host;
        _cts = new CancellationTokenSource();
        _store = new SessionStore(host);
        _ninja = new PoeNinjaClient();
        _tracker = new DeckSessionTracker();
        _alerts = new DropAlertService();

        _settings = _store.LoadSettings();
        _prices = PriceSnapshot.WithCustomCardPrices(null, _settings);
        _history = _store.LoadHistory();
        if (_store.LoadSession() is { } saved)
        {
            _tracker.LoadPersisted(saved);
        }

        var hotkeyRegistered = host.TryRegisterHotkey(
            Keys.Control | Keys.Shift | Keys.F9,
            () => RunOnUi(ToggleTracking));
        var hotkeyHint = hotkeyRegistered
            ? "Ctrl + Shift + F9 (registered)"
            : "Ctrl + Shift + F9 (unavailable — use Start/Pause button)";

        _view = new TrackerView(_settings, hotkeyHint);
        _view.SetPriceStatus(_prices, isFetching: false);
        _view.ToggleTrackingRequested += ToggleTracking;
        _view.RefreshPricesRequested += () => _ = RefreshPricesAsync(userInitiated: true);
        _view.ResetSessionRequested += ResetSession;
        _view.ClearHistoryRequested += ClearHistory;
        _view.OddsScopeChanged += RefreshUiMetrics;
        _view.TestAlertRequested += OnTestAlertRequested;
        _view.SettingsSaved += OnSettingsSaved;

        _poller = new GamePoller(() => _settings.PollIntervalMs, host.Log);
        _poller.StatusChanged += status => RunOnUi(() => OnAttachmentStatusChanged(status));
        _poller.SnapshotCaptured += OnSnapshotCaptured;

        _uiTickTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _uiTickTimer.Tick += (_, _) =>
        {
            if (_tracker is { IsEnabled: true })
            {
                RefreshUiMetrics();
                SaveSessionThrottled(DateTimeOffset.Now);
            }
        };

        _priceRefreshTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Clamp(_settings.PriceRefreshMinutes, 5, 120) * 60_000
        };
        _priceRefreshTimer.Tick += (_, _) => _ = RefreshPricesAsync(userInitiated: false);

        _view.HandleCreated += (_, _) =>
        {
            _uiTickTimer.Start();
            _priceRefreshTimer.Start();
            _poller.Start();
            RefreshUiMetrics();
            _ = InitializeLeaguesAndPricesAsync();
        };

        host.Log("Stacked Deck Tracker initialized.");
        return _view;
    }

    private void ToggleTracking()
    {
        if (_tracker == null || _view == null) return;
        var next = !_tracker.IsEnabled;
        _tracker.SetEnabled(next, DateTimeOffset.Now);
        _view.SetTrackingState(next);
        RefreshUiMetrics();
        SaveSessionNow();
        _host?.Log(next ? "Tracking started." : "Tracking paused.");
    }

    private void ResetSession()
    {
        if (_tracker == null || _view == null) return;
        var now = DateTimeOffset.Now;
        var metrics = _tracker.ComputeMetrics(_prices, _settings, now);
        if (metrics.DecksOpened > 0)
        {
            var confirm = MessageBox.Show(
                _view,
                $"Archive and reset current session ({metrics.DecksOpened:N0} decks opened, {metrics.TotalCardsPulled:N0} cards)?",
                "Reset Stacked Deck Session",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            var entry = _tracker.CreateHistoryEntry(ResolveActiveLeague(), _prices, _settings, now);
            _history.Insert(0, entry);
            _store?.SaveHistory(_history);
        }

        _tracker.ResetSession(now);
        RefreshUiMetrics();
        SaveSessionNow();
        _host?.Log("Session archived and reset.");
    }

    private void ClearHistory()
    {
        if (_view == null || _history.Count == 0) return;
        var confirm = MessageBox.Show(
            _view,
            $"Delete all {_history.Count:N0} saved session(s) from history?",
            "Clear Session History",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        _history.Clear();
        _store?.SaveHistory(_history);
        RefreshUiMetrics();
        _host?.Log("Session history cleared.");
    }

    private void OnTestAlertRequested(DropAlertTier tier)
    {
        if (_alerts == null) return;
        var (card, chaos) = tier switch
        {
            DropAlertTier.High => ("The Apothecary", Math.Max(_settings.HighAlertChaos, 150.0)),
            DropAlertTier.Mid => ("Seven Years Bad Luck", Math.Max(_settings.MidAlertChaos, 65.0)),
            _ => ("The Fortunate", Math.Max(_settings.LowAlertChaos, 12.0))
        };
        var div = _prices is { DivineChaosPrice: > 0 } ? _prices.ToDivines(chaos) : chaos / 200.0;
        _alerts.Trigger(tier, card, chaos, div, _lastPoeWindow);
    }

    private void OnSettingsSaved(TrackerSettings updated)
    {
        var previousLeague = ResolveActiveLeague();
        _settings = updated;
        _store?.SaveSettings(updated);
        _prices = PriceSnapshot.WithCustomCardPrices(_marketPrices, updated);
        _view?.SetPriceStatus(_prices, isFetching: false);

        if (_priceRefreshTimer != null)
        {
            _priceRefreshTimer.Interval = Math.Clamp(updated.PriceRefreshMinutes, 5, 120) * 60_000;
        }

        RefreshUiMetrics();
        _host?.Notify("Stacked Deck Tracker settings saved.");

        if (!string.Equals(previousLeague, ResolveActiveLeague(), StringComparison.OrdinalIgnoreCase))
        {
            _ = RefreshPricesAsync(userInitiated: false);
        }
    }

    private void OnAttachmentStatusChanged(GameAttachmentStatus status)
    {
        _view?.SetAttachmentStatus(status);
        _tracker?.ResetBaseline();

        if (status.Kind == AttachmentKind.Attached && !string.IsNullOrWhiteSpace(status.League))
        {
            var changed = !string.Equals(_detectedGameLeague, status.League, StringComparison.OrdinalIgnoreCase);
            _detectedGameLeague = status.League;
            if (changed && _settings.AutoDetectLeague)
            {
                _host?.Log($"Detected PoE league: {_detectedGameLeague}");
                _ = RefreshPricesAsync(userInitiated: false);
            }
        }
    }

    private void OnSnapshotCaptured(GameSnapshot snapshot)
    {
        if (_tracker == null) return;
        var now = DateTimeOffset.Now;
        if (snapshot.WindowHandle != IntPtr.Zero)
        {
            _lastPoeWindow = snapshot.WindowHandle;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.League) &&
            !string.Equals(_detectedGameLeague, snapshot.League, StringComparison.OrdinalIgnoreCase))
        {
            _detectedGameLeague = snapshot.League;
            if (_settings.AutoDetectLeague)
            {
                RunOnUi(() => _ = RefreshPricesAsync(userInitiated: false));
            }
        }

        if (_tracker.ProcessSnapshot(snapshot, _prices, now, out var newDrops))
        {
            RunOnUi(() =>
            {
                RefreshUiMetrics();
                SaveSessionThrottled(now);
                TriggerDropAlerts(newDrops);
            });
        }
    }

    private void TriggerDropAlerts(IReadOnlyList<DropEvent> newDrops)
    {
        if (_alerts == null || newDrops.Count == 0) return;

        // Pick the highest-value drop in this tick so simultaneous drops don't overlap popups.
        DropEvent? best = null;
        double bestChaos = -1;
        foreach (var drop in newDrops)
        {
            var val = (_prices?.GetCardPrice(drop.CardName) ?? drop.UnitChaosAtPull) * drop.Count;
            if (val > bestChaos)
            {
                bestChaos = val;
                best = drop;
            }
        }

        if (best == null) return;
        var tier = DropAlertService.Classify(bestChaos, _settings);
        if (tier == DropAlertTier.None) return;

        var div = _prices is { DivineChaosPrice: > 0 } ? _prices.ToDivines(bestChaos) : 0.0;
        _alerts.Trigger(tier, best.CardName, bestChaos, div, _lastPoeWindow);

        if (tier == DropAlertTier.High)
        {
            var divPart = div >= 0.1 ? $"{div:0.00} div / " : "";
            _host?.Notify($"High drop! {best.CardName} ({divPart}{bestChaos:0.#}c)");
        }
    }

    private async Task InitializeLeaguesAndPricesAsync()
    {
        if (_ninja == null || _cts == null) return;
        try
        {
            var leagues = await _ninja.FetchLeaguesAsync(_cts.Token).ConfigureAwait(false);
            RunOnUi(() =>
            {
                // If user still has default "Standard" and a challenge league exists, default to the active challenge league.
                if (_settings.AutoDetectLeague && string.IsNullOrEmpty(_detectedGameLeague) &&
                    string.Equals(_settings.League, "Standard", StringComparison.OrdinalIgnoreCase) &&
                    leagues.Count > 0)
                {
                    _settings.League = leagues[0];
                }
                _view?.PopulateLeagues(leagues, ResolveActiveLeague());
            });
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _host?.Log($"Failed to fetch poe.ninja league list: {ex.Message}");
        }

        await RefreshPricesAsync(userInitiated: false).ConfigureAwait(false);
    }

    private async Task RefreshPricesAsync(bool userInitiated)
    {
        if (_ninja == null || _cts == null || _disposed) return;

        var league = ResolveActiveLeague();
        RunOnUi(() => _view?.SetPriceStatus(_prices, isFetching: true));

        try
        {
            var snapshot = await _ninja.FetchPricesAsync(league, _cts.Token).ConfigureAwait(false);
            _host?.Log(
                $"Updated poe.ninja prices for {league}: Div={snapshot.DivineChaosPrice:0.#}c, Deck={snapshot.StackedDeckChaosPrice:0.##}c, Cards={snapshot.CardPrices.Count}.");

            RunOnUi(() =>
            {
                _marketPrices = snapshot;
                _prices = PriceSnapshot.WithCustomCardPrices(snapshot, _settings);
                _view?.SetPriceStatus(_prices, isFetching: false);
                RefreshUiMetrics();
                if (userInitiated)
                {
                    _host?.Notify($"Updated {league} prices (Div {snapshot.DivineChaosPrice:0.#}c, Deck {snapshot.StackedDeckChaosPrice:0.##}c)");
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _host?.Log($"Price refresh failed for '{league}': {ex.Message}");
            RunOnUi(() => _view?.SetPriceStatus(_prices, isFetching: false, errorMessage: ex.Message));
        }
    }

    private string ResolveActiveLeague() =>
        _settings.AutoDetectLeague && !string.IsNullOrWhiteSpace(_detectedGameLeague)
            ? _detectedGameLeague!
            : (string.IsNullOrWhiteSpace(_settings.League) ? "Standard" : _settings.League);

    private void RefreshUiMetrics()
    {
        if (_tracker == null || _view == null || _view.IsDisposed) return;
        var metrics = _tracker.ComputeMetrics(_prices, _settings, DateTimeOffset.Now);
        _view.RenderMetrics(metrics, _settings);

        var historyForOdds = _view.IncludeHistoryInOdds ? _history : null;
        var (totalDecks, totalEv, unitCost, oddsRows) = _tracker.ComputeOdds(_prices, _settings, historyForOdds);
        _view.RenderOdds(totalDecks, totalEv, unitCost, oddsRows);
        _view.RenderHistory(_history);
    }

    private void SaveSessionThrottled(DateTimeOffset now)
    {
        if ((now - _lastSessionSave).TotalSeconds < 10) return;
        SaveSessionNow();
    }

    private void SaveSessionNow()
    {
        if (_tracker == null || _store == null) return;
        var now = DateTimeOffset.Now;
        _lastSessionSave = now;
        _store.SaveSession(_tracker.ExportPersisted(now));
    }

    private void RunOnUi(Action action)
    {
        var view = _view;
        if (_disposed || view == null || view.IsDisposed) return;

        if (!view.IsHandleCreated)
        {
            return;
        }

        if (view.InvokeRequired)
        {
            try
            {
                view.BeginInvoke(() =>
                {
                    if (!_disposed && !view.IsDisposed) action();
                });
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }
        else
        {
            action();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SaveSessionNow();

        try { _cts?.Cancel(); } catch { }
        _uiTickTimer?.Stop();
        _uiTickTimer?.Dispose();
        _priceRefreshTimer?.Stop();
        _priceRefreshTimer?.Dispose();
        _poller?.Dispose();
        _alerts?.Dispose();
        _ninja?.Dispose();
        _host?.Dispose();
        _cts?.Dispose();
    }
}

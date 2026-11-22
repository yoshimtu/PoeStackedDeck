using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using PluginHost;
using StackedDeckTracker.Models;
using StackedDeckTracker.Services;

namespace StackedDeckTracker.UI;

/// <summary>
/// Main WinForms view for the Stacked Deck Tracker plugin.
/// Styled with PluginHost's AtlasPanel, LedgerBanner, LedgerButton, HostUi, and HostAssets.
/// </summary>
public sealed class TrackerView : Panel
{
    private static readonly Color ProfitGreen = Color.FromArgb(118, 214, 149);
    private static readonly Color LossRed = Color.FromArgb(232, 118, 118);

    private readonly AtlasPanel _atlas;
    private readonly LedgerBanner _banner;
    private readonly LedgerButton _toggleTrackingBtn;
    private readonly LedgerButton _refreshPricesBtn;
    private readonly LedgerButton _resetBtn;
    private readonly LedgerButton _cardsTabBtn;
    private readonly LedgerButton _feedTabBtn;
    private readonly LedgerButton _oddsTabBtn;
    private readonly LedgerButton _historyTabBtn;
    private readonly LedgerButton _settingsTabBtn;

    private readonly KpiCard _decksCard;
    private readonly KpiCard _valueCard;
    private readonly KpiCard _profitCard;
    private readonly KpiCard _rateCard;

    private readonly Panel _contentHost;
    private readonly DataGridView _cardsGrid;
    private readonly DataGridView _feedGrid;
    private readonly Panel _oddsHost;
    private readonly Label _oddsSummaryLabel;
    private readonly CheckBox _oddsIncludeHistoryCheck;
    private readonly DataGridView _oddsGrid;
    private readonly Panel _historyHost;
    private readonly DataGridView _historyGrid;
    private readonly Panel _settingsHost;
    private CustomCardPricesEditor _customCardPrices = null!;

    // Settings controls
    private readonly CheckBox _autoLeagueCheck;
    private readonly ComboBox _leagueCombo;
    private readonly CheckBox _customCostCheck;
    private readonly NumericUpDown _customCostInput;
    private readonly NumericUpDown _minCardValueInput;
    private readonly NumericUpDown _refreshMinutesInput;
    private readonly NumericUpDown _pollIntervalInput;
    private readonly CheckBox _alertsEnabledCheck;
    private readonly NumericUpDown _lowAlertInput;
    private readonly NumericUpDown _midAlertInput;
    private readonly NumericUpDown _highAlertInput;

    private readonly List<Image> _ownedImages = new();

    private bool _isTracking;
    private bool _isFetchingPrices;
    private GameAttachmentStatus _attachment = new(AttachmentKind.WaitingForGame, "Waiting for Path of Exile...");
    private PriceSnapshot? _prices;
    private string _activeView = "cards";

    public bool IncludeHistoryInOdds => _oddsIncludeHistoryCheck.Checked;

    public event Action? ToggleTrackingRequested;
    public event Action? RefreshPricesRequested;
    public event Action? ResetSessionRequested;
    public event Action? ClearHistoryRequested;
    public event Action? OddsScopeChanged;
    public event Action<DropAlertTier>? TestAlertRequested;
    public event Action<TrackerSettings>? SettingsSaved;

    public TrackerView(TrackerSettings initialSettings, string hotkeyHint)
    {
        Dock = DockStyle.Fill;
        BackColor = HostUi.Background;

        _atlas = new AtlasPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14)
        };
        Controls.Add(_atlas);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // Banner
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // KPI cards
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // Toolbar
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // Main content

        // 1. Banner
        _banner = new LedgerBanner
        {
            Dock = DockStyle.Top,
            Height = 72,
            Margin = new Padding(0, 0, 0, 10)
        };
        root.Controls.Add(_banner, 0, 0);

        // 2. KPI Summary Row
        var kpiRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 4,
            RowCount = 1,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 10)
        };
        for (int i = 0; i < 4; i++)
        {
            kpiRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        }

        _decksCard = new KpiCard("DECKS OPENED", LoadHostIcon("Currency/Stacked Deck.png"));
        _valueCard = new KpiCard("CARDS VALUE", LoadHostIcon("Currency/Chaos Orb.png"));
        _profitCard = new KpiCard("NET PROFIT", LoadHostIcon("Currency/Divine Orb.png"));
        _rateCard = new KpiCard("PROFIT / HOUR", null);

        kpiRow.Controls.Add(_decksCard, 0, 0);
        kpiRow.Controls.Add(_valueCard, 1, 0);
        kpiRow.Controls.Add(_profitCard, 2, 0);
        kpiRow.Controls.Add(_rateCard, 3, 0);
        root.Controls.Add(kpiRow, 0, 1);

        // 3. Action & Navigation Toolbar
        var bar = HostUi.Toolbar();
        bar.Dock = DockStyle.Top;
        bar.Margin = new Padding(0, 0, 0, 10);

        _toggleTrackingBtn = CreateActionButton("Start Tracking", primary: true);
        _toggleTrackingBtn.Click += (_, _) => ToggleTrackingRequested?.Invoke();

        _refreshPricesBtn = CreateActionButton("Update Prices", primary: false);
        _refreshPricesBtn.Click += (_, _) => RefreshPricesRequested?.Invoke();

        _resetBtn = CreateActionButton("Reset Session", primary: false);
        _resetBtn.Click += (_, _) => ResetSessionRequested?.Invoke();

        var spacer = new Panel { Width = 12, Height = 1, BackColor = Color.Transparent };

        _cardsTabBtn = CreateNavButton("Pulled Cards");
        _cardsTabBtn.Click += (_, _) => SwitchTab("cards");

        _feedTabBtn = CreateNavButton("Recent Drops");
        _feedTabBtn.Click += (_, _) => SwitchTab("feed");

        _oddsTabBtn = CreateNavButton("Odds & EV");
        _oddsTabBtn.Click += (_, _) => SwitchTab("odds");

        _historyTabBtn = CreateNavButton("History");
        _historyTabBtn.Click += (_, _) => SwitchTab("history");

        _settingsTabBtn = CreateNavButton("Settings");
        _settingsTabBtn.Click += (_, _) => SwitchTab("settings");

        bar.Controls.AddRange([
            _toggleTrackingBtn, _refreshPricesBtn, _resetBtn, spacer,
            _cardsTabBtn, _feedTabBtn, _oddsTabBtn, _historyTabBtn, _settingsTabBtn
        ]);
        root.Controls.Add(bar, 0, 2);

        // 4. Main Content Host
        _contentHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        root.Controls.Add(_contentHost, 0, 3);

        _cardsGrid = BuildCardsGrid();
        _contentHost.Controls.Add(_cardsGrid);

        _feedGrid = BuildFeedGrid();
        _feedGrid.Visible = false;
        _contentHost.Controls.Add(_feedGrid);

        (_oddsHost, _oddsSummaryLabel, _oddsIncludeHistoryCheck, _oddsGrid) = BuildOddsPanel();
        _oddsHost.Visible = false;
        _contentHost.Controls.Add(_oddsHost);

        (_historyHost, _historyGrid) = BuildHistoryPanel();
        _historyHost.Visible = false;
        _contentHost.Controls.Add(_historyHost);

        (_settingsHost, _autoLeagueCheck, _leagueCombo, _customCostCheck, _customCostInput,
            _minCardValueInput, _refreshMinutesInput, _pollIntervalInput,
            _alertsEnabledCheck, _lowAlertInput, _midAlertInput, _highAlertInput) =
            BuildSettingsPanel(initialSettings, hotkeyHint);
        _settingsHost.Visible = false;
        _contentHost.Controls.Add(_settingsHost);

        _atlas.Controls.Add(root);
        SwitchTab("cards");
        UpdateBanner();
    }

    public void SetTrackingState(bool enabled)
    {
        _isTracking = enabled;
        _toggleTrackingBtn.Text = enabled ? "Pause Tracking" : "Start Tracking";
        UpdateBanner();
    }

    public void SetAttachmentStatus(GameAttachmentStatus status)
    {
        _attachment = status;
        UpdateBanner();
    }

    public void SetPriceStatus(PriceSnapshot? prices, bool isFetching, string? errorMessage = null)
    {
        _prices = prices;
        if (prices != null) _customCardPrices.AddKnownCards(prices.CardPrices.Keys);
        _isFetchingPrices = isFetching;
        _refreshPricesBtn.Enabled = !isFetching;
        _refreshPricesBtn.Text = isFetching ? "Updating..." : "Update Prices";
        UpdateBanner(errorMessage);
    }

    public void PopulateLeagues(IReadOnlyList<string> leagues, string selectedLeague)
    {
        var current = string.IsNullOrWhiteSpace(selectedLeague) ? _leagueCombo.Text : selectedLeague;
        _leagueCombo.BeginUpdate();
        try
        {
            _leagueCombo.Items.Clear();
            foreach (var league in leagues) _leagueCombo.Items.Add(league);
            if (!string.IsNullOrWhiteSpace(current))
            {
                if (!_leagueCombo.Items.Contains(current)) _leagueCombo.Items.Add(current);
                _leagueCombo.SelectedItem = current;
            }
            else if (_leagueCombo.Items.Count > 0)
            {
                _leagueCombo.SelectedIndex = 0;
            }
        }
        finally
        {
            _leagueCombo.EndUpdate();
        }
    }

    public void RenderMetrics(SessionMetrics m, TrackerSettings settings)
    {
        _customCardPrices.AddKnownCards(m.Cards.Select(c => c.CardName));
        var costDivText = m.HasPrices ? $" ({m.TotalCostDivines:0.00} div)" : "";
        _decksCard.SetValues(
            $"{m.DecksOpened:N0}",
            $"Cost: {FormatChaos(m.TotalCostChaos)}{costDivText}",
            $"@ {m.UnitDeckCostChaos:0.##}c / deck" + (settings.UseCustomDeckCost ? " (custom)" : " (poe.ninja)"),
            HostUi.TextColor);

        var filterNote = settings.MinCardValueChaos > 0
            ? $"Cards ≥ {settings.MinCardValueChaos:0.##}c  •  Gross {FormatChaos(m.GrossValueChaos)}"
            : $"{m.TotalCardsPulled:N0} cards recorded";
        _valueCard.SetValues(
            FormatChaos(m.CountedValueChaos),
            m.HasPrices ? $"{m.CountedValueDivines:0.00} div" : "Waiting for prices",
            filterNote,
            HostUi.Accent);

        var profitColor = m.NetProfitChaos > 0.005
            ? ProfitGreen
            : m.NetProfitChaos < -0.005 ? LossRed : HostUi.TextColor;
        var profitSign = m.NetProfitChaos > 0 ? "+" : "";
        var profitDivLine = m.HasPrices ? $"{profitSign}{m.NetProfitDivines:0.00} div" : "—";
        var roiSign = m.RoiPercent > 0 ? "+" : "";
        var avgSign = m.AvgProfitPerDeckChaos > 0 ? "+" : "";
        _profitCard.SetValues(
            $"{profitSign}{FormatChaos(m.NetProfitChaos)}",
            profitDivLine,
            $"ROI {roiSign}{m.RoiPercent:0.0}%  •  {avgSign}{m.AvgProfitPerDeckChaos:0.00}c / deck",
            profitColor);

        var rateSign = m.ProfitPerHourChaos > 0 ? "+" : "";
        var rateDivLine = m.HasPrices ? $"{rateSign}{m.ProfitPerHourDivines:0.00} div/hr" : "—";
        var elapsedStr = $"{(int)m.ActiveElapsed.TotalHours:00}:{m.ActiveElapsed.Minutes:00}:{m.ActiveElapsed.Seconds:00}";
        _rateCard.SetValues(
            $"{rateSign}{FormatChaos(m.ProfitPerHourChaos)}/hr",
            rateDivLine,
            $"Active {elapsedStr}  •  {m.DecksPerHour:N0} decks/hr",
            profitColor);

        UpdateCardsGrid(m);
        UpdateFeedGrid(m);
    }

    public void RenderOdds(int totalDecks, double totalEvPerDeck, double unitDeckCost, IReadOnlyList<CardOddsSummary> rows)
    {
        var netEv = totalEvPerDeck - unitDeckCost;
        var sign = netEv > 0 ? "+" : "";
        _oddsSummaryLabel.Text = totalDecks > 0
            ? $"Sample: {totalDecks:N0} decks  •  {rows.Count:N0} unique cards  •  Observed EV/Deck: {totalEvPerDeck:0.00}c  vs  Cost: {unitDeckCost:0.00}c  ({sign}{netEv:0.00}c / deck)"
            : "Open Stacked Decks to compute observed drop rates and effective stacked-deck prices.";

        PreserveGridSort(_oddsGrid, () =>
        {
            _oddsGrid.Rows.Clear();
            foreach (var r in rows)
            {
                _oddsGrid.Rows.Add(
                    r.CardName,
                    r.Count,
                    r.DropRatePercent,
                    r.OneInDecks,
                    r.MarketChaos,
                    r.EvPerDeckChaos,
                    r.EffectiveCostChaos);
            }
        });
    }

    public void RenderHistory(IReadOnlyList<SessionHistoryEntry> history)
    {
        PreserveGridSort(_historyGrid, () =>
        {
            _historyGrid.Rows.Clear();
            foreach (var h in history)
            {
                var hours = h.ActiveElapsedSeconds >= 1.0 ? h.ActiveElapsedSeconds / 3600.0 : 0.0;
                var profitPerHr = hours > 0 ? h.NetProfitChaos / hours : 0.0;
                var topText = string.IsNullOrWhiteSpace(h.TopCardName)
                    ? "—"
                    : $"{h.TopCardName} ({h.TopCardChaos:0.#}c)";

                var idx = _historyGrid.Rows.Add(
                    h.EndedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    string.IsNullOrWhiteSpace(h.League) ? "—" : h.League,
                    h.DecksOpened,
                    h.TotalCostChaos,
                    h.CountedValueChaos,
                    h.NetProfitChaos,
                    h.NetProfitDivines,
                    profitPerHr,
                    topText);

                var row = _historyGrid.Rows[idx];
                row.Cells[5].Style.ForeColor = h.NetProfitChaos >= 0 ? ProfitGreen : LossRed;
                row.Cells[6].Style.ForeColor = h.NetProfitChaos >= 0 ? ProfitGreen : LossRed;
            }
        });
    }

    private void UpdateCardsGrid(SessionMetrics m)
    {
        var firstDisplayed = _cardsGrid.FirstDisplayedScrollingRowIndex;
        var selectedCard = _cardsGrid.CurrentRow?.Cells[0].Value as string;

        PreserveGridSort(_cardsGrid, () =>
        {
            _cardsGrid.Rows.Clear();
            foreach (var card in m.Cards)
            {
                var idx = _cardsGrid.Rows.Add(
                    card.CardName,
                    card.Count,
                    card.UnitChaos,
                    card.TotalChaos,
                    m.HasPrices ? card.TotalDivines : 0.0,
                    card.LastPulled.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));

                var row = _cardsGrid.Rows[idx];
                if (!card.IncludedInProfit)
                {
                    row.DefaultCellStyle.ForeColor = HostUi.Muted;
                }
                else if (m.HasPrices && card.TotalDivines >= 1.0)
                {
                    row.DefaultCellStyle.ForeColor = HostUi.Accent;
                }
            }
        });

        if (selectedCard != null)
        {
            foreach (DataGridViewRow row in _cardsGrid.Rows)
            {
                if (row.Cells[0].Value is string name && string.Equals(name, selectedCard, StringComparison.OrdinalIgnoreCase))
                {
                    _cardsGrid.CurrentCell = row.Cells[0];
                    break;
                }
            }
        }
        if (firstDisplayed >= 0 && firstDisplayed < _cardsGrid.Rows.Count)
        {
            _cardsGrid.FirstDisplayedScrollingRowIndex = firstDisplayed;
        }
    }

    private void UpdateFeedGrid(SessionMetrics m)
    {
        PreserveGridSort(_feedGrid, () =>
        {
            _feedGrid.Rows.Clear();
            foreach (var ev in m.RecentDrops)
            {
                var unitChaos = _prices?.GetCardPrice(ev.CardName) ?? ev.UnitChaosAtPull;
                var totalChaos = unitChaos * ev.Count;
                var totalDiv = _prices is { DivineChaosPrice: > 0 } ? _prices.ToDivines(totalChaos) : 0.0;

                var idx = _feedGrid.Rows.Add(
                    ev.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    ev.CardName,
                    ev.Count,
                    totalChaos,
                    totalDiv);

                if (totalDiv >= 1.0)
                {
                    _feedGrid.Rows[idx].DefaultCellStyle.ForeColor = HostUi.Accent;
                }
            }
        });
    }

    /// <summary>
    /// Refreshes a DataGridView while preserving any user-selected column sort order.
    /// </summary>
    private static void PreserveGridSort(DataGridView grid, Action populate)
    {
        var sortedCol = grid.SortedColumn;
        var sortOrder = grid.SortOrder;

        grid.SuspendLayout();
        try
        {
            populate();
            if (sortedCol != null && sortOrder != SortOrder.None)
            {
                var dir = sortOrder == SortOrder.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
                grid.Sort(sortedCol, dir);
            }
        }
        finally
        {
            grid.ResumeLayout();
        }
    }

    private void UpdateBanner(string? priceError = null)
    {
        var trackLabel = _isTracking ? "● TRACKING" : "○ PAUSED";
        string pricePart;
        if (!string.IsNullOrWhiteSpace(priceError))
        {
            pricePart = $"Prices error: {priceError}";
        }
        else if (_isFetchingPrices && _prices == null)
        {
            pricePart = "Loading poe.ninja prices...";
        }
        else if (_prices is { UpdatedAt: var date } && date == default)
        {
            pricePart = $"{_prices.CardPrices.Count} custom card prices active; market prices not loaded";
        }
        else if (_prices != null)
        {
            var updated = _prices.UpdatedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            pricePart = $"{_prices.League}  •  1 Div = {_prices.DivineChaosPrice:0.#}c  •  Deck = {_prices.StackedDeckChaosPrice:0.##}c  •  {_prices.CardPrices.Count} cards ({updated})";
        }
        else
        {
            pricePart = "Prices not loaded";
        }

        _banner.SetContent("Stacked Deck Tracker", $"{trackLabel}   |   {_attachment.Message}   |   {pricePart}");
    }

    private void SwitchTab(string view)
    {
        _activeView = view;
        _cardsGrid.Visible = view == "cards";
        _feedGrid.Visible = view == "feed";
        _oddsHost.Visible = view == "odds";
        _historyHost.Visible = view == "history";
        _settingsHost.Visible = view == "settings";

        _cardsTabBtn.Primary = view == "cards";
        _feedTabBtn.Primary = view == "feed";
        _oddsTabBtn.Primary = view == "odds";
        _historyTabBtn.Primary = view == "history";
        _settingsTabBtn.Primary = view == "settings";
    }

    private static DataGridViewTextBoxColumn NumericColumn(string name, string header, Type valueType, string format, int width)
    {
        return new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            ValueType = valueType,
            Width = width,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Format = format,
                Alignment = DataGridViewContentAlignment.MiddleRight
            }
        };
    }

    private static void FormatSmallDivinesAsDash(DataGridView grid, int divColumnIndex)
    {
        grid.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex == divColumnIndex && e.Value is double d && Math.Abs(d) < 0.01)
            {
                e.Value = "—";
                e.FormattingApplied = true;
            }
        };
    }

    private static DataGridView BuildCardsGrid()
    {
        var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true };
        HostUi.StyleTable(grid);
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "card",
            HeaderText = "Divination Card",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        grid.Columns.Add(NumericColumn("qty", "Qty", typeof(int), "N0", 65));
        grid.Columns.Add(NumericColumn("unit", "Unit (c)", typeof(double), "0.##", 90));
        grid.Columns.Add(NumericColumn("totalC", "Total (c)", typeof(double), "0.##", 100));
        grid.Columns.Add(NumericColumn("totalDiv", "Total (div)", typeof(double), "0.00", 95));
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "last", HeaderText = "Last Pulled", Width = 95 });
        FormatSmallDivinesAsDash(grid, 4);
        return grid;
    }

    private static DataGridView BuildFeedGrid()
    {
        var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true };
        HostUi.StyleTable(grid);
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "time", HeaderText = "Time", Width = 95 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "card",
            HeaderText = "Divination Card",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        grid.Columns.Add(NumericColumn("qty", "Qty", typeof(int), "N0", 65));
        grid.Columns.Add(NumericColumn("valC", "Value (c)", typeof(double), "0.##", 100));
        grid.Columns.Add(NumericColumn("valDiv", "Value (div)", typeof(double), "0.00", 95));
        FormatSmallDivinesAsDash(grid, 4);
        return grid;
    }

    private (Panel Host, Label Summary, CheckBox IncludeHistory, DataGridView Grid) BuildOddsPanel()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 36,
            BackColor = HostUi.Surface,
            Padding = new Padding(12, 6, 12, 6),
            Margin = new Padding(0, 0, 0, 8)
        };

        var summary = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = HostUi.Accent,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent,
            Text = "Open Stacked Decks to compute observed drop rates and effective stacked-deck prices."
        };

        var includeHistory = new CheckBox
        {
            Text = "Include saved history",
            Checked = true,
            AutoSize = true,
            Dock = DockStyle.Right,
            ForeColor = HostUi.TextColor,
            BackColor = Color.Transparent
        };
        includeHistory.CheckedChanged += (_, _) => OddsScopeChanged?.Invoke();

        header.Controls.Add(summary);
        header.Controls.Add(includeHistory);

        var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true };
        HostUi.StyleTable(grid);
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "card",
            HeaderText = "Divination Card",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        grid.Columns.Add(NumericColumn("qty", "Pulled", typeof(int), "N0", 65));
        grid.Columns.Add(NumericColumn("rate", "Drop Rate %", typeof(double), "0.00'%'", 95));
        grid.Columns.Add(NumericColumn("oneIn", "1 in N Decks", typeof(double), "0.#", 95));
        grid.Columns.Add(NumericColumn("market", "Market (c)", typeof(double), "0.##", 90));
        grid.Columns.Add(NumericColumn("ev", "EV / Deck (c)", typeof(double), "0.000", 100));
        grid.Columns.Add(NumericColumn("effPrice", "Eff. Deck Price (c)", typeof(double), "0.##", 125));

        host.Controls.Add(grid);
        host.Controls.Add(header);
        return (host, summary, includeHistory, grid);
    }

    private (Panel Host, DataGridView Grid) BuildHistoryPanel()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

        var bar = HostUi.Toolbar();
        bar.Dock = DockStyle.Top;
        bar.Margin = new Padding(0, 0, 0, 6);

        var info = new Label
        {
            Text = "Completed sessions are archived automatically on Reset Session.",
            AutoSize = true,
            ForeColor = HostUi.Muted,
            Padding = new Padding(0, 8, 12, 0),
            BackColor = Color.Transparent
        };
        var clearBtn = CreateActionButton("Clear History", primary: false);
        clearBtn.Click += (_, _) => ClearHistoryRequested?.Invoke();

        bar.Controls.Add(info);
        bar.Controls.Add(clearBtn);

        var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true };
        HostUi.StyleTable(grid);
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ended", HeaderText = "Ended", Width = 125 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "league", HeaderText = "League", Width = 85 });
        grid.Columns.Add(NumericColumn("decks", "Decks", typeof(int), "N0", 65));
        grid.Columns.Add(NumericColumn("cost", "Cost (c)", typeof(double), "0.#", 80));
        grid.Columns.Add(NumericColumn("val", "Value (c)", typeof(double), "0.#", 80));
        grid.Columns.Add(NumericColumn("profit", "Profit (c)", typeof(double), "+0.#;-0.#;0", 85));
        grid.Columns.Add(NumericColumn("profitDiv", "Profit (div)", typeof(double), "+0.00;-0.00;0", 85));
        grid.Columns.Add(NumericColumn("pph", "Profit/hr (c)", typeof(double), "+0.#;-0.#;0", 95));
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "top",
            HeaderText = "Top Drop",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        FormatSmallDivinesAsDash(grid, 6);

        host.Controls.Add(grid);
        host.Controls.Add(bar);
        return (host, grid);
    }

    private (Panel Host, CheckBox AutoLeague, ComboBox LeagueCombo, CheckBox CustomCost,
        NumericUpDown CustomCostInput, NumericUpDown MinCardVal, NumericUpDown RefreshMins,
        NumericUpDown PollMs, CheckBox AlertsEnabled, NumericUpDown LowAlert,
        NumericUpDown MidAlert, NumericUpDown HighAlert)
        BuildSettingsPanel(TrackerSettings s, string hotkeyHint)
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 4, 0, 12)
        };

        // Card 1: Tracker & Pricing Settings
        var card = HostUi.SettingsCard("Tracker & Pricing Settings");
        card.Dock = DockStyle.Top;
        card.Height = 410;

        var autoLeague = new CheckBox
        {
            Text = "Auto-detect league from Path of Exile",
            Checked = s.AutoDetectLeague,
            AutoSize = true,
            ForeColor = HostUi.TextColor,
            BackColor = Color.Transparent
        };
        HostUi.AddField(card, "League Detection", autoLeague);

        var leagueCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            Width = 220,
            BackColor = HostUi.Background,
            ForeColor = HostUi.TextColor,
            FlatStyle = FlatStyle.Flat
        };
        leagueCombo.Items.Add(s.League);
        leagueCombo.SelectedItem = s.League;
        HostUi.AddField(card, "poe.ninja League", leagueCombo);

        var customCostCheck = new CheckBox
        {
            Text = "Override poe.ninja Stacked Deck price with custom cost",
            Checked = s.UseCustomDeckCost,
            AutoSize = true,
            ForeColor = HostUi.TextColor,
            BackColor = Color.Transparent
        };
        HostUi.AddField(card, "Deck Cost Mode", customCostCheck);

        var customCostInput = HostUi.Number((decimal)s.CustomDeckCostChaos, 0, 10000, 2);
        customCostInput.Enabled = s.UseCustomDeckCost;
        customCostCheck.CheckedChanged += (_, _) => customCostInput.Enabled = customCostCheck.Checked;
        HostUi.AddField(card, "Custom Deck Cost (c)", customCostInput);

        var minCardVal = HostUi.Number((decimal)s.MinCardValueChaos, 0, 100000, 2);
        HostUi.AddField(card, "Min Card Value (c)", minCardVal);

        var refreshMins = HostUi.Number(s.PriceRefreshMinutes, 5, 120, 0);
        HostUi.AddField(card, "Auto-Refresh (min)", refreshMins);

        var pollMs = HostUi.Number(s.PollIntervalMs, 100, 2000, 0);
        HostUi.AddField(card, "Poll Interval (ms)", pollMs);

        var hotkeyLabel = new Label
        {
            Text = hotkeyHint,
            AutoSize = true,
            ForeColor = HostUi.Muted,
            BackColor = Color.Transparent
        };
        HostUi.AddField(card, "Toggle Hotkey", hotkeyLabel);

        // Card 2: Drop Alerts
        var alertCard = HostUi.SettingsCard("Drop Alerts (Sound & PoE Window Popup)");
        alertCard.Dock = DockStyle.Top;
        alertCard.Height = 310;

        var alertsEnabled = new CheckBox
        {
            Text = "Enable Low / Mid / High drop sounds and PoE overlay popups",
            Checked = s.AlertsEnabled,
            AutoSize = true,
            ForeColor = HostUi.TextColor,
            BackColor = Color.Transparent
        };
        HostUi.AddField(alertCard, "Alerts", alertsEnabled);

        var lowAlert = HostUi.Number((decimal)s.LowAlertChaos, 0, 100000, 1);
        HostUi.AddField(alertCard, "Low Alert (c) — Soft Chime", lowAlert);

        var midAlert = HostUi.Number((decimal)s.MidAlertChaos, 0, 100000, 1);
        HostUi.AddField(alertCard, "Mid Alert (c) — Tink + Popup", midAlert);

        var highAlert = HostUi.Number((decimal)s.HighAlertChaos, 0, 100000, 1);
        HostUi.AddField(alertCard, "High Alert (c) — Tink + Confetti", highAlert);

        var testRow = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        var testLowBtn = CreateActionButton("Test Low", false);
        testLowBtn.Click += (_, _) => TestAlertRequested?.Invoke(DropAlertTier.Low);
        var testMidBtn = CreateActionButton("Test Mid", false);
        testMidBtn.Click += (_, _) => TestAlertRequested?.Invoke(DropAlertTier.Mid);
        var testHighBtn = CreateActionButton("Test High", false);
        testHighBtn.Click += (_, _) => TestAlertRequested?.Invoke(DropAlertTier.High);
        testRow.Controls.AddRange([testLowBtn, testMidBtn, testHighBtn]);
        HostUi.AddField(alertCard, "Preview Alerts", testRow);

        _customCardPrices = new CustomCardPricesEditor(s.CustomCardPrices);

        var saveBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(0, 10, 0, 0),
            BackColor = Color.Transparent
        };
        var saveBtn = CreateActionButton("Save Settings", primary: true);
        saveBtn.Click += (_, _) =>
        {
            var updated = new TrackerSettings
            {
                AutoDetectLeague = autoLeague.Checked,
                League = string.IsNullOrWhiteSpace(leagueCombo.Text) ? "Standard" : leagueCombo.Text.Trim(),
                UseCustomDeckCost = customCostCheck.Checked,
                CustomDeckCostChaos = (double)customCostInput.Value,
                CustomCardPrices = _customCardPrices.ExportPrices(),
                MinCardValueChaos = (double)minCardVal.Value,
                PriceRefreshMinutes = (int)refreshMins.Value,
                PollIntervalMs = (int)pollMs.Value,
                AlertsEnabled = alertsEnabled.Checked,
                LowAlertChaos = (double)lowAlert.Value,
                MidAlertChaos = (double)midAlert.Value,
                HighAlertChaos = (double)highAlert.Value
            };
            SettingsSaved?.Invoke(updated);
            _customCardPrices.MarkSaved();
        };
        saveBar.Controls.Add(saveBtn);

        var spacer = new Panel { Dock = DockStyle.Top, Height = 12, BackColor = Color.Transparent };

        // Dock.Top stacks bottom-to-top in Controls order
        host.Controls.Add(saveBar);
        host.Controls.Add(alertCard);
        host.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 12 });
        host.Controls.Add(_customCardPrices);
        host.Controls.Add(spacer);
        host.Controls.Add(card);

        return (host, autoLeague, leagueCombo, customCostCheck, customCostInput, minCardVal,
            refreshMins, pollMs, alertsEnabled, lowAlert, midAlert, highAlert);
    }

    private static LedgerButton CreateActionButton(string text, bool primary) => new()
    {
        Text = text,
        Primary = primary,
        AutoSize = true,
        MinimumSize = new Size(106, 32),
        Padding = new Padding(10, 4, 10, 4),
        Margin = new Padding(0, 0, 8, 0)
    };

    private static LedgerButton CreateNavButton(string text) => new()
    {
        Text = text,
        Navigation = true,
        AutoSize = true,
        MinimumSize = new Size(88, 32),
        Padding = new Padding(8, 4, 8, 4),
        Margin = new Padding(0, 0, 6, 0)
    };

    private Image? LoadHostIcon(string assetId)
    {
        try
        {
            using var stream = HostAssets.Open(assetId);
            if (stream == null) return null;
            var img = Image.FromStream(stream);
            _ownedImages.Add(img);
            return img;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatChaos(double chaos) =>
        Math.Abs(chaos) >= 1000 ? $"{chaos:N0}c" : $"{chaos:0.#}c";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var img in _ownedImages) img.Dispose();
            _ownedImages.Clear();
        }
        base.Dispose(disposing);
    }

    private sealed class KpiCard : Panel
    {
        private readonly Label _primary;
        private readonly Label _secondary;
        private readonly Label _detail;

        public KpiCard(string title, Image? icon)
        {
            Dock = DockStyle.Fill;
            Height = 92;
            Margin = new Padding(0, 0, 8, 0);
            Padding = new Padding(12, 10, 12, 10);
            BackColor = HostUi.Surface;

            var header = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 20,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };

            if (icon != null)
            {
                header.Controls.Add(new PictureBox
                {
                    Image = icon,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Width = 18,
                    Height = 18,
                    Margin = new Padding(0, 0, 6, 0),
                    BackColor = Color.Transparent
                });
            }

            header.Controls.Add(new Label
            {
                Text = title,
                AutoSize = true,
                ForeColor = HostUi.Muted,
                Font = new Font("Segoe UI", 8.25f, FontStyle.Bold),
                Margin = new Padding(0, 2, 0, 0),
                BackColor = Color.Transparent
            });

            _primary = new Label
            {
                Text = "0",
                Dock = DockStyle.Top,
                Height = 28,
                ForeColor = HostUi.TextColor,
                Font = new Font("Segoe UI", 14.5f, FontStyle.Bold),
                BackColor = Color.Transparent
            };

            _secondary = new Label
            {
                Text = "—",
                Dock = DockStyle.Top,
                Height = 18,
                ForeColor = HostUi.Accent,
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                BackColor = Color.Transparent
            };

            _detail = new Label
            {
                Text = "",
                Dock = DockStyle.Top,
                Height = 16,
                ForeColor = HostUi.Muted,
                Font = new Font("Segoe UI", 8f, FontStyle.Regular),
                BackColor = Color.Transparent
            };

            Controls.Add(_detail);
            Controls.Add(_secondary);
            Controls.Add(_primary);
            Controls.Add(header);
        }

        public void SetValues(string primary, string secondary, string detail, Color primaryColor)
        {
            _primary.Text = primary;
            _primary.ForeColor = primaryColor;
            _secondary.Text = secondary;
            _detail.Text = detail;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(HostUi.Border, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
}

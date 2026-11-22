using PluginHost;

namespace StackedDeckTracker.UI;

/// <summary>Edits a private draft; changes take effect only when Settings is saved.</summary>
public sealed class CustomCardPricesEditor : Panel
{
    private readonly Dictionary<string, double> _draft;
    private readonly ComboBox _cardName;
    private readonly NumericUpDown _price;
    private readonly DataGridView _grid;
    private readonly Label _validation;

    public CustomCardPricesEditor(IReadOnlyDictionary<string, double> prices)
    {
        Height = 320;
        Dock = DockStyle.Top;
        Padding = new Padding(12);
        BackColor = HostUi.Surface;
        _draft = new Dictionary<string, double>(prices, StringComparer.OrdinalIgnoreCase);

        var title = new Label
        {
            Text = "Custom Card Prices (Chaos per card)", Dock = DockStyle.Top,
            Height = 26, ForeColor = HostUi.Accent
        };
        var help = new Label
        {
            Text = "Choose or type a card name. Overrides apply to profit, odds and alerts in every league.\nRemove an override to use poe.ninja again. Click Save Settings to apply changes.",
            Dock = DockStyle.Top, Height = 42, ForeColor = HostUi.Muted
        };
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 70, WrapContents = true,
            BackColor = Color.Transparent
        };
        _cardName = new ComboBox
        {
            Width = 240, DropDownStyle = ComboBoxStyle.DropDown,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems,
            BackColor = HostUi.Background, ForeColor = HostUi.TextColor,
            AccessibleName = "Card name"
        };
        _price = HostUi.Number(0, 0, 1000000000, 2);
        _price.Width = 110;
        _price.AccessibleName = "Custom price in Chaos per card";
        _validation = new Label { Dock = DockStyle.Bottom, Height = 24, ForeColor = HostUi.Muted };
        _grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true };
        var add = new LedgerButton { Text = "Add / Update", AutoSize = true, Height = 30 };
        add.Click += (_, _) =>
        {
            var name = _cardName.Text.Trim();
            if (name.Length == 0)
            {
                _validation.Text = "Enter a card name first.";
                _cardName.Focus();
                return;
            }
            _draft[name] = (double)_price.Value;
            _validation.Text = "Unsaved changes — click Save Settings to apply.";
            AddKnownCards([name]);
            RenderRows();
        };
        var remove = new LedgerButton { Text = "Remove Selected", AutoSize = true, Height = 30 };
        remove.Click += (_, _) =>
        {
            if (_grid.CurrentRow?.Cells[0].Value is not string name) return;
            _draft.Remove(name);
            _validation.Text = "Unsaved changes — click Save Settings to apply.";
            RenderRows();
        };
        bar.Controls.AddRange([_cardName, _price, add, remove]);
        HostUi.StyleTable(_grid);
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Card", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Custom Price (c)", Width = 150, ValueType = typeof(double),
            DefaultCellStyle = new DataGridViewCellStyle { Format = "0.##" }
        });
        _grid.SelectionChanged += (_, _) =>
        {
            if (_grid.CurrentRow?.Cells[0].Value is not string name || !_draft.TryGetValue(name, out var price)) return;
            _cardName.Text = name;
            _price.Value = (decimal)Math.Clamp(price, (double)_price.Minimum, (double)_price.Maximum);
        };
        Controls.Add(_grid);
        Controls.Add(_validation);
        Controls.Add(bar);
        Controls.Add(help);
        Controls.Add(title);
        AddKnownCards(_draft.Keys);
        RenderRows();
    }

    public Dictionary<string, double> ExportPrices() => new(_draft, StringComparer.OrdinalIgnoreCase);

    public void MarkSaved() => _validation.Text = "Custom card prices saved.";

    public void AddKnownCards(IEnumerable<string> names)
    {
        foreach (var name in names)
            if (!_cardName.Items.Contains(name)) _cardName.Items.Add(name);
        _cardName.Sorted = true;
    }

    private void RenderRows()
    {
        _grid.Rows.Clear();
        foreach (var (name, price) in _draft.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            _grid.Rows.Add(name, price);
    }
}

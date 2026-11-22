# Stacked Deck Tracker

A Windows Plugin Host extension for tracking Path of Exile 1 Stacked Deck openings, card pulls, session profit, observed drop odds, and drop alerts.

## Install

Download `poe.stacked-deck-tracker.plugin.zip` from [Plugin Host releases](https://github.com/yoshimtu/PluginHost-Releases/releases) and put it in Plugin Host's `plugins` directory. The host loads updates automatically. Install only one copy: use either the package or the legacy `StackedDeckTracker.plugin.dll`, never both.

Use Start Tracking or **Ctrl+Shift+F9** to start/pause. Settings, current session and history persist under `plugin-data/poe.stacked-deck-tracker` beside the host. Existing 1.0.0 data remains compatible.

## Custom card prices

In **Settings → Custom Card Prices**, choose a card from the list or type its full in-game name, enter its price in Chaos **per card**, and click **Add / Update**. Select an existing row to edit it or use **Remove Selected** to return to poe.ninja pricing. Click **Save Settings** to apply and persist changes.

Overrides take precedence over poe.ninja, survive automatic price refreshes and restarts, and apply across leagues. Prices may be zero (to value a card at zero). Cards without an override continue to use market prices. Custom prices also work before market prices load; Divine conversions still require the market exchange rate.

Saved changes immediately recalculate current-session card values, recent-drop values, profit, minimum-value filtering and observed EV. New drop alerts use these prices. Archived session totals retain the values saved at the time; the Odds & EV tab uses current prices when including historical card counts.

The deck purchase cost has its own existing custom-cost setting.

## Build and verify

Requires Windows and the .NET 10 SDK. The matching Plugin Host SDK reference is included in `sdk/PluginHost.dll`; the running host provides it at runtime and it is excluded from plugin packages.

```powershell
./Build.ps1
dotnet run --project Tests/StackedDeckTracker.Checks.csproj -c Release
```

Build outputs are in `bin/`. The checks exercise settings persistence, price precedence/removal, offline pricing, metrics, odds, recorded drop values, alert classification and the settings controls. Live game attachment requires a supported PoE client and a running host.

# Stacked Deck Tracker 1.1.0

- Set custom Chaos prices per divination card in Settings. Choose a known card or enter its full name, then add/update or remove overrides and save.
- Custom prices apply immediately to current session profit, card values, recent drops, minimum-value filtering, observed EV and new drop alerts.
- Overrides survive restarts and poe.ninja refreshes. Zero prices and cards missing from market data are supported.
- Removing an override restores poe.ninja pricing. Archived session totals and existing settings/session files remain compatible.

Install `poe.stacked-deck-tracker.plugin.zip` in the host's `plugins` folder. Use only one installation of the plugin. Packaged installations can update through Host settings → Plugin updates.

Validation: Release build with zero warnings/errors; 27 automated checks covering pricing, persistence, session calculations, alert classification and settings controls. Live PoE attachment has not been tested for this release.

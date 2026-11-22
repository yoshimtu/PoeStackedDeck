using PluginHost.Games.Poe1;
using StackedDeckTracker.Models;

namespace StackedDeckTracker.Services;

/// <summary>
/// Manages attachment to the Path of Exile 1 process via PluginHost's GameReader
/// and polls inventory + ground state on a background task.
/// </summary>
public sealed class GamePoller : IDisposable
{
    private readonly Func<int> _getPollIntervalMs;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;
    private GameReader? _reader;
    private int _attachedPid;
    private GameAttachmentStatus _lastStatus = new(AttachmentKind.WaitingForGame, "Waiting for Path of Exile...");

    public event Action<GameSnapshot>? SnapshotCaptured;
    public event Action<GameAttachmentStatus>? StatusChanged;

    public GamePoller(Func<int> getPollIntervalMs, Action<string> log)
    {
        _getPollIntervalMs = getPollIntervalMs;
        _log = log;
    }

    public void Start()
    {
        if (_loopTask != null) return;
        _loopTask = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_reader == null || !_reader.Running)
                {
                    DetachReader();
                    TryAttach();
                }

                if (_reader != null && _reader.Running)
                {
                    var view = _reader.Capture(readSellTargets: false);
                    if (view != null)
                    {
                        var league = string.IsNullOrWhiteSpace(view.League) ? null : view.League.Trim();
                        ReportStatus(new GameAttachmentStatus(
                            AttachmentKind.Attached,
                            league != null ? $"Attached to PoE ({league})" : "Attached to PoE",
                            league));

                        var snapshot = ToGameSnapshot(view);
                        SnapshotCaptured?.Invoke(snapshot);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log($"Game poll error: {ex.Message}");
                DetachReader();
                ReportStatus(new GameAttachmentStatus(AttachmentKind.Error, $"Reader error: {ex.Message}"));
                await DelaySafeAsync(2000, ct).ConfigureAwait(false);
                continue;
            }

            var delayMs = _reader != null ? Math.Clamp(_getPollIntervalMs(), 100, 2000) : 1500;
            await DelaySafeAsync(delayMs, ct).ConfigureAwait(false);
        }

        DetachReader();
    }

    private void TryAttach()
    {
        var process = GameReader.FindGame();
        if (process == null)
        {
            ReportStatus(new GameAttachmentStatus(AttachmentKind.WaitingForGame, "Waiting for Path of Exile..."));
            return;
        }

        try
        {
            _reader = new GameReader(process);
            _attachedPid = process.Id;
            _log($"Attached to Path of Exile (PID {_attachedPid}).");
            ReportStatus(new GameAttachmentStatus(AttachmentKind.Attached, "Attached to PoE"));
        }
        catch (Exception ex)
        {
            process.Dispose();
            var isUnsupported = ex.Message.Contains("profile", StringComparison.OrdinalIgnoreCase) ||
                                ex.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
                                ex.Message.Contains("hash", StringComparison.OrdinalIgnoreCase) ||
                                ex.Message.Contains("SHA", StringComparison.OrdinalIgnoreCase);

            var kind = isUnsupported ? AttachmentKind.UnsupportedBuild : AttachmentKind.Error;
            var msg = isUnsupported
                ? $"Unsupported PoE build: {ex.Message}"
                : $"Could not attach to PoE: {ex.Message}";

            _log(msg);
            ReportStatus(new GameAttachmentStatus(kind, msg));
        }
    }

    private void DetachReader()
    {
        if (_reader == null) return;
        try { _reader.Dispose(); } catch { /* ignore cleanup errors */ }
        _reader = null;
        _attachedPid = 0;
    }

    private void ReportStatus(GameAttachmentStatus status)
    {
        if (status == _lastStatus) return;
        _lastStatus = status;
        StatusChanged?.Invoke(status);
    }

    private static GameSnapshot ToGameSnapshot(GameView view)
    {
        var inv = view.Inventory != null
            ? view.Inventory
                .Where(p => p?.Item != null)
                .Select(p => new ObservedItem(p.Item.Name ?? "", p.Item.Count, p.Item.Kind ?? ""))
                .ToArray()
            : Array.Empty<ObservedItem>();

        var ground = view.Ground != null
            ? view.Ground
                .Where(i => i != null)
                .Select(i => new ObservedItem(i.Name ?? "", i.Count, i.Kind ?? ""))
                .ToArray()
            : Array.Empty<ObservedItem>();

        var isStashOpen = view.Stash != null || !string.IsNullOrEmpty(view.OpenScope);
        return new GameSnapshot(view.League ?? "", isStashOpen, inv, ground, view.Window);
    }

    private static async Task DelaySafeAsync(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        DetachReader();
        _cts.Dispose();
    }
}

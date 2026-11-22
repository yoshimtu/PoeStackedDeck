using System.Media;
using StackedDeckTracker.Models;
using StackedDeckTracker.UI;

namespace StackedDeckTracker.Services;

public enum DropAlertTier
{
    None,
    Low,
    Mid,
    High
}

/// <summary>
/// Plays synthesized drop-tier chimes (Low / Mid / High) and triggers click-through
/// PoE-window overlay popups for Mid and High value divination card drops.
/// </summary>
public sealed class DropAlertService : IDisposable
{
    private readonly byte[] _lowWav;
    private readonly byte[] _midWav;
    private readonly byte[] _highWav;
    private DropAlertOverlay? _activeOverlay;

    public DropAlertService()
    {
        _lowWav = BuildLowChimeWav();
        _midWav = BuildMidTinkWav();
        _highWav = BuildHighDopamineTinkWav();
    }

    public static DropAlertTier Classify(double chaosValue, TrackerSettings settings)
    {
        if (!settings.AlertsEnabled || chaosValue <= 0) return DropAlertTier.None;
        if (settings.HighAlertChaos > 0 && chaosValue >= settings.HighAlertChaos) return DropAlertTier.High;
        if (settings.MidAlertChaos > 0 && chaosValue >= settings.MidAlertChaos) return DropAlertTier.Mid;
        if (settings.LowAlertChaos > 0 && chaosValue >= settings.LowAlertChaos) return DropAlertTier.Low;
        return DropAlertTier.None;
    }

    /// <summary>
    /// Must be called on the WinForms UI thread.
    /// </summary>
    public void Trigger(DropAlertTier tier, string cardName, double chaosValue, double divineValue, IntPtr poeWindow)
    {
        if (tier == DropAlertTier.None) return;

        PlaySound(tier);

        if (tier is DropAlertTier.Mid or DropAlertTier.High)
        {
            ShowOverlay(tier, cardName, chaosValue, divineValue, poeWindow);
        }
    }

    private void PlaySound(DropAlertTier tier)
    {
        var wav = tier switch
        {
            DropAlertTier.Low => _lowWav,
            DropAlertTier.Mid => _midWav,
            DropAlertTier.High => _highWav,
            _ => null
        };
        if (wav == null) return;

        _ = Task.Run(() =>
        {
            try
            {
                using var ms = new MemoryStream(wav, writable: false);
                using var player = new SoundPlayer(ms);
                player.PlaySync();
            }
            catch
            {
                // Ignore audio device errors
            }
        });
    }

    private void ShowOverlay(DropAlertTier tier, string cardName, double chaosValue, double divineValue, IntPtr poeWindow)
    {
        try
        {
            if (_activeOverlay is { IsDisposed: false })
            {
                _activeOverlay.Close();
                _activeOverlay.Dispose();
            }

            _activeOverlay = new DropAlertOverlay(tier, cardName, chaosValue, divineValue, poeWindow);
            _activeOverlay.Show();
        }
        catch
        {
            // Ignore overlay display errors
        }
    }

    /// <summary>
    /// Low tier (10c+): subtle soft bell ping (~220ms).
    /// </summary>
    private static byte[] BuildLowChimeWav()
    {
        return SynthesizeWav(0.24, t =>
        {
            var env = Math.Exp(-t * 16.0) * Math.Min(1.0, t / 0.005);
            var s = 0.65 * Math.Sin(2 * Math.PI * 880 * t)
                  + 0.25 * Math.Sin(2 * Math.PI * 1760 * t);
            return s * env * 0.35;
        });
    }

    /// <summary>
    /// Mid tier (60c+): crisp PoE-style metallic "tink" (~550ms).
    /// </summary>
    private static byte[] BuildMidTinkWav()
    {
        return SynthesizeWav(0.58, t =>
        {
            var attack = Math.Min(1.0, t / 0.002);
            var envFast = Math.Exp(-t * 9.5) * attack;
            var envTail = Math.Exp(-t * 5.2) * attack;
            var strike = 0.55 * Math.Sin(2 * Math.PI * 1567.98 * t)   // G6
                       + 0.35 * Math.Sin(2 * Math.PI * 3135.96 * t)   // G7
                       + 0.22 * Math.Sin(2 * Math.PI * 2349.32 * t);  // D7
            var shimmer = 0.25 * Math.Sin(2 * Math.PI * 1174.66 * t); // D6
            return (strike * envFast + shimmer * envTail) * 0.50;
        });
    }

    /// <summary>
    /// High tier (100c+): bright dopamine-inducing layered "TINK-SHING!" (~1.25s).
    /// </summary>
    private static byte[] BuildHighDopamineTinkWav()
    {
        return SynthesizeWav(1.25, t =>
        {
            var attack1 = Math.Min(1.0, t / 0.0015);
            var env1 = Math.Exp(-t * 4.2) * attack1;
            var tink = 0.50 * Math.Sin(2 * Math.PI * 2093.00 * t)     // C7
                     + 0.38 * Math.Sin(2 * Math.PI * 3135.96 * t)     // G7
                     + 0.28 * Math.Sin(2 * Math.PI * 4186.01 * t)     // C8
                     + 0.18 * Math.Sin(2 * Math.PI * 2637.02 * t);    // E7

            // Second crystalline bloom 75ms after initial strike
            double bloom = 0;
            if (t >= 0.075)
            {
                var tb = t - 0.075;
                var env2 = Math.Exp(-tb * 3.1) * Math.Min(1.0, tb / 0.008);
                bloom = (0.35 * Math.Sin(2 * Math.PI * 1046.50 * tb)  // C6
                       + 0.30 * Math.Sin(2 * Math.PI * 1567.98 * tb)  // G6
                       + 0.25 * Math.Sin(2 * Math.PI * 2093.00 * tb)  // C7
                       + 0.20 * Math.Sin(2 * Math.PI * 2637.02 * tb)) // E7
                       * env2;
            }

            return Math.Tanh((tink * env1 + bloom) * 0.72) * 0.65;
        });
    }

    private static byte[] SynthesizeWav(double durationSeconds, Func<double, double> sampleAt)
    {
        const int sampleRate = 44100;
        int sampleCount = (int)(sampleRate * durationSeconds);
        int dataBytes = sampleCount * 2; // 16-bit mono

        using var ms = new MemoryStream(44 + dataBytes);
        using var bw = new BinaryWriter(ms);

        // RIFF WAV header
        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);              // PCM chunk size
        bw.Write((short)1);        // PCM format
        bw.Write((short)1);        // Mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);  // Byte rate
        bw.Write((short)2);        // Block align
        bw.Write((short)16);       // Bits per sample
        bw.Write("data"u8);
        bw.Write(dataBytes);

        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            double v = Math.Clamp(sampleAt(t), -1.0, 1.0);
            bw.Write((short)(v * short.MaxValue));
        }

        return ms.ToArray();
    }

    public void Dispose()
    {
        try
        {
            if (_activeOverlay is { IsDisposed: false })
            {
                _activeOverlay.Close();
                _activeOverlay.Dispose();
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}

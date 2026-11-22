using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using PluginHost;
using StackedDeckTracker.Services;

namespace StackedDeckTracker.UI;

/// <summary>
/// Click-through, non-activating overlay popup centered on the Path of Exile window.
/// Mid tier displays a clean toast; High tier stays 3 seconds then fades out with animated confetti.
/// </summary>
public sealed class DropAlertOverlay : Form
{
    private const int WsExLayered = 0x80000;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;

    private readonly DropAlertTier _tier;
    private readonly string _cardName;
    private readonly string _valueText;
    private readonly DateTime _createdAt;
    private readonly double _holdSeconds;
    private readonly double _fadeSeconds;
    private readonly System.Windows.Forms.Timer _animTimer;
    private readonly List<ConfettiPiece> _confetti = new();
    private readonly Image? _icon;

    private sealed class ConfettiPiece
    {
        public float X, Y, Vx, Vy, Size, Angle, Spin;
        public Color Color;
    }

    public DropAlertOverlay(DropAlertTier tier, string cardName, double chaosValue, double divineValue, IntPtr poeWindow)
    {
        _tier = tier;
        _cardName = cardName;
        _valueText = divineValue >= 0.1
            ? $"{chaosValue:0.#}c   •   {divineValue:0.00} div"
            : $"{chaosValue:0.#}c";
        _createdAt = DateTime.UtcNow;
        _holdSeconds = tier == DropAlertTier.High ? 3.0 : 2.0;
        _fadeSeconds = tier == DropAlertTier.High ? 0.9 : 0.6;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(1, 2, 3);
        TransparencyKey = BackColor;

        Size = tier == DropAlertTier.High ? new Size(460, 210) : new Size(360, 96);
        Location = ComputeCenteredLocation(poeWindow, Size);

        try
        {
            using var s = HostAssets.Open(tier == DropAlertTier.High ? "Currency/Divine Orb.png" : "Currency/Stacked Deck.png");
            if (s != null) _icon = Image.FromStream(s);
        }
        catch { /* optional icon */ }

        if (tier == DropAlertTier.High)
        {
            SpawnConfetti();
        }

        _animTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _animTimer.Tick += OnAnimationTick;
        _animTimer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate;
            return cp;
        }
    }

    private void SpawnConfetti()
    {
        var rng = Random.Shared;
        Color[] palette =
        [
            HostUi.Accent,
            Color.FromArgb(255, 224, 130),
            Color.FromArgb(118, 214, 149),
            Color.FromArgb(245, 130, 110),
            Color.FromArgb(240, 240, 230)
        ];

        float cx = Width / 2f;
        float cy = Height / 2f;
        for (int i = 0; i < 42; i++)
        {
            var angle = rng.NextDouble() * Math.PI * 2;
            var speed = 1.6 + rng.NextDouble() * 4.4;
            _confetti.Add(new ConfettiPiece
            {
                X = cx + (float)((rng.NextDouble() - 0.5) * 140),
                Y = cy + (float)((rng.NextDouble() - 0.5) * 30),
                Vx = (float)(Math.Cos(angle) * speed),
                Vy = (float)(Math.Sin(angle) * speed - 2.4),
                Size = (float)(5 + rng.NextDouble() * 5),
                Angle = (float)(rng.NextDouble() * 360),
                Spin = (float)((rng.NextDouble() - 0.5) * 14),
                Color = palette[i % palette.Length]
            });
        }
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        var elapsed = (DateTime.UtcNow - _createdAt).TotalSeconds;
        var total = _holdSeconds + _fadeSeconds;

        if (elapsed >= total)
        {
            _animTimer.Stop();
            Close();
            return;
        }

        if (elapsed > _holdSeconds)
        {
            var fadeProgress = (elapsed - _holdSeconds) / _fadeSeconds;
            Opacity = Math.Clamp(1.0 - fadeProgress, 0.0, 1.0);
        }

        if (_confetti.Count > 0)
        {
            foreach (var c in _confetti)
            {
                c.X += c.Vx;
                c.Y += c.Vy;
                c.Vy += 0.09f; // gentle gravity
                c.Vx *= 0.985f;
                c.Angle += c.Spin;
            }
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Draw confetti behind/around the card banner for High tier
        foreach (var c in _confetti)
        {
            if (c.X < -10 || c.X > Width + 10 || c.Y < -10 || c.Y > Height + 10) continue;
            var state = g.Save();
            g.TranslateTransform(c.X, c.Y);
            g.RotateTransform(c.Angle);
            using var brush = new SolidBrush(c.Color);
            g.FillRectangle(brush, -c.Size / 2f, -c.Size / 3f, c.Size, c.Size * 0.65f);
            g.Restore(state);
        }

        var bannerRect = _tier == DropAlertTier.High
            ? new Rectangle(38, 48, Width - 76, 114)
            : new Rectangle(8, 8, Width - 16, Height - 16);

        var bg = _tier == DropAlertTier.High ? Color.FromArgb(242, 18, 30, 32) : Color.FromArgb(238, 22, 38, 40);
        var borderCol = _tier == DropAlertTier.High ? HostUi.Accent : HostUi.Border;

        using (var bgBrush = new SolidBrush(bg))
        using (var borderPen = new Pen(borderCol, _tier == DropAlertTier.High ? 2.5f : 1.5f))
        {
            g.FillRectangle(bgBrush, bannerRect);
            g.DrawRectangle(borderPen, bannerRect);
        }

        if (_tier == DropAlertTier.High)
        {
            // Inner subtle gold accent rule
            using var innerPen = new Pen(Color.FromArgb(120, HostUi.Accent), 1f);
            g.DrawRectangle(innerPen, Rectangle.Inflate(bannerRect, -5, -5));
        }

        int textLeft = bannerRect.Left + 16;
        if (_icon != null)
        {
            int iconSize = _tier == DropAlertTier.High ? 48 : 36;
            var iconRect = new Rectangle(bannerRect.Left + 16, bannerRect.Top + (bannerRect.Height - iconSize) / 2, iconSize, iconSize);
            g.DrawImage(_icon, iconRect);
            textLeft = iconRect.Right + 14;
        }

        var textRect = new Rectangle(textLeft, bannerRect.Top + 10, bannerRect.Right - textLeft - 14, bannerRect.Height - 20);
        var headerText = _tier == DropAlertTier.High ? "★  HIGH VALUE DROP  ★" : "DIVINATION CARD DROP";

        using var headerFont = new Font("Segoe UI", _tier == DropAlertTier.High ? 8.5f : 7.5f, FontStyle.Bold);
        using var nameFont = new Font("Georgia", _tier == DropAlertTier.High ? 15f : 12.5f, FontStyle.Bold);
        using var valFont = new Font("Segoe UI", _tier == DropAlertTier.High ? 11.5f : 9.5f, FontStyle.Bold);
        using var headerBrush = new SolidBrush(_tier == DropAlertTier.High ? HostUi.Accent : HostUi.Muted);
        using var nameBrush = new SolidBrush(HostUi.TextColor);
        using var valBrush = new SolidBrush(Color.FromArgb(118, 214, 149));

        g.DrawString(headerText, headerFont, headerBrush, textRect.Left, textRect.Top);
        g.DrawString(_cardName, nameFont, nameBrush, new RectangleF(textRect.Left, textRect.Top + 18, textRect.Width, 34));
        g.DrawString(_valueText, valFont, valBrush, textRect.Left, textRect.Bottom - (_tier == DropAlertTier.High ? 26 : 20));
    }

    private static Point ComputeCenteredLocation(IntPtr poeWindow, Size popupSize)
    {
        if (poeWindow != IntPtr.Zero && GetWindowRect(poeWindow, out var rect) &&
            rect.Right - rect.Left > 100 && rect.Bottom - rect.Top > 100)
        {
            int x = rect.Left + (rect.Right - rect.Left - popupSize.Width) / 2;
            int y = rect.Top + (rect.Bottom - rect.Top - popupSize.Height) / 2;
            return new Point(x, y);
        }

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        return new Point(
            area.Left + (area.Width - popupSize.Width) / 2,
            area.Top + (area.Height - popupSize.Height) / 2);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animTimer.Stop();
            _animTimer.Dispose();
            _icon?.Dispose();
        }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}

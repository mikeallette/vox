using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Vox.Core;

namespace Vox.UI;

/// <summary>
/// The Vox bar: a small always-on-top pill, a white note-card capsule with an ink rim that grows into a live ink line while
/// you talk. Click-through and non-activating, so it never steals focus from the app you are typing in.
/// States: resting (tiny capsule), listening, hands-free (inverted ink), quiet mic (amber), writing (pen + dots),
/// written (green tick) and notices.
/// </summary>
public partial class VoxBar : Window
{
    const int Points = 32;
    const double WaveW = 110, WaveH = 24;
    readonly float[] _hist = new float[Points];
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly Random _rng = new();
    float _target, _smooth;
    VoxState _state = VoxState.Idle;
    int _frame;
    public bool AlwaysVisible { get; set; } = true;

    static readonly Color Ink = Color.FromRgb(0x1F, 0x3A, 0x5F), PaperC = Color.FromRgb(0xF7, 0xF4, 0xEB), Yellow = Color.FromRgb(0xF2, 0xC1, 0x4E),
        RedC = Color.FromRgb(0xB4, 0x43, 0x2F), AmberC = Color.FromRgb(0xB7, 0x79, 0x1F), AmberBg = Color.FromRgb(0xFF, 0xF7, 0xE6), AmberText = Color.FromRgb(0x7A, 0x4F, 0x0E),
        GreenC = Color.FromRgb(0x2E, 0x7D, 0x4F), GreenText = Color.FromRgb(0x1F, 0x5C, 0x38), NoteBg = Color.FromRgb(0xFF, 0xFD, 0xF6);
    static readonly Geometry PenIcon = Geometry.Parse("M 12,20 L 21,20 M 16.5,3.5 A 2.1,2.1 0 0 1 19.5,6.5 L 7,19 L 3,20 L 4,16 Z");
    static readonly Geometry TickIcon = Geometry.Parse("M 20,6 L 9,17 L 4,12");

    public VoxBar()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => Animate();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT));
            Reposition();
        };
        SystemParameters.StaticPropertyChanged += (_, _) => Dispatcher.Invoke(Reposition);
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Reposition);   // landed on a monitor with another scale: re-center
        _follow.Tick += (_, _) => FollowActiveScreen();
        _follow.Start();
    }

    IntPtr _monitor;
    readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(400) };

    /// <summary>
    /// Centers the bar at the bottom of the screen you are working on (the monitor of the active window).
    /// Works in physical pixels so mixed-DPI monitors line up; WPF rescales the window when it lands on a
    /// monitor with a different DPI, and DpiChanged calls this again to re-center it.
    /// </summary>
    void Reposition()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero)
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - Width) / 2; Top = wa.Bottom - Height;
            return;
        }
        try
        {
            var mon = Native.ActiveMonitor(h);
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            if (mon == IntPtr.Zero || !Native.GetMonitorInfo(mon, ref mi)) return;
            double scale = Native.GetDpiForMonitor(mon, 0, out var dpi, out _) == 0 ? dpi / 96.0 : VisualTreeHelper.GetDpi(this).DpiScaleX;
            int w = (int)Math.Round(Width * scale), ht = (int)Math.Round(Height * scale);
            var r = mi.rcWork;
            int x = r.Left + (r.Right - r.Left - w) / 2, y = r.Bottom - ht;
            _monitor = mon;
            Native.SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        catch (Exception ex) { Log.Error("bar position", ex); }
    }

    /// <summary>Moves the bar when you switch to a window on another screen (checked a few times a second; cheap).</summary>
    void FollowActiveScreen()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero || !IsVisible) return;
        if (Native.ActiveMonitor(h) != _monitor) Reposition();
    }

    public void PushLevel(float rms) => _target = (float)Math.Min(1.0, Math.Sqrt(rms) * 2.4);

    public void SetState(VoxState st, string message)
    {
        _state = st;
        if (st == VoxState.Idle && !AlwaysVisible) { Fade(0, () => Hide()); _tick.Stop(); return; }
        Pill.BeginAnimation(OpacityProperty, null);   // clear any fade-out that is still holding opacity at 0
        Pill.Opacity = 1;
        if (!IsVisible) Show();
        Reposition();

        const double badge = 7 + 26 + 10, wave = WaveW + 10, dots = 28 + 10, pad = 18;
        switch (st)
        {
            case VoxState.Idle:
                _tick.Stop();
                FadeContent(0); Pill.CornerRadius = new CornerRadius(6); Paint(Colors.White, Ink);
                Resize(64, 12);
                break;
            case VoxState.Listening:
                if (message == DictationController.QuietLabel)
                {   // the mic is too quiet: amber, never red; Vox still types what it hears
                    Paint(AmberBg, AmberC); SetBadge(AmberC, null, bang: true); WaveLine.Stroke = new SolidColorBrush(AmberC);
                    ShowContent("mic is quiet, move closer", AmberText, wave: true);
                }
                else if (message.StartsWith("Hands", StringComparison.OrdinalIgnoreCase))
                {   // hands-free: inverted ink, so it is obvious your hands are free
                    Paint(Ink, Ink); SetBadge(PaperC, RedC); WaveLine.Stroke = new SolidColorBrush(PaperC);
                    ShowContent("hands-free", PaperC, wave: true);
                }
                else
                {
                    Paint(Colors.White, Ink); SetBadge(Ink, Yellow); WaveLine.Stroke = new SolidColorBrush(Ink);
                    ShowContent("listening…", Ink, wave: true);
                }
                Resize(MeasureWidth(Label.Text, badge + wave + pad), 40);
                _tick.Start();
                break;
            case VoxState.Processing:
                Paint(Colors.White, Ink); SetBadge(Ink, null, PenIcon);
                ShowContent(message.StartsWith("Re-", StringComparison.OrdinalIgnoreCase) ? "listening again…" : "writing it down…", Ink, wave: false, dotsOn: true);
                Resize(MeasureWidth(Label.Text, badge + dots + pad), 40);
                _tick.Start();
                break;
            case VoxState.Done:
                Paint(Colors.White, GreenC); SetBadge(GreenC, null, TickIcon);
                ShowContent(message.Length > 0 && char.IsDigit(message[0]) ? "written · " + message.Replace("s", " s") : message.ToLowerInvariant(), GreenText, wave: false);
                Resize(MeasureWidth(Label.Text, badge + pad), 40);
                _tick.Stop();
                break;
            case VoxState.Notice:
                bool warn = message.Contains("quiet", StringComparison.OrdinalIgnoreCase) || message.Contains("Admin", StringComparison.OrdinalIgnoreCase);
                Paint(warn ? AmberBg : NoteBg, warn ? AmberC : Ink);
                Badge.Visibility = Visibility.Collapsed;
                ShowContent(message, warn ? AmberText : Ink, wave: false);
                Resize(Math.Min(440, MeasureWidth(message, 18 + pad)), 40);
                _tick.Stop();
                break;
        }
    }

    void Paint(Color background, Color rim)
    {
        Pill.Background = new SolidColorBrush(background);
        Pill.BorderBrush = new SolidColorBrush(rim);
        Pill.CornerRadius = new CornerRadius(20);
    }

    void SetBadge(Color circle, Color? dot, Geometry? icon = null, bool bang = false)
    {
        Badge.Visibility = Visibility.Visible;
        BadgeCircle.Fill = new SolidColorBrush(circle);
        BadgeDot.Visibility = dot.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (dot.HasValue) BadgeDot.Fill = new SolidColorBrush(dot.Value);
        BadgeIcon.Visibility = icon != null ? Visibility.Visible : Visibility.Collapsed;
        if (icon != null) BadgeIcon.Data = icon;
        BadgeBang.Visibility = bang ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowContent(string text, Color color, bool wave, bool dotsOn = false)
    {
        Label.Text = text;
        Label.Foreground = new SolidColorBrush(color);
        Wave.Visibility = wave ? Visibility.Visible : Visibility.Collapsed;
        Dots.Visibility = dotsOn ? Visibility.Visible : Visibility.Collapsed;
        FadeContent(1);
    }

    double MeasureWidth(string text, double extra)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Label.FontFamily, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal), Label.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return Math.Ceiling(ft.Width + extra + 4);
    }

    void Resize(double w, double h)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        Pill.BeginAnimation(HeightProperty, new DoubleAnimation(h, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    void FadeContent(double to) => Inner.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(140)));

    void Fade(double to, Action done)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(160));
        a.Completed += (_, _) => done();
        Pill.BeginAnimation(OpacityProperty, a);
    }

    void Animate()
    {
        _frame++;
        if (_state == VoxState.Processing)
        {   // "writing it down…": three ink dots that brighten in turn
            var dots = new[] { Dot1, Dot2, Dot3 };
            for (int i = 0; i < 3; i++) dots[i].Opacity = 0.25 + 0.75 * Math.Max(0, Math.Sin(_frame / 4.0 - i * 0.9));
            return;
        }
        _smooth = _smooth * 0.55f + _target * 0.45f;
        float level = _smooth;
        _target *= 0.85f;
        BadgeDot.Opacity = 0.65 + 0.35 * Math.Sin(_frame / 5.0);

        // an ink line drawn by your voice: newest sample on the right, alternating up/down like a pen trace
        Array.Copy(_hist, 1, _hist, 0, Points - 1);
        _hist[Points - 1] = level * (0.6f + 0.4f * (float)_rng.NextDouble());
        var pts = new PointCollection(Points);
        for (int i = 0; i < Points; i++)
        {
            double edge = 0.3 + 0.7 * Math.Sin(Math.PI * (i + 0.5) / Points);    // taper toward the ends
            double amp = (1.2 + _hist[i] * 10.5) * edge * (i % 2 == 0 ? 1 : -1);
            pts.Add(new Point(i * (WaveW / (Points - 1)), WaveH / 2 + amp));
        }
        WaveLine.Points = pts;
    }
}

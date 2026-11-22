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
/// The small always-on-top pill (Wispr's "Flow Bar"): a tiny idle capsule that expands into a live
/// waveform while listening. Click-through and non-activating, so it never steals focus from the app you are typing in.
/// </summary>
public partial class FlowBar : Window
{
    const int Bars = 28;
    readonly Rectangle[] _bars = new Rectangle[Bars];
    readonly float[] _hist = new float[Bars];
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly Random _rng = new();
    float _target, _smooth;
    VoxState _state = VoxState.Idle;
    int _frame;
    public bool AlwaysVisible { get; set; } = true;

    public FlowBar()
    {
        InitializeComponent();
        for (int i = 0; i < Bars; i++)
        {
            var r = new Rectangle { Width = 2.5, Height = 3, RadiusX = 1.25, RadiusY = 1.25, Fill = new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF5)) };
            Canvas.SetLeft(r, i * 4);
            Canvas.SetTop(r, 10.5);
            Wave.Children.Add(r);
            _bars[i] = r;
        }
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
    /// Centers the bar at the bottom of the screen you are working on (the monitor of the active window), like Wispr
    /// Flow does. Works in physical pixels so mixed-DPI monitors line up; WPF rescales the window when it lands on a
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
        catch (Exception ex) { Log.Error("flow bar position", ex); }
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

        switch (st)
        {
            case VoxState.Idle:
                _tick.Stop();
                FadeContent(0); Resize(48, 10);
                break;
            case VoxState.Listening:
                Label.Text = message; Dot.Visibility = Visibility.Visible; Wave.Visibility = Visibility.Visible;
                // amber dot while the mic is too quiet (DictationController.QuietLabel), red otherwise
                Dot.Fill = new SolidColorBrush(message == DictationController.QuietLabel ? Color.FromRgb(0xF5, 0xB9, 0x42) : Color.FromRgb(0xFF, 0x5A, 0x4F));
                Resize(MeasureWidth(message, 112 + 19 + 28), 38); FadeContent(1);
                _tick.Start();
                break;
            case VoxState.Processing:
                Label.Text = message; Dot.Visibility = Visibility.Visible; Wave.Visibility = Visibility.Visible;
                Dot.Fill = new SolidColorBrush(Color.FromRgb(0xF5, 0xB9, 0x42));
                Resize(MeasureWidth(message, 112 + 19 + 28), 38); FadeContent(1);
                _tick.Start();
                break;
            case VoxState.Done:
                Label.Text = "✓  " + message; Dot.Visibility = Visibility.Collapsed; Wave.Visibility = Visibility.Collapsed;
                Resize(MeasureWidth(Label.Text, 28), 38); FadeContent(1);
                _tick.Stop();
                break;
            case VoxState.Notice:
                Label.Text = message; Dot.Visibility = Visibility.Collapsed; Wave.Visibility = Visibility.Collapsed;
                Resize(Math.Min(340, MeasureWidth(message, 28)), 38); FadeContent(1);
                _tick.Stop();
                break;
        }
    }

    double MeasureWidth(string text, double extra)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI Semibold"), 13, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return Math.Ceiling(ft.Width + extra + 2);
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
        float level;
        if (_state == VoxState.Listening)
        {
            _smooth = _smooth * 0.55f + _target * 0.45f;
            level = _smooth;
            _target *= 0.85f;
            Dot.Opacity = 0.65 + 0.35 * Math.Sin(_frame / 5.0);
        }
        else
        {
            level = 0.18f + 0.12f * (float)Math.Sin(_frame / 4.0);   // gentle "working" shimmer
            Dot.Opacity = 1;
        }
        Array.Copy(_hist, 1, _hist, 0, Bars - 1);
        _hist[Bars - 1] = level * (0.7f + 0.3f * (float)_rng.NextDouble());
        for (int i = 0; i < Bars; i++)
        {
            double edge = 0.35 + 0.65 * Math.Sin(Math.PI * (i + 0.5) / Bars);     // taper toward the ends
            double h = 3 + _hist[i] * 20 * edge;
            _bars[i].Height = h;
            Canvas.SetTop(_bars[i], (24 - h) / 2);
        }
    }
}

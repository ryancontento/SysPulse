using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Extensions.DependencyInjection;
using SysPulse.App.Components;
using SysPulse.Core.Models;
using SysPulse.Core.Power;
using SysPulse.Core.Rules;
using SysPulse.Core.Services;
using SysPulse.Core.Settings;

namespace SysPulse.App;

/// <summary>The always-on-top mini widget, styled like a 1U rack panel.</summary>
public partial class WidgetWindow : Window
{
    private const int Segments = 12;

    // The last segments light red, like the top of a VU meter.
    private const int HotSegments = 2;

    // "--" placeholders for missing sensors are dimmed so they don't read like values.
    private const double UnavailableOpacity = 0.35;

    private static readonly Brush SegmentOff = Frozen(Color.FromRgb(0x2A, 0x27, 0x21));
    private static readonly Brush SegmentHot = Frozen(Color.FromRgb(0xE5, 0x58, 0x4A));
    private static readonly Brush LampWarn = Frozen(Color.FromRgb(0xF0, 0xA3, 0x3A));

    private readonly IMetricsSource _metrics;
    private readonly ISettingsService _settings;
    private readonly IRuleEngine _rules;
    private readonly IPowerProfileService _power;
    private Brush _accent = Frozen(Color.FromRgb(0xF0, 0xA3, 0x3A));

    public WidgetWindow(IServiceProvider services)
    {
        InitializeComponent();

        _metrics = services.GetRequiredService<IMetricsSource>();
        _settings = services.GetRequiredService<ISettingsService>();
        _rules = services.GetRequiredService<IRuleEngine>();
        _power = services.GetRequiredService<IPowerProfileService>();

        foreach (var bar in new[] { CpuBar, GpuBar, RamBar })
        {
            bar.Columns = Segments;
            for (var i = 0; i < Segments; i++)
                bar.Children.Add(new Rectangle { Margin = new Thickness(0, 0, 1.5, 0), Fill = SegmentOff });
        }

        ApplyAccent(_settings.Current);
        Display(_metrics.Current);

        _metrics.Updated += OnMetricsUpdated;
        _settings.Changed += OnSettingsChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        _metrics.Updated -= OnMetricsUpdated;
        _settings.Changed -= OnSettingsChanged;
        base.OnClosed(e);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var settings = _settings.Current;
        if (settings.WidgetLeft is { } left && settings.WidgetTop is { } top && IsOnScreen(left, top))
        {
            Left = left;
            Top = top;
        }
        else
        {
            // Top centre of the primary screen's work area.
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Top;
        }
    }

    // A saved position can end up off screen after a monitor is unplugged.
    private bool IsOnScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - ActualWidth / 2
        && left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - ActualWidth / 2
        && top >= SystemParameters.VirtualScreenTop
        && top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 30;

    private void OnMetricsUpdated(SystemSnapshot snapshot) => Dispatcher.InvokeAsync(() => Display(snapshot));

    private void OnSettingsChanged(AppSettings settings) => Dispatcher.InvokeAsync(() => ApplyAccent(settings));

    private void ApplyAccent(AppSettings settings)
    {
        var color = AccentPalette.For(settings.Accent);
        _accent = Frozen(color);
        Resources["WidgetAccent"] = _accent;
        PowerLampGlow.Color = color;
    }

    private void Display(SystemSnapshot snapshot)
    {
        if (!snapshot.HasData)
            return;

        CpuLoad.Text = Whole(snapshot.Cpu.LoadPercent);
        CpuTemp.Text = Temperature(snapshot.Cpu.TemperatureC);
        CpuTemp.Opacity = snapshot.Cpu.TemperatureC is null ? UnavailableOpacity : 1;
        SetBar(CpuBar, snapshot.Cpu.LoadPercent);

        GpuLoad.Text = Whole(snapshot.Gpu.LoadPercent);
        GpuTemp.Text = Temperature(snapshot.Gpu.TemperatureC);
        GpuTemp.Opacity = snapshot.Gpu.TemperatureC is null ? UnavailableOpacity : 1;
        SetBar(GpuBar, snapshot.Gpu.LoadPercent);

        var (used, unit) = Units.Size(snapshot.Memory.UsedBytes, minUnit: 3);
        RamLoad.Text = Whole(snapshot.Memory.LoadPercent);
        RamUsed.Text = $"{used} {unit}";
        SetBar(RamBar, snapshot.Memory.LoadPercent);

        NetDown.Text = "▼ " + Rate(snapshot.Network.DownloadBytesPerSec);
        NetUp.Text = "▲ " + Rate(snapshot.Network.UploadBytesPerSec);

        AlertLamp.Fill = _settings.Current.Rules.Any(r => _rules.IsActive(r.Id)) ? _accent : SegmentOff;

        // Red for thermal throttling, amber for power or other limits.
        var cpuThrottle = snapshot.Cpu.Throttle;
        var gpuThrottle = snapshot.Gpu.Throttle;
        ThrottleLamp.Fill = cpuThrottle == ThrottleReason.Thermal || gpuThrottle == ThrottleReason.Thermal ? SegmentHot
            : cpuThrottle is ThrottleReason.Power or ThrottleReason.Limited || gpuThrottle is ThrottleReason.Power or ThrottleReason.Limited ? LampWarn
            : SegmentOff;
        ThrottleLamp.ToolTip = $"CPU: {Describe(cpuThrottle)}\nGPU: {Describe(gpuThrottle)}";

        ProfileText.Text = (CurrentProfile()?.ToString() ?? "Custom plan").ToUpperInvariant();
    }

    private static string Describe(ThrottleReason? reason) => reason switch
    {
        null => "unknown",
        ThrottleReason.None => "full speed",
        ThrottleReason.Thermal => "thermal throttling",
        ThrottleReason.Power => "at its power limit",
        _ => "speed limited",
    };

    private PowerProfile? CurrentProfile()
    {
        try
        {
            return _power.Current;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void SetBar(UniformGrid bar, double percent)
    {
        var lit = (int)Math.Round(Math.Clamp(percent, 0, 100) / 100 * Segments);
        for (var i = 0; i < Segments; i++)
            ((Rectangle)bar.Children[i]).Fill = i >= lit ? SegmentOff : i >= Segments - HotSegments ? SegmentHot : _accent;
    }

    private string Temperature(double? celsius) =>
        celsius is not { } c ? "--"
        : _settings.Current.TemperatureUnit == TemperatureUnit.Fahrenheit ? $"{Whole(c * 9 / 5 + 32)}°F"
        : $"{Whole(c)}°C";

    private static string Rate(double bytesPerSec)
    {
        var (value, unit) = Units.Size(bytesPerSec, minUnit: 1);
        return $"{value} {unit}/s";
    }

    private static string Whole(double value) => Math.Round(value).ToString(CultureInfo.CurrentCulture);

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OpenMainWindow();
            return;
        }

        // DragMove returns once the button is released.
        DragMove();
        _settings.Update(s => s with { WidgetLeft = Left, WidgetTop = Top });
    }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenMainWindow();

    private void Topmost_Click(object sender, RoutedEventArgs e) => Topmost = TopmostItem.IsChecked;

    private void Hide_Click(object sender, RoutedEventArgs e) => _settings.Update(s => s with { ShowWidget = false });

    private static void OpenMainWindow() => (Application.Current.MainWindow as MainWindow)?.BringToFront();
}

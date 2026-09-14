using System.Windows.Media;
using SysPulse.Core.Settings;

namespace SysPulse.App;

/// <summary>
/// Accent colors for the WPF title bar and the names the web UI uses in <c>data-accent</c>.
/// Keep the colors in sync with the <c>[data-accent]</c> blocks in wwwroot/css/app.css.
/// </summary>
internal static class AccentPalette
{
    public static Color For(AccentTheme accent) => accent switch
    {
        AccentTheme.Phosphor => Color.FromRgb(0x6F, 0xDC, 0x8C),
        AccentTheme.Arctic => Color.FromRgb(0x8F, 0xD3, 0xE8),
        _ => Color.FromRgb(0xF0, 0xA3, 0x3A),
    };

    public static string CssName(AccentTheme accent) => accent.ToString().ToLowerInvariant();
}

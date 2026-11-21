using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using ScholarFlow.Core.Models;

namespace ScholarFlow.Desktop.Theme;

/// <summary>
/// Manages sleek, low-noise neutral graphite Dark &amp; warm paper Light mode brushes in pure C# (no .axaml).
/// Inspired by Todoist, Linear, and Obsidian.
/// </summary>
public static class ThemeService
{
    public const string BgBase = "Sf.BgBase";
    public const string BgSurface = "Sf.BgSurface";
    public const string BgElevated = "Sf.BgElevated";
    public const string BgHover = "Sf.BgHover";
    public const string BgBackdrop = "Sf.BgBackdrop";
    public const string BorderSubtle = "Sf.BorderSubtle";
    public const string BorderStrong = "Sf.BorderStrong";
    public const string TextPrimary = "Sf.TextPrimary";
    public const string TextSecondary = "Sf.TextSecondary";
    public const string TextMuted = "Sf.TextMuted";
    public const string AccentPrimary = "Sf.AccentPrimary";
    public const string AccentSoftBg = "Sf.AccentSoftBg";
    public const string PriorityP1Fg = "Sf.P1.Fg";
    public const string PriorityP1Bg = "Sf.P1.Bg";
    public const string PriorityP2Fg = "Sf.P2.Fg";
    public const string PriorityP2Bg = "Sf.P2.Bg";
    public const string PriorityP3Fg = "Sf.P3.Fg";
    public const string PriorityP3Bg = "Sf.P3.Bg";
    public const string PriorityP4Fg = "Sf.P4.Fg";
    public const string PriorityP4Bg = "Sf.P4.Bg";
    public const string SuccessFg = "Sf.Success.Fg";
    public const string SuccessBg = "Sf.Success.Bg";

    public static ThemeMode CurrentTheme { get; private set; } = ThemeMode.Dark;

    public static event EventHandler<ThemeMode>? ThemeChanged;

    public static void ApplyTheme(ThemeMode mode)
    {
        CurrentTheme = mode;
        var app = Application.Current;
        if (app is null)
            return;

        app.RequestedThemeVariant = mode == ThemeMode.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var res = app.Resources;

        if (mode == ThemeMode.Dark)
        {
            // Calm, high-craft neutral graphite palette
            SetBrush(res, BgBase, "#141518");
            SetBrush(res, BgSurface, "#191B1F");
            SetBrush(res, BgElevated, "#212329");
            SetBrush(res, BgHover, "#272A32");
            SetBrush(res, BgBackdrop, "#99090A0D");
            SetBrush(res, BorderSubtle, "#252830");
            SetBrush(res, BorderStrong, "#343844");
            SetBrush(res, TextPrimary, "#EAECEF");
            SetBrush(res, TextSecondary, "#949BA7");
            SetBrush(res, TextMuted, "#686F7C");
            SetBrush(res, AccentPrimary, "#E0564B");
            SetBrush(res, AccentSoftBg, "#2B1E20");
            SetBrush(res, PriorityP1Fg, "#F06A6A");
            SetBrush(res, PriorityP1Bg, "#2D1C1E");
            SetBrush(res, PriorityP2Fg, "#F2B04A");
            SetBrush(res, PriorityP2Bg, "#2D2418");
            SetBrush(res, PriorityP3Fg, "#5C9CF5");
            SetBrush(res, PriorityP3Bg, "#1C2638");
            SetBrush(res, PriorityP4Fg, "#8A919E");
            SetBrush(res, PriorityP4Bg, "#22242A");
            SetBrush(res, SuccessFg, "#45C486");
            SetBrush(res, SuccessBg, "#182B22");

            // FluentTheme input controls (avoid pitch-black default TextBox/CalendarDatePicker)
            SetBrush(res, "TextControlBackground", "#1D2027");
            SetBrush(res, "TextControlBackgroundPointerOver", "#232730");
            SetBrush(res, "TextControlBackgroundFocused", "#181B21");
            SetBrush(res, "TextControlBorderBrush", "#2E333F");
            SetBrush(res, "TextControlBorderBrushPointerOver", "#3E4554");
            SetBrush(res, "TextControlBorderBrushFocused", "#E0564B");
            SetBrush(res, "TextControlForeground", "#EAECEF");
            SetBrush(res, "TextControlPlaceholderForeground", "#686F7C");

            // FluentTheme Flyout & CalendarDatePicker / Calendar Month popup brushes
            SetBrush(res, "FlyoutPresenterBackground", "#191B1F");
            SetBrush(res, "FlyoutBorderThemeBrush", "#343844");
            SetBrush(res, "CalendarDatePickerBackground", "#1D2027");
            SetBrush(res, "CalendarDatePickerBackgroundPointerOver", "#232730");
            SetBrush(res, "CalendarDatePickerBackgroundPressed", "#181B21");
            SetBrush(res, "CalendarDatePickerBorderBrush", "#2E333F");
            SetBrush(res, "CalendarDatePickerBorderBrushPointerOver", "#3E4554");
            SetBrush(res, "CalendarDatePickerBorderBrushFocused", "#E0564B");
            SetBrush(res, "CalendarDatePickerForeground", "#EAECEF");
            SetBrush(res, "CalendarDatePickerCalendarGlyphForeground", "#949BA7");
            SetBrush(res, "CalendarViewBackground", "#191B1F");
            SetBrush(res, "CalendarViewBorderBrush", "#343844");
            SetBrush(res, "CalendarViewCalendarItemBackground", "#1D2027");
            SetBrush(res, "CalendarViewCalendarItemForeground", "#EAECEF");
            SetBrush(res, "CalendarViewCalendarItemHoverBackground", "#272A32");
            SetBrush(res, "CalendarViewCalendarItemPressedBackground", "#2B1E20");
            SetBrush(res, "CalendarViewHeaderNavigationButtonForeground", "#EAECEF");
            SetBrush(res, "CalendarViewWeekDayNameForeground", "#949BA7");
            SetBrush(res, "CalendarViewOutOfScopeBackground", "#141518");
            SetBrush(res, "CalendarViewOutOfScopeForeground", "#686F7C");
            SetBrush(res, "CalendarViewTodayForeground", "#FFFFFF");
            SetBrush(res, "CalendarViewTodayBackground", "#E0564B");
            SetBrush(res, "CalendarViewSelectedBorderBrush", "#E0564B");
            SetBrush(res, "CalendarViewHoverBorderBrush", "#3E4554");
        }
        else
        {
            // Warm, crisp Todoist paper light palette
            SetBrush(res, BgBase, "#FFFFFF");
            SetBrush(res, BgSurface, "#FAF9F7");
            SetBrush(res, BgElevated, "#F2F1EE");
            SetBrush(res, BgHover, "#EAE8E4");
            SetBrush(res, BgBackdrop, "#66111318");
            SetBrush(res, BorderSubtle, "#E2E0DA");
            SetBrush(res, BorderStrong, "#CBC8C0");
            SetBrush(res, TextPrimary, "#1E2024");
            SetBrush(res, TextSecondary, "#626770");
            SetBrush(res, TextMuted, "#8E939D");
            SetBrush(res, AccentPrimary, "#DC4C3E");
            SetBrush(res, AccentSoftBg, "#FFEPE8".Replace('P', 'E'));
            SetBrush(res, PriorityP1Fg, "#D1453B");
            SetBrush(res, PriorityP1Bg, "#FDF0EF");
            SetBrush(res, PriorityP2Fg, "#D97706");
            SetBrush(res, PriorityP2Bg, "#FEF6E9");
            SetBrush(res, PriorityP3Fg, "#246FE0");
            SetBrush(res, PriorityP3Bg, "#EEF5FF");
            SetBrush(res, PriorityP4Fg, "#686F7C");
            SetBrush(res, PriorityP4Bg, "#F2F3F5");
            SetBrush(res, SuccessFg, "#1F8A58");
            SetBrush(res, SuccessBg, "#EAF7F0");

            // FluentTheme input controls (warm paper surface instead of harsh pure white)
            SetBrush(res, "TextControlBackground", "#F7F6F3");
            SetBrush(res, "TextControlBackgroundPointerOver", "#F1EFEA");
            SetBrush(res, "TextControlBackgroundFocused", "#FFFFFF");
            SetBrush(res, "TextControlBorderBrush", "#D6D4CE");
            SetBrush(res, "TextControlBorderBrushPointerOver", "#B8B5AE");
            SetBrush(res, "TextControlBorderBrushFocused", "#DC4C3E");
            SetBrush(res, "TextControlForeground", "#1E2024");
            SetBrush(res, "TextControlPlaceholderForeground", "#8E939D");

            // FluentTheme Flyout & CalendarDatePicker / Calendar Month popup brushes
            SetBrush(res, "FlyoutPresenterBackground", "#FAF9F7");
            SetBrush(res, "FlyoutBorderThemeBrush", "#CBC8C0");
            SetBrush(res, "CalendarDatePickerBackground", "#F7F6F3");
            SetBrush(res, "CalendarDatePickerBackgroundPointerOver", "#F1EFEA");
            SetBrush(res, "CalendarDatePickerBackgroundPressed", "#FFFFFF");
            SetBrush(res, "CalendarDatePickerBorderBrush", "#D6D4CE");
            SetBrush(res, "CalendarDatePickerBorderBrushPointerOver", "#B8B5AE");
            SetBrush(res, "CalendarDatePickerBorderBrushFocused", "#DC4C3E");
            SetBrush(res, "CalendarDatePickerForeground", "#1E2024");
            SetBrush(res, "CalendarDatePickerCalendarGlyphForeground", "#626770");
            SetBrush(res, "CalendarViewBackground", "#FAF9F7");
            SetBrush(res, "CalendarViewBorderBrush", "#CBC8C0");
            SetBrush(res, "CalendarViewCalendarItemBackground", "#FFFFFF");
            SetBrush(res, "CalendarViewCalendarItemForeground", "#1E2024");
            SetBrush(res, "CalendarViewCalendarItemHoverBackground", "#EAE8E4");
            SetBrush(res, "CalendarViewCalendarItemPressedBackground", "#FFEEE8");
            SetBrush(res, "CalendarViewHeaderNavigationButtonForeground", "#1E2024");
            SetBrush(res, "CalendarViewWeekDayNameForeground", "#626770");
            SetBrush(res, "CalendarViewOutOfScopeBackground", "#F2F1EE");
            SetBrush(res, "CalendarViewOutOfScopeForeground", "#8E939D");
            SetBrush(res, "CalendarViewTodayForeground", "#FFFFFF");
            SetBrush(res, "CalendarViewTodayBackground", "#DC4C3E");
            SetBrush(res, "CalendarViewSelectedBorderBrush", "#DC4C3E");
            SetBrush(res, "CalendarViewHoverBorderBrush", "#B8B5AE");
        }

        ThemeChanged?.Invoke(null, mode);
    }

    public static ThemeMode Toggle()
    {
        var next = CurrentTheme == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark;
        ApplyTheme(next);
        return next;
    }

    public static IBrush Brush(string key)
    {
        if (
            Application.Current?.Resources.TryGetResource(key, null, out var val) == true
            && val is IBrush b
        )
            return b;

        return Brushes.Transparent;
    }

    public static (IBrush Fg, IBrush Bg) GetPriorityBrushes(TaskPriority priority) =>
        priority switch
        {
            TaskPriority.P1 => (Brush(PriorityP1Fg), Brush(PriorityP1Bg)),
            TaskPriority.P2 => (Brush(PriorityP2Fg), Brush(PriorityP2Bg)),
            TaskPriority.P3 => (Brush(PriorityP3Fg), Brush(PriorityP3Bg)),
            _ => (Brush(PriorityP4Fg), Brush(PriorityP4Bg)),
        };

    public static IBrush PriorityBrush(TaskPriority priority) => GetPriorityBrushes(priority).Fg;

    private static void SetBrush(IResourceDictionary dict, string key, string hex)
    {
        var color = Color.Parse(hex);
        if (dict.TryGetValue(key, out var existing) && existing is SolidColorBrush scb)
        {
            scb.Color = color;
            return;
        }

        dict[key] = new SolidColorBrush(color);
    }
}

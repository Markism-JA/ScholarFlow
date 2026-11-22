using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;
using ScholarFlow.Desktop.Theme;

namespace ScholarFlow.Desktop.UI;

/// <summary>
/// Pure C# low-noise UI primitives with smooth hover transitions, zero emojis, and zero .axaml dependencies.
/// </summary>
public static class UiBuilder
{
    public static MaterialIcon Icon(
        MaterialIconKind kind,
        double size = 15,
        IBrush? foreground = null
    )
    {
        var icon = new MaterialIcon
        {
            Kind = kind,
            Width = size,
            Height = size,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (foreground is not null)
            icon.Foreground = foreground;
        return icon;
    }

    public static Transitions CreateSmoothBrushTransitions(int durationMs = 130) =>
        [
            new BrushTransition
            {
                Property = Border.BackgroundProperty,
                Duration = TimeSpan.FromMilliseconds(durationMs),
            },
            new BrushTransition
            {
                Property = Border.BorderBrushProperty,
                Duration = TimeSpan.FromMilliseconds(durationMs),
            },
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(durationMs),
            },
        ];

    public static Border HairlineDivider(Thickness? margin = null) =>
        new()
        {
            Height = 1,
            Background = ThemeService.Brush(ThemeService.BorderSubtle),
            Margin = margin ?? new Thickness(0),
        };

    public static Border KbdChip(string keyText) =>
        new()
        {
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1.5),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = keyText,
                FontSize = 10.5,
                FontWeight = FontWeight.Medium,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

    public static Border Pill(
        string text,
        IBrush fg,
        IBrush bg,
        MaterialIconKind? iconKind = null,
        double fontSize = 11
    )
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (iconKind.HasValue)
            row.Children.Add(Icon(iconKind.Value, 11.5, fg));

        row.Children.Add(
            new TextBlock
            {
                Text = text,
                Foreground = fg,
                FontSize = fontSize,
                FontWeight = FontWeight.Medium,
                VerticalAlignment = VerticalAlignment.Center,
            }
        );

        return new Border
        {
            Background = bg,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = row,
        };
    }

    public static Button IconButton(
        MaterialIconKind icon,
        Action onClick,
        double iconSize = 15,
        IBrush? fg = null
    )
    {
        var iconFg = fg ?? ThemeService.Brush(ThemeService.TextSecondary);
        var hoverBg = ThemeService.Brush(ThemeService.BgHover);

        var btn = new Button
        {
            Content = Icon(icon, iconSize, iconFg),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };

        btn.PointerEntered += (_, _) => btn.Background = hoverBg;
        btn.PointerExited += (_, _) => btn.Background = Brushes.Transparent;
        btn.Click += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return btn;
    }

    public static Button GhostButton(
        string label,
        MaterialIconKind? icon,
        Action onClick,
        bool isActive = false,
        IBrush? customFg = null
    )
    {
        var fg =
            customFg
            ?? (
                isActive
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.TextSecondary)
            );
        var normalBg = isActive
            ? ThemeService.Brush(ThemeService.AccentSoftBg)
            : Brushes.Transparent;
        var hoverBg = isActive
            ? ThemeService.Brush(ThemeService.AccentSoftBg)
            : ThemeService.Brush(ThemeService.BgHover);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (icon.HasValue)
            row.Children.Add(Icon(icon.Value, 13.5, fg));

        if (!string.IsNullOrEmpty(label))
        {
            row.Children.Add(
                new TextBlock
                {
                    Text = label,
                    Foreground = fg,
                    FontSize = 12,
                    FontWeight = isActive ? FontWeight.SemiBold : FontWeight.Medium,
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
        }

        var btn = new Button
        {
            Content = row,
            Background = normalBg,
            BorderBrush = isActive
                ? ThemeService.Brush(ThemeService.AccentPrimary)
                : ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 4),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };

        btn.PointerEntered += (_, _) => btn.Background = hoverBg;
        btn.PointerExited += (_, _) => btn.Background = normalBg;
        btn.Click += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return btn;
    }

    public static Button ActionButton(
        string label,
        MaterialIconKind icon,
        Action onClick,
        bool isPrimary = false,
        string? tooltip = null
    )
    {
        var fg = isPrimary ? Brushes.White : ThemeService.Brush(ThemeService.TextPrimary);
        var normalBg = isPrimary
            ? ThemeService.Brush(ThemeService.AccentPrimary)
            : ThemeService.Brush(ThemeService.BgElevated);
        var hoverBg = isPrimary
            ? ThemeService.Brush(ThemeService.AccentPrimary)
            : ThemeService.Brush(ThemeService.BgHover);
        var normalBorder = isPrimary
            ? Brushes.Transparent
            : ThemeService.Brush(ThemeService.BorderSubtle);
        var hoverBorder = isPrimary
            ? Brushes.Transparent
            : ThemeService.Brush(ThemeService.BorderStrong);

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(Icon(icon, 14.5, fg));
        if (!string.IsNullOrEmpty(label))
        {
            content.Children.Add(
                new TextBlock
                {
                    Text = label,
                    Foreground = fg,
                    FontSize = 12.5,
                    FontWeight = isPrimary ? FontWeight.SemiBold : FontWeight.Medium,
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
        }

        var btn = new Button
        {
            Content = content,
            Background = normalBg,
            BorderBrush = normalBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Padding = new Thickness(string.IsNullOrEmpty(label) ? 6.5 : 10, 5.5),
        };

        btn.PointerEntered += (_, _) =>
        {
            btn.Background = hoverBg;
            btn.BorderBrush = hoverBorder;
            if (isPrimary)
                btn.Opacity = 0.9;
        };
        btn.PointerExited += (_, _) =>
        {
            btn.Background = normalBg;
            btn.BorderBrush = normalBorder;
            btn.Opacity = 1.0;
        };

        btn.Click += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return btn;
    }

    public static TextBlock SectionHeader(string text) =>
        new()
        {
            Text = text.ToUpperInvariant(),
            FontSize = 10.5,
            FontWeight = FontWeight.SemiBold,
            LetterSpacing = 0.7,
            Foreground = ThemeService.Brush(ThemeService.TextMuted),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 4, 4, 4),
        };

    public static Button PrimaryButton(string label, MaterialIconKind icon, Action onClick) =>
        ActionButton(label, icon, onClick, isPrimary: true);

    public static Border PillBadge(string text, IBrush fg, IBrush bg) => Pill(text, fg, bg);

    private static readonly Dictionary<string, Vector> SavedScrollOffsets = new(
        StringComparer.Ordinal
    );

    public static void ResetScrollOffset(string key)
    {
        SavedScrollOffsets.Remove(key);
    }

    /// <summary>
    /// Creates a ScrollViewer whose scroll offset is preserved across full pure-C# UI rebuilds
    /// (RequestUiRefresh), preventing scroll-to-top jumps when state changes.
    /// </summary>
    public static ScrollViewer PersistentScrollViewer(
        string key,
        Control content,
        Avalonia.Controls.Primitives.ScrollBarVisibility horizontalVisibility =
            Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        Avalonia.Controls.Primitives.ScrollBarVisibility verticalVisibility =
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
    )
    {
        var savedOffset = SavedScrollOffsets.GetValueOrDefault(key, Vector.Zero);
        bool layoutRestored = false;

        var scroll = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = horizontalVisibility,
            VerticalScrollBarVisibility = verticalVisibility,
            Offset = savedOffset,
        };

        scroll.ScrollChanged += (_, _) =>
        {
            if (layoutRestored)
                SavedScrollOffsets[key] = scroll.Offset;
        };

        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                scroll.Offset = savedOffset;
                layoutRestored = true;
            },
            Avalonia.Threading.DispatcherPriority.Loaded
        );

        return scroll;
    }

    /// <summary>
    /// Enables Ctrl+ScrollWheel zooming and middle-mouse-button drag panning on any PDF ScrollViewer.
    /// </summary>
    public static void AttachPdfWheelAndPan(ScrollViewer scroll, Action<int> onZoomDelta)
    {
        scroll.PointerWheelChanged += (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && Math.Abs(e.Delta.Y) > 0.01)
            {
                e.Handled = true;
                onZoomDelta(e.Delta.Y > 0 ? 15 : -15);
            }
        };

        Point? panLast = null;
        scroll.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(scroll).Properties.IsMiddleButtonPressed)
            {
                panLast = e.GetPosition(scroll);
                e.Pointer.Capture(scroll);
                e.Handled = true;
            }
        };
        scroll.PointerMoved += (_, e) =>
        {
            if (!panLast.HasValue)
                return;
            var cur = e.GetPosition(scroll);
            var delta = cur - panLast.Value;
            panLast = cur;
            scroll.Offset = new Vector(
                Math.Max(0, scroll.Offset.X - delta.X),
                Math.Max(0, scroll.Offset.Y - delta.Y)
            );
        };
        scroll.PointerReleased += (_, e) =>
        {
            if (panLast.HasValue)
            {
                panLast = null;
                e.Pointer.Capture(null);
                e.Handled = true;
            }
        };
    }

    public static Border Card(Control child, Thickness? padding = null) =>
        new()
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = padding ?? new Thickness(14, 12),
            Child = child,
        };
}

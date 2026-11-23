using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using ScholarFlow.Desktop.Theme;

namespace ScholarFlow.Desktop.UI;

/// <summary>
/// Responsive-safe animated collapsible container using Avalonia's <see cref="LayoutTransformControl"/>.
/// Animates <see cref="ScaleTransform.ScaleY"/> (0..1) and <see cref="Visual.Opacity"/> without hardcoded
/// pixel heights so wrapped text and grids reflow naturally on any window resize.
/// </summary>
public sealed class AnimatedCollapsible : Border
{
    private readonly LayoutTransformControl _transformHost;
    private readonly ScaleTransform _scaleTransform;
    private Func<Control>? _lazyFactory;
    private int _toggleVersion;
    private bool _isExpanded;
    private readonly int _durationMs;

    public bool IsExpanded => _isExpanded;

    public AnimatedCollapsible(Control content, bool initialExpanded = false, int durationMs = 180)
        : this(() => content, initialExpanded, durationMs, materializeImmediately: true) { }

    public AnimatedCollapsible(
        Func<Control> contentFactory,
        bool initialExpanded = false,
        int durationMs = 180
    )
        : this(contentFactory, initialExpanded, durationMs, materializeImmediately: initialExpanded) { }

    private AnimatedCollapsible(
        Func<Control> contentFactory,
        bool initialExpanded,
        int durationMs,
        bool materializeImmediately
    )
    {
        _isExpanded = initialExpanded;
        _durationMs = durationMs;
        ClipToBounds = true;

        _scaleTransform = new ScaleTransform(1.0, initialExpanded ? 1.0 : 0.0)
        {
            Transitions =
            [
                new DoubleTransition
                {
                    Property = ScaleTransform.ScaleYProperty,
                    Duration = TimeSpan.FromMilliseconds(durationMs),
                    Easing = new CubicEaseOut(),
                },
            ],
        };

        Control? initialChild = null;
        if (materializeImmediately)
            initialChild = contentFactory();
        else
            _lazyFactory = contentFactory;

        _transformHost = new LayoutTransformControl
        {
            LayoutTransform = _scaleTransform,
            Child = initialChild,
            Opacity = initialExpanded ? 1.0 : 0.0,
            Transitions =
            [
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(Math.Max(100, durationMs - 20)),
                    Easing = new CubicEaseOut(),
                },
            ],
        };

        IsVisible = initialExpanded;
        Child = _transformHost;
    }

    public void SetExpanded(bool expanded)
    {
        if (_isExpanded == expanded)
            return;

        _isExpanded = expanded;
        int version = ++_toggleVersion;

        if (expanded)
        {
            if (_transformHost.Child is null && _lazyFactory is not null)
            {
                _transformHost.Child = _lazyFactory();
                _lazyFactory = null;
            }
            IsVisible = true;
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (_toggleVersion != version)
                        return;
                    _scaleTransform.ScaleY = 1.0;
                    _transformHost.Opacity = 1.0;
                },
                DispatcherPriority.Render
            );
        }
        else
        {
            _scaleTransform.ScaleY = 0.0;
            _transformHost.Opacity = 0.0;

            _ = Task.Delay(_durationMs + 20)
                .ContinueWith(_ =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_toggleVersion == version && !_isExpanded)
                            IsVisible = false;
                    });
                });
        }
    }

    /// <summary>
    /// Builds a styled collapsible section card with a clickable header, rotating chevron,
    /// optional trailing action control, and responsive animated body.
    /// </summary>
    public static Border CreateCard(
        string title,
        string? subtitle,
        MaterialIconKind icon,
        Control body,
        bool initialExpanded = false,
        Control? trailingHeaderAction = null
    )
    {
        var collapsible = new AnimatedCollapsible(body, initialExpanded);

        var chevron = UiBuilder.Icon(
            MaterialIconKind.ChevronRight,
            15,
            ThemeService.Brush(ThemeService.TextSecondary)
        );
        var chevronRotate = new RotateTransform(initialExpanded ? 90 : 0)
        {
            Transitions =
            [
                new DoubleTransition
                {
                    Property = RotateTransform.AngleProperty,
                    Duration = TimeSpan.FromMilliseconds(160),
                    Easing = new CubicEaseOut(),
                },
            ],
        };
        chevron.RenderTransform = chevronRotate;
        chevron.RenderTransformOrigin = RelativePoint.Center;

        var titleStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleStack.Children.Add(
            new TextBlock
            {
                Text = title,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            titleStack.Children.Add(
                new TextBlock
                {
                    Text = subtitle,
                    FontSize = 10.5,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }
            );
        }

        var headerGrid = new Grid
        {
            ColumnDefinitions = trailingHeaderAction is not null
                ? ColumnDefinitions.Parse("Auto,Auto,*,Auto")
                : ColumnDefinitions.Parse("Auto,Auto,*"),
            ColumnSpacing = 6,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        headerGrid.Children.Add(chevron);
        var leadIcon = UiBuilder.Icon(icon, 14, ThemeService.Brush(ThemeService.TextSecondary));
        Grid.SetColumn(leadIcon, 1);
        headerGrid.Children.Add(leadIcon);
        Grid.SetColumn(titleStack, 2);
        headerGrid.Children.Add(titleStack);
        if (trailingHeaderAction is not null)
        {
            Grid.SetColumn(trailingHeaderAction, 3);
            headerGrid.Children.Add(trailingHeaderAction);
        }

        headerGrid.PointerPressed += (_, e) =>
        {
            if (trailingHeaderAction is not null && e.Source is Visual v)
            {
                for (Visual? cur = v; cur is not null; cur = cur.Parent as Visual)
                {
                    if (ReferenceEquals(cur, trailingHeaderAction))
                        return;
                }
            }
            e.Handled = true;
            bool next = !collapsible.IsExpanded;
            collapsible.SetExpanded(next);
            chevronRotate.Angle = next ? 90 : 0;
        };

        body.Margin = new Thickness(0, 8, 0, 0);

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 8),
            Child = new StackPanel { Children = { headerGrid, collapsible } },
        };
    }
}

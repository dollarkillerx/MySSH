using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MySSH.UI.Carbon;

/// <summary>
/// Draws a Carbon icon from the <c>CarbonIcon.{Kind}</c> StreamGeometry resources (Icons/CarbonIcons.axaml).
/// Uses the pixel-tuned 16/20px artwork when it exists, otherwise scales the 32px artwork.
/// </summary>
/// <remarks>
/// <see cref="Foreground"/> inherits like text (Carbon icons use <c>currentColor</c>); outside of a colored
/// container it resolves to <c>text-primary</c>, which equals <c>icon-primary</c> in every Carbon theme.
/// </remarks>
public class CarbonIcon : Control
{
    /// <summary>Icon name, see <see cref="CarbonIconNames"/> (e.g. "add", "chevron-down").</summary>
    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<CarbonIcon, string?>(nameof(Kind));

    /// <summary>Rendered size in DIPs: 16, 20, 24 or 32 (any value works; 16/20 pick tuned artwork).</summary>
    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<CarbonIcon, double>(nameof(Size), 16);

    /// <summary>Icon fill. Inherits from the parent's text foreground.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<CarbonIcon>();

    /// <summary>Fill of the inner path of filled status icons (e.g. the "!" of warning-filled). Null = not drawn.</summary>
    public static readonly StyledProperty<IBrush?> InnerForegroundProperty =
        AvaloniaProperty.Register<CarbonIcon, IBrush?>(nameof(InnerForeground));

    private Geometry? _geometry;
    private Geometry? _inner;
    private double _artSize;
    private bool _resolved;

    static CarbonIcon()
    {
        AffectsMeasure<CarbonIcon>(SizeProperty);
        AffectsRender<CarbonIcon>(KindProperty, SizeProperty, ForegroundProperty, InnerForegroundProperty);
    }

    /// <inheritdoc cref="KindProperty"/>
    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <inheritdoc cref="SizeProperty"/>
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <inheritdoc cref="ForegroundProperty"/>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <inheritdoc cref="InnerForegroundProperty"/>
    public IBrush? InnerForeground
    {
        get => GetValue(InnerForegroundProperty);
        set => SetValue(InnerForegroundProperty, value);
    }

    /// <summary>True when <see cref="Kind"/> resolved to an icon resource.</summary>
    public bool HasIcon
    {
        get
        {
            Resolve();
            return _geometry is not null;
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty || change.Property == SizeProperty)
        {
            _resolved = false;
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _resolved = false;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        Resolve();
        if (_geometry is null || _artSize <= 0)
        {
            return;
        }

        var size = Size;
        var scale = size / _artSize;
        var offset = new Point((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            if (Foreground is { } fg)
            {
                context.DrawGeometry(fg, null, _geometry);
            }

            if (_inner is not null && InnerForeground is { } inner)
            {
                context.DrawGeometry(inner, null, _inner);
            }
        }
    }

    private void Resolve()
    {
        if (_resolved)
        {
            return;
        }

        _resolved = true;
        _geometry = _inner = null;
        var kind = Kind;
        if (string.IsNullOrEmpty(kind))
        {
            return;
        }

        var size = Size;
        string key;
        if (size <= 16.5 && CarbonIconNames.Has16.Contains(kind))
        {
            key = $"CarbonIcon.{kind}.16";
            _artSize = 16;
        }
        else if (size is > 16.5 and <= 20.5 && CarbonIconNames.Has20.Contains(kind))
        {
            key = $"CarbonIcon.{kind}.20";
            _artSize = 20;
        }
        else
        {
            key = $"CarbonIcon.{kind}";
            _artSize = 32;
        }

        _geometry = Find(key);
        _inner = Find(key + ".inner");
    }

    private Geometry? Find(string key)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var value) && value is Geometry g)
        {
            return g;
        }

        if (Application.Current is { } app && app.TryFindResource(key, out value) && value is Geometry g2)
        {
            return g2;
        }

        return null;
    }
}

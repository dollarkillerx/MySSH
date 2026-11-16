using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MySSH.Desktop.Localization;
using MySSH.UI.Carbon;

namespace MySSH.Desktop.Views;

/// <summary>
/// Renders <see cref="Markdown"/> (see <see cref="MarkdownParser"/>) with Carbon styles. Code blocks get "Copy" and
/// "Insert into terminal" (<see cref="InsertCommand"/>, parameter = the code; never executed). Re-renders are coalesced, so
/// a streaming answer is laid out at most once per UI frame.
/// </summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));
    public static readonly StyledProperty<ICommand?> InsertCommandProperty = AvaloniaProperty.Register<MarkdownView, ICommand?>(nameof(InsertCommand));

    bool _pending;
    string? _rendered;

    public MarkdownView()
    {
        Spacing = 8;
        ActualThemeVariantChanged += (_, _) => { if (Children.Count > 0) RenderNow(); }; // brushes are resolved per render
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public ICommand? InsertCommand
    {
        get => GetValue(InsertCommandProperty);
        set => SetValue(InsertCommandProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty || change.Property == InsertCommandProperty) Schedule();
    }

    void Schedule()
    {
        if (_pending) return;
        _pending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pending = false;
            if (_rendered == Markdown && Children.Count > 0) return;
            _rendered = Markdown;
            Render();
        }, DispatcherPriority.Background);
    }

    /// <summary>Renders now (tests, first layout).</summary>
    internal void RenderNow()
    {
        _pending = false;
        _rendered = Markdown;
        Render();
    }

    void Render()
    {
        Children.Clear();
        foreach (var block in MarkdownParser.Parse(Markdown))
            Children.Add(block.Kind switch
            {
                MdBlockKind.Code => CodeBlock(block),
                MdBlockKind.Table => Table(block),
                MdBlockKind.Heading => Text(block.Text, block.Level <= 2 ? "heading-compact-02" : "heading-compact-01"),
                MdBlockKind.ListItem => ListItem(block),
                MdBlockKind.Quote => Quote(block),
                MdBlockKind.Rule => new Border { Height = 1, Background = Brush("CarbonBorderSubtle01"), Margin = new Thickness(0, 4) },
                _ => Text(block.Text, "body-01"),
            });
    }

    IBrush? Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var v) ? v as IBrush : null;

    SelectableTextBlock Text(string text, string style)
    {
        var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        tb.Classes.Add(style);
        AddInlines(tb.Inlines!, text);
        return tb;
    }

    void AddInlines(InlineCollection inlines, string text)
    {
        var mono = this.TryFindResource("CarbonFontMono", out var f) ? f as FontFamily : null;
        foreach (var span in MarkdownParser.ParseInline(text))
        {
            var lines = span.Text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) inlines.Add(new LineBreak());
                if (lines[i].Length == 0) continue;
                var run = new Run(lines[i]);
                switch (span.Kind)
                {
                    case MdSpanKind.Bold:
                        run.FontWeight = FontWeight.SemiBold;
                        break;
                    case MdSpanKind.Italic:
                        run.FontStyle = FontStyle.Italic;
                        break;
                    case MdSpanKind.Code:
                        if (mono is not null) run.FontFamily = mono;
                        run.Background = Brush("CarbonLayerAccent01");
                        break;
                    case MdSpanKind.Link:
                        run.Foreground = Brush("CarbonLinkPrimary");
                        run.TextDecorations = TextDecorations.Underline;
                        break;
                }
                inlines.Add(run);
            }
        }
    }

    Grid ListItem(MdBlock block)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(block.Level * 16, 0, 0, 0) };
        var marker = new TextBlock { Text = block.Marker ?? "•", Margin = new Thickness(0, 0, 8, 0), MinWidth = 12 };
        marker.Classes.Add("body-01");
        grid.Children.Add(marker);
        var text = Text(block.Text, "body-01");
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    Border Quote(MdBlock block)
    {
        var text = Text(block.Text, "body-01");
        text.Classes.Add("secondary");
        return new Border
        {
            BorderBrush = Brush("CarbonBorderStrong01"), BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 0, 0, 0), Child = text,
        };
    }

    /// <summary>
    /// Carbon-style table: header on layer-accent, subtle row rules, cells wrap. With up to three columns the last ones share
    /// the width (the panel is narrow); wider tables keep their natural width and scroll sideways.
    /// </summary>
    Control Table(MdBlock block)
    {
        var rows = block.Rows ?? [];
        var columns = rows.Count == 0 ? 0 : rows[0].Count;
        var fit = columns <= 3;
        var grid = new Grid();
        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(fit && c > 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto)
            {
                MaxWidth = fit && c == 0 && columns > 1 ? 140 : double.PositiveInfinity,
            });
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var background = new Border
            {
                Background = r == 0 ? Brush("CarbonLayerAccent01") : null,
                BorderBrush = Brush("CarbonBorderSubtle01"),
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
            Grid.SetRow(background, r);
            Grid.SetColumnSpan(background, Math.Max(1, columns));
            grid.Children.Add(background);
            for (var c = 0; c < columns; c++)
            {
                var cell = Text(rows[r][c], r == 0 ? "heading-compact-01" : "body-compact-01");
                cell.Margin = new Thickness(8, 6);
                cell.TextWrapping = fit ? TextWrapping.Wrap : TextWrapping.NoWrap;
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        if (fit) return grid;
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = grid,
        };
    }

    Border CodeBlock(MdBlock block)
    {
        var code = new SelectableTextBlock { Text = block.Text, Margin = new Thickness(12, 8, 12, 12), TextWrapping = TextWrapping.NoWrap };
        code.Classes.Add("code-01");

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(12, 4, 4, 0) };
        var lang = new TextBlock { Text = block.Language ?? "", VerticalAlignment = VerticalAlignment.Center };
        lang.Classes.Add("label-01");
        lang.Classes.Add("secondary");
        header.Children.Add(lang);

        var copy = ActionButton("copy", Loc.Instance["common.copy"]);
        copy.Name = "CopyCode";
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(block.Text);
        };
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);

        if (InsertCommand is { } insert && block.Text.Trim().Length > 0)
        {
            var put = ActionButton("terminal", Loc.Instance["ai.code.insert"]);
            put.Name = "InsertCode";
            put.Command = insert;
            put.CommandParameter = block.Text.Trim();
            ToolTip.SetTip(put, Loc.Instance["ai.code.insertTip"]);
            Grid.SetColumn(put, 2);
            header.Children.Add(put);
        }

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = code,
        });
        return new Border { Background = Brush("CarbonField02"), Child = panel };
    }

    static Button ActionButton(string icon, string text)
    {
        var b = new Button { Content = text };
        b.Classes.Add("ghost");
        b.Classes.Add("sm");
        ButtonAssist.SetIcon(b, icon);
        return b;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RenderNow();
    }
}

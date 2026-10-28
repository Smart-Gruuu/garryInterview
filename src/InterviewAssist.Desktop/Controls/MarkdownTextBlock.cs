using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using InterviewAssist.Presentation.Markdown;

namespace InterviewAssist.Desktop.Controls;

/// <summary>
/// A <see cref="SelectableTextBlock"/> that renders the streaming-friendly Markdown subset of <see cref="MarkdownParser"/>
/// into <see cref="TextBlock.Inlines"/>: bold, italic, inline code, fenced code blocks, #/##/### headings, bullets,
/// numbered items, links (text only), block quotes and paragraphs. Re-rendering is throttled to one per 60 ms while the
/// text streams in; the latest value is always rendered.
/// </summary>
public class MarkdownTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, string?>(nameof(Markdown));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, IBrush?>(nameof(AccentBrush));

    public static readonly StyledProperty<IBrush?> CodeBackgroundBrushProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, IBrush?>(nameof(CodeBackgroundBrush));

    public static readonly StyledProperty<IBrush?> CodeTextBrushProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, IBrush?>(nameof(CodeTextBrush));

    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(60);

    private static readonly FontFamily MonospaceFontFamily = new(
        OperatingSystem.IsWindows() ? "Consolas" : OperatingSystem.IsMacOS() ? "Menlo" : "monospace");

    private long _lastRenderAt;
    private bool _renderScheduled;

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public IBrush? AccentBrush
    {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public IBrush? CodeBackgroundBrush
    {
        get => GetValue(CodeBackgroundBrushProperty);
        set => SetValue(CodeBackgroundBrushProperty, value);
    }

    public IBrush? CodeTextBrush
    {
        get => GetValue(CodeTextBrushProperty);
        set => SetValue(CodeTextBrushProperty, value);
    }

    // Use the SelectableTextBlock theme (selection brush, cursor) from the Fluent theme.
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Heading sizes are absolute, derived from the control's font size.
        if (change.Property == MarkdownProperty
            || change.Property == AccentBrushProperty
            || change.Property == CodeBackgroundBrushProperty
            || change.Property == CodeTextBrushProperty
            || change.Property == FontSizeProperty)
        {
            RequestRender();
        }
    }

    private void RequestRender()
    {
        if (_renderScheduled)
        {
            return; // the scheduled render reads the latest values
        }

        var sinceLast = Stopwatch.GetElapsedTime(_lastRenderAt);
        if (sinceLast >= RenderInterval)
        {
            Render();
            return;
        }

        _renderScheduled = true;
        DispatcherTimer.RunOnce(
            () =>
            {
                _renderScheduled = false;
                Render();
            },
            RenderInterval - sinceLast,
            DispatcherPriority.Normal);
    }

    private void Render()
    {
        _lastRenderAt = Stopwatch.GetTimestamp();

        var markdown = Markdown;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            Inlines = null;
            return;
        }

        var fontSize = FontSize > 0 ? FontSize : 14;
        var inlines = new InlineCollection();
        foreach (var run in MarkdownParser.Parse(markdown))
        {
            inlines.Add(CreateRun(run, fontSize));
        }

        // Assigned once, after all runs are added, so the text layout is rebuilt a single time.
        Inlines = inlines;
    }

    private Run CreateRun(MarkdownRun markdownRun, double fontSize)
    {
        var style = markdownRun.Style;
        var run = new Run(markdownRun.Text);

        if ((style & (RunStyle.Bold | RunStyle.Heading1 | RunStyle.Heading2 | RunStyle.Heading3)) != 0)
        {
            run.FontWeight = FontWeight.Bold;
        }

        if ((style & RunStyle.Italic) != 0)
        {
            run.FontStyle = FontStyle.Italic;
        }

        if ((style & (RunStyle.Code | RunStyle.CodeBlock)) != 0)
        {
            run.FontFamily = MonospaceFontFamily;
            if (CodeBackgroundBrush is { } codeBackground)
            {
                run.Background = codeBackground;
            }

            if (CodeTextBrush is { } codeText)
            {
                run.Foreground = codeText;
            }
        }
        else if ((style & RunStyle.Accent) != 0 && AccentBrush is { } accent)
        {
            run.Foreground = accent;
        }

        if ((style & RunStyle.Heading1) != 0)
        {
            run.FontSize = Math.Round(fontSize * 1.3, 1);
        }
        else if ((style & RunStyle.Heading2) != 0)
        {
            run.FontSize = Math.Round(fontSize * 1.18, 1);
        }
        else if ((style & RunStyle.Heading3) != 0)
        {
            run.FontSize = Math.Round(fontSize * 1.08, 1);
        }

        return run;
    }
}

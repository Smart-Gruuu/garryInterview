using System.Diagnostics;
using System.Runtime.CompilerServices;
using InterviewAssist.Presentation.Markdown;

namespace InterviewAssist.Controls;

/// <summary>
/// A <see cref="Label"/> that renders a small, streaming-friendly subset of Markdown into <see cref="Label.FormattedText"/>:
/// bold, italic, inline code, fenced code blocks, #/##/### headings, bullets, numbered items, links (text only),
/// block quotes and paragraphs. Unclosed inline markers render as plain text. Re-rendering is throttled to one per
/// 60 ms while the text streams in; the latest value is always rendered.
/// </summary>
public class MarkdownLabel : Label
{
    public static readonly BindableProperty MarkdownProperty = BindableProperty.Create(
        nameof(Markdown), typeof(string), typeof(MarkdownLabel), string.Empty, propertyChanged: OnRenderInputChanged);

    public static readonly BindableProperty AccentColorProperty = BindableProperty.Create(
        nameof(AccentColor), typeof(Color), typeof(MarkdownLabel), null, propertyChanged: OnRenderInputChanged);

    public static readonly BindableProperty CodeBackgroundColorProperty = BindableProperty.Create(
        nameof(CodeBackgroundColor), typeof(Color), typeof(MarkdownLabel), null, propertyChanged: OnRenderInputChanged);

    public static readonly BindableProperty CodeTextColorProperty = BindableProperty.Create(
        nameof(CodeTextColor), typeof(Color), typeof(MarkdownLabel), null, propertyChanged: OnRenderInputChanged);

    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(60);

    private long _lastRenderAt;
    private bool _renderScheduled;

    public string? Markdown
    {
        get => (string?)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public Color? AccentColor
    {
        get => (Color?)GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    public Color? CodeBackgroundColor
    {
        get => (Color?)GetValue(CodeBackgroundColorProperty);
        set => SetValue(CodeBackgroundColorProperty, value);
    }

    public Color? CodeTextColor
    {
        get => (Color?)GetValue(CodeTextColorProperty);
        set => SetValue(CodeTextColorProperty, value);
    }

    protected override void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);

        // Heading sizes are absolute, derived from the label's font size.
        if (propertyName == FontSizeProperty.PropertyName)
        {
            RequestRender();
        }
    }

    private static void OnRenderInputChanged(BindableObject bindable, object? oldValue, object? newValue) =>
        ((MarkdownLabel)bindable).RequestRender();

    private void RequestRender()
    {
        if (_renderScheduled)
        {
            return; // the scheduled render reads the latest values
        }

        var sinceLast = Stopwatch.GetElapsedTime(_lastRenderAt);
        var dispatcher = Dispatcher;
        if (sinceLast >= RenderInterval || dispatcher is null)
        {
            Render();
            return;
        }

        _renderScheduled = true;
        dispatcher.DispatchDelayed(RenderInterval - sinceLast, () =>
        {
            _renderScheduled = false;
            Render();
        });
    }

    private void Render()
    {
        _lastRenderAt = Stopwatch.GetTimestamp();

        var markdown = Markdown;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            FormattedText = null;
            return;
        }

        var runs = MarkdownParser.Parse(markdown);
        var fontSize = FontSize > 0 ? FontSize : 14;
        var formatted = new FormattedString();
        foreach (var run in runs)
        {
            formatted.Spans.Add(CreateSpan(run, fontSize));
        }

        // Assigned once, after all spans are added, so the platform view is rebuilt a single time.
        FormattedText = formatted;
    }

    private Span CreateSpan(MarkdownRun run, double fontSize)
    {
        var style = run.Style;
        var span = new Span { Text = run.Text };

        var attributes = FontAttributes.None;
        if ((style & (RunStyle.Bold | RunStyle.Heading1 | RunStyle.Heading2 | RunStyle.Heading3)) != 0)
        {
            attributes |= FontAttributes.Bold;
        }

        if ((style & RunStyle.Italic) != 0)
        {
            attributes |= FontAttributes.Italic;
        }

        span.FontAttributes = attributes;

        if ((style & (RunStyle.Code | RunStyle.CodeBlock)) != 0)
        {
            span.FontFamily = MonospaceFontFamily;
            if (CodeBackgroundColor is { } codeBackground)
            {
                span.BackgroundColor = codeBackground;
            }

            if (CodeTextColor is { } codeText)
            {
                span.TextColor = codeText;
            }
        }
        else if ((style & RunStyle.Accent) != 0 && AccentColor is { } accent)
        {
            span.TextColor = accent;
        }

        if ((style & RunStyle.Heading1) != 0)
        {
            span.FontSize = Math.Round(fontSize * 1.3, 1);
        }
        else if ((style & RunStyle.Heading2) != 0)
        {
            span.FontSize = Math.Round(fontSize * 1.18, 1);
        }
        else if ((style & RunStyle.Heading3) != 0)
        {
            span.FontSize = Math.Round(fontSize * 1.08, 1);
        }

        return span;
    }

    private static string MonospaceFontFamily =>
        DeviceInfo.Current.Platform == DevicePlatform.WinUI ? "Consolas" : "monospace";
}

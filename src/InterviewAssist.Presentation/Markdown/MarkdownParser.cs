using System.Text;

namespace InterviewAssist.Presentation.Markdown;

/// <summary>Formatting of a <see cref="MarkdownRun"/>; heading levels combine with the inline styles.</summary>
[Flags]
public enum RunStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
    CodeBlock = 8,
    Accent = 16,
    Heading1 = 32,
    Heading2 = 64,
    Heading3 = 128,
}

/// <summary>A stretch of text with one style. Each UI renders a run as one span/inline.</summary>
public sealed class MarkdownRun(RunStyle style, string text)
{
    public RunStyle Style { get; } = style;

    internal StringBuilder Builder { get; } = new(text);

    public string Text => Builder.ToString();
}

/// <summary>
/// Line-based Markdown → styled runs. Adjacent text with the same style is merged so a typical answer becomes a
/// few dozen spans. Pure and allocation-light; safe to call on every streamed update.
/// </summary>
public static class MarkdownParser
{
    private const string Bullet = "•  ";
    private const string IndentUnit = " "; // em space: one level of list nesting

    public static IReadOnlyList<MarkdownRun> Parse(string markdown)
    {
        var writer = new RunWriter();
        var inFence = false;
        var fenceChar = '`';

        foreach (var rawLine in markdown.AsSpan().EnumerateLines())
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.TrimStart();

            if (inFence)
            {
                if (IsFence(trimmed, fenceChar))
                {
                    inFence = false;
                    writer.ParagraphBreak();
                    continue;
                }

                // Code is kept verbatim (including indentation and blank lines).
                writer.StartLine();
                writer.Append(line, RunStyle.CodeBlock);
                continue;
            }

            if (trimmed.IsEmpty)
            {
                writer.ParagraphBreak();
                continue;
            }

            if (IsFence(trimmed, '`') || IsFence(trimmed, '~'))
            {
                // An unclosed fence while streaming still renders what follows as code.
                inFence = true;
                fenceChar = trimmed[0];
                writer.ParagraphBreak();
                continue;
            }

            if (IsHorizontalRule(trimmed))
            {
                writer.ParagraphBreak();
                continue;
            }

            if (TryParseHeading(trimmed, out var headingLevel, out var headingText))
            {
                var headingStyle = headingLevel switch
                {
                    1 => RunStyle.Heading1,
                    2 => RunStyle.Heading2,
                    _ => RunStyle.Heading3,
                };
                writer.StartLine();
                ParseInline(writer, headingText, headingStyle);
                continue;
            }

            var indentLevel = Math.Min(CountIndent(line) / 2, 4);

            if (TryParseBullet(trimmed, out var bulletText))
            {
                writer.StartLine();
                WriteIndent(writer, indentLevel);
                writer.Append(Bullet, RunStyle.Accent);
                ParseInline(writer, bulletText, RunStyle.None);
                continue;
            }

            if (TryParseNumbered(trimmed, out var number, out var numberedText))
            {
                writer.StartLine();
                WriteIndent(writer, indentLevel);
                writer.Append(number, RunStyle.Accent);
                writer.Append(" ", RunStyle.None);
                ParseInline(writer, numberedText, RunStyle.None);
                continue;
            }

            if (trimmed[0] == '>')
            {
                writer.StartLine();
                writer.Append("▎ ", RunStyle.Accent);
                ParseInline(writer, trimmed[1..].TrimStart(), RunStyle.Italic);
                continue;
            }

            if (trimmed[0] == '|')
            {
                if (!IsTableSeparator(trimmed))
                {
                    writer.StartLine();
                    WriteTableRow(writer, trimmed);
                }

                continue;
            }

            writer.StartLine();
            ParseInline(writer, trimmed, RunStyle.None);
        }

        return writer.Finish();
    }

    private static void WriteIndent(RunWriter writer, int level)
    {
        for (var i = 0; i < level; i++)
        {
            writer.Append(IndentUnit, RunStyle.None);
        }
    }

    private static void WriteTableRow(RunWriter writer, ReadOnlySpan<char> row)
    {
        var cells = row.Trim('|');
        var first = true;
        while (true)
        {
            var bar = cells.IndexOf('|');
            var cell = (bar < 0 ? cells : cells[..bar]).Trim();
            if (!first)
            {
                writer.Append("  ·  ", RunStyle.Accent);
            }

            ParseInline(writer, cell, RunStyle.None);
            first = false;
            if (bar < 0)
            {
                break;
            }

            cells = cells[(bar + 1)..];
        }
    }

    private static int CountIndent(ReadOnlySpan<char> line)
    {
        var width = 0;
        foreach (var c in line)
        {
            if (c == ' ')
            {
                width++;
            }
            else if (c == '\t')
            {
                width += 4;
            }
            else
            {
                break;
            }
        }

        return width;
    }

    private static bool IsFence(ReadOnlySpan<char> trimmed, char fenceChar) =>
        trimmed.Length >= 3 && trimmed[0] == fenceChar && trimmed[1] == fenceChar && trimmed[2] == fenceChar;

    private static bool IsHorizontalRule(ReadOnlySpan<char> trimmed)
    {
        var marker = trimmed[0];
        if (marker is not ('-' or '*' or '_'))
        {
            return false;
        }

        var count = 0;
        foreach (var c in trimmed)
        {
            if (c == marker)
            {
                count++;
            }
            else if (c != ' ')
            {
                return false;
            }
        }

        return count >= 3;
    }

    private static bool IsTableSeparator(ReadOnlySpan<char> trimmed)
    {
        foreach (var c in trimmed)
        {
            if (c is not ('|' or '-' or ':' or ' '))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseHeading(ReadOnlySpan<char> trimmed, out int level, out ReadOnlySpan<char> text)
    {
        level = 0;
        while (level < trimmed.Length && trimmed[level] == '#')
        {
            level++;
        }

        if (level is >= 1 and <= 6 && level < trimmed.Length && trimmed[level] == ' ')
        {
            text = trimmed[(level + 1)..].Trim().TrimEnd('#').TrimEnd();
            return true;
        }

        text = default;
        return false;
    }

    private static bool TryParseBullet(ReadOnlySpan<char> trimmed, out ReadOnlySpan<char> text)
    {
        if (trimmed.Length >= 2 && trimmed[0] is ('-' or '*' or '+') && trimmed[1] == ' ')
        {
            text = trimmed[2..].TrimStart();
            return true;
        }

        text = default;
        return false;
    }

    private static bool TryParseNumbered(ReadOnlySpan<char> trimmed, out ReadOnlySpan<char> number, out ReadOnlySpan<char> text)
    {
        var digits = 0;
        while (digits < trimmed.Length && digits < 3 && char.IsAsciiDigit(trimmed[digits]))
        {
            digits++;
        }

        if (digits > 0 && digits + 1 < trimmed.Length && trimmed[digits] is ('.' or ')') && trimmed[digits + 1] == ' ')
        {
            number = trimmed[..(digits + 1)];
            text = trimmed[(digits + 2)..].TrimStart();
            return true;
        }

        number = default;
        text = default;
        return false;
    }

    /// <summary>Inline markup. Any marker without a valid closing partner is emitted literally.</summary>
    private static void ParseInline(RunWriter writer, ReadOnlySpan<char> text, RunStyle style)
    {
        var plainStart = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
            {
                writer.Append(text[plainStart..i], style);
                writer.Append(text.Slice(i + 1, 1), style);
                i += 2;
                plainStart = i;
                continue;
            }

            if (c == '`')
            {
                var ticks = CountRun(text, i, '`');
                var close = FindBacktickRun(text, i + ticks, ticks);
                if (close >= 0)
                {
                    writer.Append(text[plainStart..i], style);
                    var code = text[(i + ticks)..close];
                    if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && !code.IsWhiteSpace())
                    {
                        code = code[1..^1];
                    }

                    writer.Append(code, (style & ~(RunStyle.Bold | RunStyle.Italic | RunStyle.Accent)) | RunStyle.Code);
                    i = close + ticks;
                    plainStart = i;
                    continue;
                }

                i += ticks;
                continue;
            }

            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = i + 2 < text.Length && !char.IsWhiteSpace(text[i + 2])
                    ? FindClosingDouble(text, i + 2)
                    : -1;
                if (close > i + 2)
                {
                    writer.Append(text[plainStart..i], style);
                    ParseInline(writer, text[(i + 2)..close], style | RunStyle.Bold);
                    i = close + 2;
                    plainStart = i;
                    continue;
                }

                i += 2;
                continue;
            }

            if ((c == '*' || c == '_') && CanOpenSingle(text, i, c))
            {
                var close = FindClosingSingle(text, i + 1, c);
                if (close > i + 1)
                {
                    writer.Append(text[plainStart..i], style);
                    ParseInline(writer, text[(i + 1)..close], style | RunStyle.Italic);
                    i = close + 1;
                    plainStart = i;
                    continue;
                }
            }

            if (c == '[' && TryParseLink(text, i, out var labelEnd, out var linkEnd))
            {
                writer.Append(text[plainStart..i], style);
                ParseInline(writer, text[(i + 1)..labelEnd], style | RunStyle.Accent);
                i = linkEnd + 1;
                plainStart = i;
                continue;
            }

            i++;
        }

        writer.Append(text[plainStart..], style);
    }

    private static bool IsEscapable(char c) => c is '*' or '_' or '`' or '\\' or '[' or ']' or '#' or '|';

    private static int CountRun(ReadOnlySpan<char> text, int start, char c)
    {
        var end = start;
        while (end < text.Length && text[end] == c)
        {
            end++;
        }

        return end - start;
    }

    private static int FindBacktickRun(ReadOnlySpan<char> text, int from, int length)
    {
        var i = from;
        while (i < text.Length)
        {
            if (text[i] != '`')
            {
                i++;
                continue;
            }

            var run = CountRun(text, i, '`');
            if (run == length)
            {
                return i;
            }

            i += run;
        }

        return -1;
    }

    /// <summary>Closing <c>**</c> preceded by a non-space; for <c>***</c> the last two close (so <c>***x***</c> nests).</summary>
    private static int FindClosingDouble(ReadOnlySpan<char> text, int from)
    {
        for (var i = from + 1; i + 1 < text.Length; i++)
        {
            if (text[i] != '*' || text[i + 1] != '*' || char.IsWhiteSpace(text[i - 1]))
            {
                continue;
            }

            while (i + 2 < text.Length && text[i + 2] == '*')
            {
                i++;
            }

            return i;
        }

        return -1;
    }

    private static bool CanOpenSingle(ReadOnlySpan<char> text, int i, char marker)
    {
        if (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]) || text[i + 1] == marker)
        {
            return false;
        }

        // '_' inside words (snake_case) is not emphasis.
        return marker != '_' || i == 0 || !char.IsLetterOrDigit(text[i - 1]);
    }

    private static int FindClosingSingle(ReadOnlySpan<char> text, int from, char marker)
    {
        for (var i = from + 1; i < text.Length; i++)
        {
            if (text[i] != marker)
            {
                continue;
            }

            if (marker == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i++; // a nested **bold** pair, not our closer
                continue;
            }

            if (char.IsWhiteSpace(text[i - 1]))
            {
                continue;
            }

            if (marker == '_' && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]))
            {
                continue;
            }

            return i;
        }

        return -1;
    }

    private static bool TryParseLink(ReadOnlySpan<char> text, int open, out int labelEnd, out int linkEnd)
    {
        linkEnd = -1;
        labelEnd = text[(open + 1)..].IndexOf(']');
        if (labelEnd <= 0)
        {
            return false;
        }

        labelEnd += open + 1;
        if (labelEnd + 1 >= text.Length || text[labelEnd + 1] != '(')
        {
            return false;
        }

        var close = text[(labelEnd + 2)..].IndexOf(')');
        if (close < 0)
        {
            return false;
        }

        linkEnd = labelEnd + 2 + close;
        return true;
    }

    /// <summary>Collects runs, merging adjacent ones with the same style, and manages line/paragraph breaks.</summary>
    private sealed class RunWriter
    {
        private readonly List<MarkdownRun> _runs = [];
        private bool _hasContent;
        private bool _paragraphPending;

        /// <summary>Ends the previous line (with a blank line if a paragraph break is pending). No-op at the start.</summary>
        public void StartLine()
        {
            if (_hasContent)
            {
                AppendBreak(_paragraphPending ? "\n\n" : "\n");
            }

            _paragraphPending = false;
        }

        public void ParagraphBreak() => _paragraphPending = _hasContent;

        public void Append(ReadOnlySpan<char> text, RunStyle style)
        {
            if (text.IsEmpty)
            {
                return;
            }

            _hasContent = true;
            if (_runs.Count > 0 && _runs[^1].Style == style)
            {
                _runs[^1].Builder.Append(text);
                return;
            }

            _runs.Add(new MarkdownRun(style, text.ToString()));
        }

        public List<MarkdownRun> Finish() => _runs;

        private void AppendBreak(string breakText)
        {
            var last = _runs[^1];
            // Keep inline-code highlighting from bleeding onto the line break.
            if ((last.Style & RunStyle.Code) != 0)
            {
                _runs.Add(new MarkdownRun(RunStyle.None, breakText));
            }
            else
            {
                last.Builder.Append(breakText);
            }
        }
    }
}

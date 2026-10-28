using System.IO.Compression;
using System.Text;
using System.Xml;
using InterviewAssist.Core.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace InterviewAssist.Core.Documents;

/// <summary>Extracts plain text from resumes and job descriptions (.pdf, .docx, .txt, .md). Stateless and thread-safe.</summary>
public sealed class DocumentTextExtractor : IDocumentTextExtractor
{
    /// <summary>Resumes and job descriptions are tiny; this only guards against picking the wrong file.</summary>
    internal const long MaxFileBytes = 50L * 1024 * 1024;

    // Stops runaway extraction (e.g. a zip bomb posing as .docx); far beyond any real resume.
    private const int MaxExtractedChars = 2_000_000;

    private const string NoPdfTextMessage =
        "No selectable text found — this PDF may be a scanned image. Paste the text instead.";

    private const string DamagedPdfMessage =
        "This PDF is damaged or unsupported and couldn't be read. Paste the text instead.";

    private const string MarkupCompatibilityNs = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    private static readonly XmlReaderSettings DocxXmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    public IReadOnlyList<string> SupportedExtensions { get; } = [".pdf", ".docx", ".txt", ".md"];

    public async Task<string> ExtractAsync(Stream stream, string fileName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var extension = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
        {
            var shown = extension.Length > 0 ? $"'{extension}' files are" : "This file type is";
            throw new NotSupportedException($"{shown} not supported. Use a PDF, Word (.docx), .txt or .md file.");
        }

        // MAUI file streams may not be seekable (and PdfPig wants random access), so buffer the whole file.
        var bytes = await ReadAllBytesAsync(stream, ct).ConfigureAwait(false);

        return await Task.Run(
            () =>
            {
                var raw = extension switch
                {
                    ".pdf" => ExtractPdf(bytes, ct),
                    ".docx" => ExtractDocx(bytes, ct),
                    _ => DecodeText(bytes),
                };
                var text = Normalize(raw);
                if (text.Length == 0)
                {
                    throw new InvalidDataException(
                        extension == ".pdf" ? NoPdfTextMessage : "This file doesn't contain any text.");
                }
                return text;
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// CRLF/CR → LF, strips control characters (form feeds become line breaks), trims trailing whitespace on every
    /// line, collapses runs of blank lines to one, and trims the whole text.
    /// </summary>
    internal static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var sb = new StringBuilder(text.Length);
        var line = new StringBuilder();
        var blankLines = 0;

        void FlushLine()
        {
            var end = line.Length;
            while (end > 0 && char.IsWhiteSpace(line[end - 1]))
                end--;

            if (end == 0)
            {
                blankLines++;
            }
            else
            {
                if (sb.Length > 0)
                    sb.Append(blankLines > 0 ? "\n\n" : "\n");
                sb.Append(line, 0, end);
                blankLines = 0;
            }
            line.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                        i++;
                    FlushLine();
                    break;
                case '\n' or '\f' or '\v' or '\u2028' or '\u2029' or '\u0085':
                    FlushLine();
                    break;
                case '\t':
                    line.Append(c);
                    break;
                case '\uFEFF':
                    break;
                default:
                    if (!char.IsControl(c))
                        line.Append(c);
                    break;
            }
        }
        FlushLine();

        // Only the first line can still have leading whitespace worth trimming.
        var start = 0;
        while (start < sb.Length && char.IsWhiteSpace(sb[start]))
            start++;
        return start == 0 ? sb.ToString() : sb.ToString(start, sb.Length - start);
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            if (stream.CanSeek && stream.Length - stream.Position > MaxFileBytes)
                throw new InvalidDataException(TooLargeMessage());

            using var memory = new MemoryStream(stream.CanSeek ? (int)Math.Max(0, stream.Length - stream.Position) : 0);
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + read > MaxFileBytes)
                    throw new InvalidDataException(TooLargeMessage());
                memory.Write(buffer, 0, read);
            }
            return memory.ToArray();
        }
        catch (IOException ex)
        {
            throw new InvalidDataException($"The file couldn't be read: {ex.Message}", ex);
        }
    }

    private static string TooLargeMessage() =>
        $"This file is too large (over {MaxFileBytes / (1024 * 1024)} MB). Pick a smaller file or paste the text instead.";

    /// <summary>Decodes text honoring a UTF-8/UTF-16/UTF-32 BOM; without one, UTF-8 (falling back to Latin-1 if invalid).</summary>
    internal static string DecodeText(byte[] bytes)
    {
        try
        {
            using var reader = new StreamReader(
                new MemoryStream(bytes, writable: false),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            // Legacy ANSI files (e.g. Windows-1252) — Latin-1 keeps every byte and gets almost all of them right.
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static string ExtractDocx(byte[] bytes, CancellationToken ct)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                "This isn't a valid .docx file. It may be damaged, password-protected, or an old .doc file — save it as .docx or paste the text instead.",
                ex);
        }

        using (zip)
        {
            var entry = zip.GetEntry("word/document.xml")
                ?? throw new InvalidDataException("This .docx file has no document body.");
            try
            {
                using var entryStream = entry.Open();
                return ReadDocumentXml(entryStream, ct);
            }
            catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException)
            {
                throw new InvalidDataException("This .docx file is damaged and couldn't be read. Paste the text instead.", ex);
            }
        }
    }

    /// <summary>Walks WordprocessingML: text from w:t, tabs from w:tab, line breaks from w:br/w:cr and paragraph ends.</summary>
    internal static string ReadDocumentXml(Stream xml, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var inText = false;
        var tabStopsDepth = 0; // w:tab inside w:tabs (paragraph properties) is a tab stop definition, not a tab.
        var fallbackDepth = 0; // mc:Fallback repeats mc:Choice content (text boxes in resume templates) — skip it.

        using var reader = XmlReader.Create(xml, DocxXmlSettings);
        while (reader.Read())
        {
            if (reader.NamespaceURI == MarkupCompatibilityNs && reader.LocalName == "Fallback")
            {
                if (reader.NodeType == XmlNodeType.Element && !reader.IsEmptyElement)
                    fallbackDepth++;
                else if (reader.NodeType == XmlNodeType.EndElement)
                    fallbackDepth = Math.Max(0, fallbackDepth - 1);
                continue;
            }
            if (fallbackDepth > 0)
                continue;

            switch (reader.NodeType)
            {
                case XmlNodeType.Element when IsWordNamespace(reader.NamespaceURI):
                    switch (reader.LocalName)
                    {
                        case "t":
                            inText = !reader.IsEmptyElement;
                            break;
                        case "tabs":
                            if (!reader.IsEmptyElement)
                                tabStopsDepth++;
                            break;
                        case "tab" when tabStopsDepth == 0:
                            sb.Append('\t');
                            break;
                        case "br":
                        case "cr":
                            sb.Append('\n');
                            break;
                        case "noBreakHyphen":
                            sb.Append('-');
                            break;
                        case "p" when reader.IsEmptyElement:
                            sb.Append('\n');
                            break;
                    }
                    break;

                case XmlNodeType.EndElement when IsWordNamespace(reader.NamespaceURI):
                    switch (reader.LocalName)
                    {
                        case "t":
                            inText = false;
                            break;
                        case "tabs":
                            tabStopsDepth = Math.Max(0, tabStopsDepth - 1);
                            break;
                        case "p":
                            sb.Append('\n');
                            ct.ThrowIfCancellationRequested();
                            if (sb.Length > MaxExtractedChars)
                                return sb.ToString();
                            break;
                    }
                    break;

                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace
                    when inText:
                    sb.Append(reader.Value);
                    break;
            }
        }
        return sb.ToString();
    }

    // Transitional (the usual) and Strict OOXML WordprocessingML namespaces.
    private static bool IsWordNamespace(string ns) =>
        ns is "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
            or "http://purl.oclc.org/ooxml/wordprocessingml/main";

    private static string ExtractPdf(byte[] bytes, CancellationToken ct)
    {
        try
        {
            // SkipMissingFonts: a page with an unresolvable font still yields the rest of its text instead of throwing.
            using var document = PdfDocument.Open(bytes, new ParsingOptions { SkipMissingFonts = true });
            var sb = new StringBuilder();
            Exception? pageError = null;
            for (var number = 1; number <= document.NumberOfPages; number++)
            {
                ct.ThrowIfCancellationRequested();

                string text;
                try
                {
                    text = ExtractPdfPage(document.GetPage(number));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One broken page shouldn't lose the rest of the document.
                    pageError ??= ex;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (sb.Length > 0)
                        sb.Append("\n\n");
                    sb.Append(text.Trim());
                    if (sb.Length > MaxExtractedChars)
                        break;
                }
            }

            if (sb.Length == 0)
            {
                if (pageError is not null)
                    throw new InvalidDataException(DamagedPdfMessage, pageError);
                throw new InvalidDataException(NoPdfTextMessage);
            }
            return sb.ToString();
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new InvalidDataException(
                "This PDF is password-protected. Remove the password (or print it to a new PDF) or paste the text instead.", ex);
        }
        catch (Exception ex) when (ex is not InvalidDataException and not OperationCanceledException)
        {
            // PdfPig throws a variety of exception types for malformed files.
            throw new InvalidDataException(DamagedPdfMessage, ex);
        }
    }

    /// <summary>Text in content order (keeps reading order for most resumes), falling back to PdfPig's raw page text.</summary>
    private static string ExtractPdfPage(Page page)
    {
        try
        {
            var text = ContentOrderTextExtractor.GetText(page);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fall through to the simpler extraction below.
        }
        return page.Text;
    }
}

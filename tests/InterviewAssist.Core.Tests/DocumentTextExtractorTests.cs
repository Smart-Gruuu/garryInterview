using System.IO.Compression;
using System.Text;
using InterviewAssist.Core.Documents;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace InterviewAssist.Core.Tests;

public class DocumentTextExtractorTests
{
    private const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private readonly DocumentTextExtractor _extractor = new();

    private Task<string> ExtractAsync(byte[] bytes, string fileName) =>
        _extractor.ExtractAsync(new MemoryStream(bytes), fileName);

    private static byte[] Docx(string bodyXml, string entryName = "word/document.xml", string ns = WordNs)
    {
        var xml =
            $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="{ns}"><w:body>{bodyXml}</w:body></w:document>""";
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("[Content_Types].xml").Open().Dispose();
            using var writer = new StreamWriter(zip.CreateEntry(entryName).Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }
        return memory.ToArray();
    }

    private static byte[] Pdf(params string[][] pages)
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            var y = 780.0;
            foreach (var line in lines)
            {
                page.AddText(line, 12, new PdfPoint(50, y), font);
                y -= 20;
            }
        }
        return builder.Build();
    }

    [Fact]
    public void Supported_extensions_are_listed()
    {
        Assert.Equal([".pdf", ".docx", ".txt", ".md"], _extractor.SupportedExtensions);
    }

    // ---- plain text ----

    [Fact]
    public async Task Txt_with_utf8_bom_is_decoded_without_the_bom()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Résumé — José")).ToArray();

        Assert.Equal("Résumé — José", await ExtractAsync(bytes, "resume.txt"));
    }

    [Fact]
    public async Task Txt_with_utf16_bom_is_decoded()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Zoë 日本語")).ToArray();

        Assert.Equal("Zoë 日本語", await ExtractAsync(bytes, "notes.txt"));
    }

    [Fact]
    public async Task Txt_without_bom_defaults_to_utf8()
    {
        Assert.Equal("naïve café", await ExtractAsync(Encoding.UTF8.GetBytes("naïve café"), "a.txt"));
    }

    [Fact]
    public async Task Invalid_utf8_falls_back_to_latin1()
    {
        byte[] bytes = [0x43, 0x61, 0x66, 0xE9]; // "Café" in Windows-1252 / Latin-1

        Assert.Equal("Café", await ExtractAsync(bytes, "legacy.txt"));
    }

    [Fact]
    public async Task Markdown_and_uppercase_extensions_are_supported()
    {
        Assert.Equal("# Title", await ExtractAsync(Encoding.UTF8.GetBytes("# Title\n"), "README.MD"));
        Assert.Equal("text", await ExtractAsync(Encoding.UTF8.GetBytes("text"), "FILE.TXT"));
    }

    [Fact]
    public async Task Text_is_normalized()
    {
        const string raw = "\n\n  Jane Doe   \r\n\r\n\r\n\r\nSenior Engineer\t \r\n  - C#  \rSQL\n\n\n\n\f Last line \n\n";

        var text = await ExtractAsync(Encoding.UTF8.GetBytes(raw), "resume.txt");

        Assert.Equal("Jane Doe\n\nSenior Engineer\n  - C#\nSQL\n\n Last line", text);
    }

    [Fact]
    public void Normalize_drops_control_characters_but_keeps_tabs()
    {
        Assert.Equal("a\tb c", DocumentTextExtractor.Normalize("a\tb\0 c\u0007"));
    }

    [Fact]
    public async Task Empty_text_file_is_rejected()
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ExtractAsync(Encoding.UTF8.GetBytes(" \r\n \n"), "empty.txt"));

        Assert.Equal("This file doesn't contain any text.", ex.Message);
    }

    [Theory]
    [InlineData("resume.doc")]
    [InlineData("resume.rtf")]
    [InlineData("resume")]
    [InlineData("")]
    public async Task Unsupported_extensions_throw_not_supported(string fileName)
    {
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => ExtractAsync([1, 2, 3], fileName));

        Assert.Contains("not supported", ex.Message);
    }

    [Fact]
    public async Task Non_seekable_streams_are_supported()
    {
        await using var stream = new NonSeekableStream(Encoding.UTF8.GetBytes("streamed text"));

        Assert.Equal("streamed text", await _extractor.ExtractAsync(stream, "x.txt"));
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _extractor.ExtractAsync(new MemoryStream(Encoding.UTF8.GetBytes("x")), "x.txt", cts.Token));
    }

    // ---- docx ----

    [Fact]
    public async Task Docx_text_tabs_breaks_and_paragraphs_are_extracted()
    {
        const string body =
            """<w:p><w:pPr><w:tabs><w:tab w:val="left" w:pos="720"/></w:tabs></w:pPr><w:r><w:t>Jane</w:t></w:r><w:r><w:t xml:space="preserve"> </w:t></w:r><w:r><w:t>Doe</w:t></w:r></w:p>""" +
            """<w:p><w:r><w:t>Skills:</w:t><w:tab/><w:t>C#</w:t><w:br/><w:t>SQL</w:t><w:cr/><w:t xml:space="preserve">  Azure</w:t></w:r></w:p>""" +
            """<w:p/><w:p/><w:p/>""" +
            """<w:p><w:r><w:t>Experience</w:t></w:r><w:r><w:delText>deleted words</w:delText></w:r><w:r><w:instrText> PAGE </w:instrText></w:r></w:p>""" +
            """<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Cell A</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Cell B</w:t></w:r></w:p></w:tc></w:tr></w:tbl>""";

        var text = await ExtractAsync(Docx(body), "resume.docx");

        Assert.Equal("Jane Doe\nSkills:\tC#\nSQL\n  Azure\n\nExperience\nCell A\nCell B", text);
    }

    [Fact]
    public async Task Docx_text_box_fallback_copies_are_not_duplicated()
    {
        const string body =
            """<w:p><w:r><mc:AlternateContent xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006">""" +
            """<mc:Choice Requires="wps"><w:drawing><w:txbxContent><w:p><w:r><w:t>Box text</w:t></w:r></w:p></w:txbxContent></w:drawing></mc:Choice>""" +
            """<mc:Fallback><w:pict><w:txbxContent><w:p><w:r><w:t>Box text</w:t></w:r></w:p></w:txbxContent></w:pict></mc:Fallback>""" +
            """</mc:AlternateContent></w:r><w:r><w:t>e</w:t><w:noBreakHyphen/><w:t>commerce</w:t></w:r></w:p>""";

        var text = await ExtractAsync(Docx(body), "template.docx");

        Assert.Equal("Box text\ne-commerce", text);
    }

    [Fact]
    public async Task Docx_in_strict_ooxml_namespace_is_extracted()
    {
        var bytes = Docx("<w:p><w:r><w:t>Strict text</w:t></w:r></w:p>", ns: "http://purl.oclc.org/ooxml/wordprocessingml/main");

        Assert.Equal("Strict text", await ExtractAsync(bytes, "strict.docx"));
    }

    [Fact]
    public async Task Docx_without_document_body_is_rejected()
    {
        var bytes = Docx("<w:p/>", entryName: "word/other.xml");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ExtractAsync(bytes, "broken.docx"));

        Assert.Equal("This .docx file has no document body.", ex.Message);
    }

    [Fact]
    public async Task File_that_is_not_a_zip_is_rejected_as_docx()
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => ExtractAsync(Encoding.UTF8.GetBytes("this is really a .doc file"), "old.docx"));

        Assert.Contains(".docx", ex.Message);
    }

    [Fact]
    public async Task Docx_with_malformed_xml_is_rejected()
    {
        var bytes = Docx("<w:p><w:r><w:t>unclosed</w:r></w:p>");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ExtractAsync(bytes, "bad.docx"));

        Assert.Contains("damaged", ex.Message);
    }

    [Fact]
    public async Task Docx_without_text_is_rejected()
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ExtractAsync(Docx("<w:p/><w:p/>"), "blank.docx"));

        Assert.Equal("This file doesn't contain any text.", ex.Message);
    }

    // ---- pdf ----

    [Fact]
    public async Task Pdf_text_is_extracted_with_pages_separated_by_a_blank_line()
    {
        var bytes = Pdf(["Jane Doe", "Senior Engineer"], ["Page two"]);

        var text = await ExtractAsync(bytes, "resume.pdf");

        Assert.Contains("Jane Doe", text);
        Assert.Contains("Senior Engineer", text);
        Assert.EndsWith("\n\nPage two", text);
        Assert.True(text.IndexOf("Jane Doe", StringComparison.Ordinal) < text.IndexOf("Senior Engineer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pdf_without_text_is_reported_as_scanned()
    {
        var bytes = Pdf([], []);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ExtractAsync(bytes, "scan.pdf"));

        Assert.Equal("No selectable text found — this PDF may be a scanned image. Paste the text instead.", ex.Message);
    }

    [Fact]
    public async Task Corrupt_pdf_is_rejected_with_a_friendly_message()
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => ExtractAsync(Encoding.ASCII.GetBytes("%PDF-1.7\nthis is not really a pdf"), "broken.pdf"));

        Assert.Contains("PDF", ex.Message);
        Assert.Contains("Paste the text instead", ex.Message);
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 3));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

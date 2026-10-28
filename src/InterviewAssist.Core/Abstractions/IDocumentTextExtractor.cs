namespace InterviewAssist.Core.Abstractions;

/// <summary>Extracts plain text from a resume / job description file (.txt, .md, .pdf, .docx).</summary>
public interface IDocumentTextExtractor
{
    /// <summary>Lower-case extensions including the dot, e.g. ".pdf".</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Returns normalized text (trimmed lines, no runs of more than one blank line). Throws
    /// <see cref="NotSupportedException"/> for other extensions and <see cref="InvalidDataException"/> with a
    /// user-facing message when the file can't be read (corrupt, encrypted, or a scanned PDF with no text).
    /// </summary>
    Task<string> ExtractAsync(Stream stream, string fileName, CancellationToken ct = default);
}

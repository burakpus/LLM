namespace SetYazilim.Llm.Api.Admin;

public static class DocumentParser
{
    /// <summary>
    /// Extracts plain text from uploaded file bytes based on content type or filename extension.
    /// Supported: .txt, .md, .pdf, .docx
    /// </summary>
    public static string ExtractText(byte[] data, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf"  => ExtractPdf(data),
            ".docx" => ExtractDocx(data),
            _       => System.Text.Encoding.UTF8.GetString(data) // txt, md, etc.
        };
    }

    private static string ExtractPdf(byte[] data)
    {
        var sb = new System.Text.StringBuilder();
        using var doc = UglyToad.PdfPig.PdfDocument.Open(data);
        foreach (var page in doc.GetPages())
            sb.AppendLine(string.Join(" ", page.GetWords().Select(w => w.Text)));
        return sb.ToString();
    }

    private static string ExtractDocx(byte[] data)
    {
        var sb = new System.Text.StringBuilder();
        using var ms = new MemoryStream(data);
        using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart?.Document.Body;
        if (body == null) return string.Empty;
        foreach (var para in body.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
            sb.AppendLine(para.InnerText);
        return sb.ToString();
    }
}

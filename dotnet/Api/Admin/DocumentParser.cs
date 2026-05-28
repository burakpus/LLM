namespace SetYazilim.Llm.Api.Admin;

public static class DocumentParser
{
    /// <summary>
    /// Image formats that go straight to LiteParse (OCR-only — no native fallback).
    /// </summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".tif", ".webp",
    };

    /// <summary>
    /// Minimum char count below which a PDF parse result is considered "empty"
    /// and triggers a LiteParse OCR fallback. Tuned for typical scanned PDFs
    /// (whose PdfPig output is usually 0-10 chars of stray text).
    /// </summary>
    private const int PdfFallbackThreshold = 60;

    /// <summary>
    /// Synchronous text extraction — kept for backward compatibility with callers
    /// that don't have an async path (e.g. agent tool dispatcher). Uses native
    /// parsers only; no OCR. New code should prefer <see cref="ExtractTextAsync"/>.
    /// </summary>
    public static string ExtractText(byte[] data, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf"        => ExtractPdf(data),
            ".docx"       => ExtractDocx(data),
            ".xlsx"       => ExtractXlsx(data),
            ".xls"        => "[.xls formatı desteklenmiyor. Lütfen .xlsx olarak kaydedin.]",
            ".doc"        => "[.doc formatı desteklenmiyor. Lütfen .docx olarak kaydedin.]",
            _ when ImageExtensions.Contains(ext) =>
                "[Resim dosyaları için OCR gerekli — LiteParseInvoker enjekte edilmemiş çağrıdır.]",
            _             => System.Text.Encoding.UTF8.GetString(data) // txt, md, csv, etc.
        };
    }

    /// <summary>
    /// Async-capable extraction with optional LiteParse OCR fallback:
    ///  - PDF: try PdfPig first; if result is empty / very short, retry via LiteParse
    ///  - Images (jpg/png/tiff/...): straight to LiteParse OCR
    ///  - DOCX/XLSX/TXT/MD/CSV: same as sync path (no OCR needed)
    ///
    /// Pass <c>null</c> for <paramref name="liteParse"/> to disable OCR — behaves
    /// identically to <see cref="ExtractText"/>.
    /// </summary>
    public static async Task<string> ExtractTextAsync(
        byte[] data, string fileName, LiteParseInvoker? liteParse, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        if (ImageExtensions.Contains(ext))
        {
            if (liteParse is null)
                return "[Resim dosyası — LiteParse OCR gerekli, mevcut değil.]";
            var ocr = await liteParse.ParseAsync(data, fileName, ct);
            return ocr ?? "[Resim OCR başarısız oldu.]";
        }

        if (ext == ".pdf")
        {
            var native = ExtractPdf(data);
            if (native.Length >= PdfFallbackThreshold) return native;
            // Empty or near-empty result → probably scanned PDF; try OCR
            if (liteParse is null) return native;
            var ocr = await liteParse.ParseAsync(data, fileName, ct);
            return string.IsNullOrWhiteSpace(ocr) ? native : ocr!;
        }

        return ExtractText(data, fileName);   // docx/xlsx/txt/csv — already optimal
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

    private static string ExtractXlsx(byte[] data)
    {
        var sb = new System.Text.StringBuilder();
        using var ms = new MemoryStream(data);
        using var doc = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(ms, false);

        var workbook = doc.WorkbookPart;
        if (workbook == null) return string.Empty;

        // Shared strings lookup (Excel stores unique strings in a shared table)
        var sharedStrings = workbook.SharedStringTablePart?.SharedStringTable
            .Elements<DocumentFormat.OpenXml.Spreadsheet.SharedStringItem>()
            .Select(x => x.InnerText)
            .ToArray() ?? [];

        // Map relationship ID → sheet display name
        var sheetNames = workbook.Workbook.Sheets?
            .Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>()
            .ToDictionary(s => s.Id?.Value ?? string.Empty, s => s.Name?.Value ?? "Sheet")
            ?? [];

        foreach (var sheetPart in workbook.WorksheetParts)
        {
            var relId     = workbook.GetIdOfPart(sheetPart);
            var sheetName = sheetNames.TryGetValue(relId, out var n) ? n : relId;
            sb.AppendLine($"# {sheetName}");

            var rows = sheetPart.Worksheet
                .GetFirstChild<DocumentFormat.OpenXml.Spreadsheet.SheetData>()
                ?.Elements<DocumentFormat.OpenXml.Spreadsheet.Row>();

            if (rows == null) { sb.AppendLine(); continue; }

            foreach (var row in rows)
            {
                var values = row.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>()
                    .Select(c =>
                    {
                        var val = c.CellValue?.Text ?? string.Empty;
                        if (c.DataType?.Value == DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString
                            && int.TryParse(val, out var idx)
                            && idx >= 0 && idx < sharedStrings.Length)
                            val = sharedStrings[idx];
                        return val;
                    });
                sb.AppendLine(string.Join("\t", values));
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

namespace SetYazilim.Llm.Api.Admin;

public static class DocumentParser
{
    /// <summary>
    /// Extracts plain text from uploaded file bytes based on content type or filename extension.
    /// Supported: .txt, .md, .csv, .pdf, .docx, .doc, .xlsx, .xls
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
            _             => System.Text.Encoding.UTF8.GetString(data) // txt, md, csv, etc.
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

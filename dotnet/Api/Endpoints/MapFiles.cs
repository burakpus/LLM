using Microsoft.AspNetCore.Authorization;
using SetYazilim.Llm.Api.Admin;

namespace SetYazilim.Llm.Api.Endpoints;

/// <summary>
/// /api/files/extract — parse uploaded document (.txt, .md, .csv, .pdf, .docx, .xlsx)
/// and return plain text. Used to inject document content into chat context.
/// </summary>
public static class FilesEndpoints
{
    public static IEndpointRouteBuilder MapFiles(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/files/extract", [Authorize] async (HttpContext http, CancellationToken ct) =>
        {
            if (!http.Request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required" });

            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file == null)
                return Results.BadRequest(new { error = "No file uploaded" });

            const int MaxChars = 16_000; // ~4000 tokens

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);

            string text;
            try
            {
                text = DocumentParser.ExtractText(ms.ToArray(), file.FileName);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"Dosya okunamadı: {ex.Message}" });
            }

            var truncated = text.Length > MaxChars;
            if (truncated) text = text[..MaxChars];

            return Results.Ok(new { filename = file.FileName, text, truncated });
        });

        return app;
    }
}

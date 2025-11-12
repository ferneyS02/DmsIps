using DocumentFormat.OpenXml.Packaging;
using Microsoft.AspNetCore.Http;
using System.Text;
using System.Text.RegularExpressions;

// 👇 PdfPig
using UglyToad.PdfPig;

namespace DmsContayPerezIPS.API.Services
{
    public class PdfDocxTextExtractor : ITextExtractor
    {
        public async Task<string> ExtractAsync(IFormFile file, CancellationToken ct = default)
        {
            if (file == null || file.Length == 0) return string.Empty;

            var name = file.FileName?.ToLowerInvariant() ?? "";
            var ctType = file.ContentType?.ToLowerInvariant() ?? "";

            if (name.EndsWith(".pdf") || ctType.Contains("pdf"))
                return await ExtractPdfAsync(file, ct);

            if (name.EndsWith(".docx") || ctType.Contains("officedocument.wordprocessingml.document"))
                return await ExtractDocxAsync(file, ct);

            return string.Empty;
        }

        private static async Task<string> ExtractPdfAsync(IFormFile file, CancellationToken ct)
        {
            // PdfPig necesita stream "seekable": copiamos a memoria
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            ms.Position = 0;

            var sb = new StringBuilder();
            using var pdf = PdfDocument.Open(ms);
            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                var text = page.Text;
                if (!string.IsNullOrWhiteSpace(text))
                    sb.AppendLine(text);
            }
            return Normalize(sb.ToString());
        }

        private static async Task<string> ExtractDocxAsync(IFormFile file, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            ms.Position = 0;

            using var doc = WordprocessingDocument.Open(ms, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            return Normalize(body?.InnerText ?? string.Empty);
        }

        private static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            text = text.Replace("\0", " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            const int maxChars = 1_000_000;
            if (text.Length > maxChars) text = text[..maxChars];
            return text;
        }
    }
}

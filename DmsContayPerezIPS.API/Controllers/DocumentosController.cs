using System.Text.Json;
using DmsContayPerezIPS.Domain.Entities;
using DmsContayPerezIPS.Infrastructure.Persistence;
using DmsContayPerezIPS.API.Services; // ITextExtractor + SpanishDateParser (si lo tienes aquí)
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;  // EF.Functions
using Minio;
using Minio.DataModel.Args;

namespace DmsContayPerezIPS.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentosController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IMinioClient _minio;
        private readonly string _bucket;
        private readonly ITextExtractor _textExtractor;

        public DocumentosController(
            AppDbContext db,
            IMinioClient minio,
            IConfiguration config,
            ITextExtractor textExtractor)
        {
            _db = db;
            _minio = minio;
            _bucket = config["MinIO:Bucket"] ?? "dms";
            _textExtractor = textExtractor;
        }

        // ==========================================================
        // 🔐 Subida de documentos (firma compatible con Swagger)
        // ==========================================================
        [HttpPost("upload")]
        [Authorize]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> Upload(
            IFormFile file,                // sin [FromForm] para evitar el bug de Swashbuckle
            [FromForm] long tipoDocId,
            [FromForm] string? documentDate = null)
        {
            if (file == null || file.Length == 0)
                return BadRequest("Archivo inválido");

            if (tipoDocId <= 0)
                return BadRequest("tipoDocId inválido.");

            // Validar que el TipoDocumental exista
            var tipoDoc = await _db.TiposDocumentales.FindAsync(tipoDocId);
            if (tipoDoc == null)
                return BadRequest("El tipo documental no existe");

            // Verificar/crear bucket en MinIO
            bool bucketExists = await _minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(_bucket));
            if (!bucketExists)
                await _minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(_bucket));

            var objectName = $"{Guid.NewGuid()}_{file.FileName}";

            // Subir a MinIO
            using (var stream = file.OpenReadStream())
            {
                var contentType = string.IsNullOrWhiteSpace(file.ContentType)
                    ? "application/octet-stream"
                    : file.ContentType;

                await _minio.PutObjectAsync(new PutObjectArgs()
                    .WithBucket(_bucket)
                    .WithObject(objectName)
                    .WithStreamData(stream)
                    .WithObjectSize(file.Length)
                    .WithContentType(contentType));
            }

            // ✅ Parseo y normalización de fechas
            DateTime? parsedDocDate = null;
            if (!string.IsNullOrWhiteSpace(documentDate) &&
                SpanishDateParser.TryParse(documentDate, out var parsed))
            {
                parsedDocDate = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
            }

            var createdAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

            // ✅ EXTRAER TEXTO PARA FTS (PDF/DOCX)
            var extractedText = await _textExtractor.ExtractAsync(file, HttpContext.RequestAborted);
            var safeSearchText = string.IsNullOrWhiteSpace(extractedText) ? string.Empty : extractedText;

            // Crear documento
            var doc = new Document
            {
                OriginalName = file.FileName,
                ObjectName = objectName,
                ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                SizeBytes = file.Length,
                CurrentVersion = 1,
                CreatedAt = createdAt,
                DocumentDate = parsedDocDate,
                TipoDocId = tipoDocId,
                SearchText = safeSearchText, // 👈 clave para FTS (no null)
                MetadataJson = JsonSerializer.Serialize(new
                {
                    DocumentDate = parsedDocDate,
                    UploadedBy = User.Identity?.Name ?? "anon"
                })
            };

            _db.Documents.Add(doc);
            await _db.SaveChangesAsync();

            return Ok(new { message = "Archivo subido", docId = doc.Id, objectName = doc.ObjectName });
        }

        // ==========================================================
        // 🔐 Listado simple
        // ==========================================================
        [HttpGet("list")]
        [Authorize]
        public IActionResult ListDocuments()
        {
            var docs = _db.Documents.Select(d => new
            {
                d.Id,
                d.OriginalName,
                d.ContentType,
                d.SizeBytes,
                d.CreatedAt,
                d.DocumentDate
            });

            return Ok(docs);
        }

        // ==========================================================
        // 🔐 Descarga de documento
        // ==========================================================
        [HttpGet("download/{id}")]
        [Authorize]
        public async Task<IActionResult> Download(long id)
        {
            var doc = _db.Documents.FirstOrDefault(d => d.Id == id);
            if (doc == null) return NotFound("Documento no encontrado");

            var ms = new MemoryStream();

            await _minio.GetObjectAsync(new GetObjectArgs()
                .WithBucket(_bucket)
                .WithObject(doc.ObjectName)
                .WithCallbackStream(stream => stream.CopyTo(ms)));

            ms.Position = 0;
            return File(ms, doc.ContentType, doc.OriginalName);
        }

        // ==========================================================
        // 🔐 Búsqueda avanzada (por metadatos/TRD)
        // ==========================================================
        [HttpGet("search")]
        [Authorize]
        public IActionResult Search(
            string? name,
            DateTime? fromUpload,
            DateTime? toUpload,
            string? fromDoc,
            string? toDoc,
            long? serieId,
            long? subserieId,
            long? tipoDocId,
            string? metadata)
        {
            var query = _db.Documents.AsQueryable();

            // ✅ case-insensitive nativo en PostgreSQL (evita ToLower/ToLowerInvariant)
            if (!string.IsNullOrWhiteSpace(name))
                query = query.Where(d => EF.Functions.ILike(d.OriginalName ?? "", $"%{name}%"));

            if (fromUpload.HasValue)
                query = query.Where(d => d.CreatedAt >= fromUpload.Value);

            if (toUpload.HasValue)
                query = query.Where(d => d.CreatedAt <= toUpload.Value);

            if (!string.IsNullOrWhiteSpace(fromDoc) && SpanishDateParser.TryParse(fromDoc, out var fdoc))
                query = query.Where(d => d.DocumentDate >= DateTime.SpecifyKind(fdoc, DateTimeKind.Unspecified));

            if (!string.IsNullOrWhiteSpace(toDoc) && SpanishDateParser.TryParse(toDoc, out var tdoc))
                query = query.Where(d => d.DocumentDate <= DateTime.SpecifyKind(tdoc, DateTimeKind.Unspecified));

            if (tipoDocId.HasValue)
                query = query.Where(d => d.TipoDocId == tipoDocId.Value);
            else if (subserieId.HasValue)
                query = query.Where(d => d.TipoDocumental != null && d.TipoDocumental.SubserieId == subserieId.Value);
            else if (serieId.HasValue)
                query = query.Where(d => d.TipoDocumental != null &&
                                         d.TipoDocumental.Subserie != null &&
                                         d.TipoDocumental.Subserie.SerieId == serieId.Value);

            if (!string.IsNullOrWhiteSpace(metadata))
                query = query.Where(d => EF.Functions.ILike(d.MetadataJson ?? "", $"%{metadata}%"));

            var results = query.Select(d => new
            {
                d.Id,
                d.OriginalName,
                d.ContentType,
                d.SizeBytes,
                d.CreatedAt,
                d.DocumentDate,
                Tipo = d.TipoDocumental == null ? null : d.TipoDocumental.Nombre,
                Subserie = d.TipoDocumental != null && d.TipoDocumental.Subserie != null
                    ? d.TipoDocumental.Subserie.Nombre
                    : null,
                Serie = d.TipoDocumental != null && d.TipoDocumental.Subserie != null &&
                        d.TipoDocumental.Subserie.Serie != null
                    ? d.TipoDocumental.Subserie.Serie.Nombre
                    : null,
                d.MetadataJson
            }).ToList();

            return Ok(results);
        }

        // ==========================================================
        // 🔐 Full-Text Search (tsvector español + índice GIN)
        // ==========================================================
        [HttpGet("fulltext")]
        [Authorize]
        public async Task<IActionResult> FullTextSearch([FromQuery] string q)
        {
            if (string.IsNullOrWhiteSpace(q))
                return BadRequest("Ingrese una consulta (q).");

            var results = await _db.Documents
                .Where(d => d.SearchVector != null &&
                            d.SearchVector.Matches(EF.Functions.PlainToTsQuery("spanish", q)))
                .Select(d => new
                {
                    d.Id,
                    d.OriginalName,
                    d.ContentType,
                    d.SizeBytes,
                    d.CreatedAt,
                    d.DocumentDate,
                    Snippet = !string.IsNullOrEmpty(d.SearchText) && d.SearchText.Length > 240
                        ? d.SearchText.Substring(0, 240) + "..."
                        : d.SearchText
                })
                .ToListAsync();

            return Ok(results);
        }
    }
}

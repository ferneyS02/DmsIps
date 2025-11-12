using System.Security.Claims;
using System.Text.Json;
using DmsContayPerezIPS.API.Authorization;        // ← AllowedSeries() / IsAdmin()
using DmsContayPerezIPS.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;

namespace DmsContayPerezIPS.API.Controllers
{
    [Authorize] // requiere token
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentosController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IMinioClient _minio;
        private readonly IConfiguration _config;

        public DocumentosController(AppDbContext db, IMinioClient minio, IConfiguration config)
        {
            _db = db;
            _minio = minio;
            _config = config;
        }

        /// <summary>
        /// Lista documentos visibles según el rol (Admin ve todo).
        /// Filtros opcionales: serieId, subserieId, tipoDocId, q (texto).
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Get(
            [FromQuery] long? serieId = null,
            [FromQuery] long? subserieId = null,
            [FromQuery] long? tipoDocId = null,
            [FromQuery] string? q = null)
        {
            var allowed = User.AllowedSeries();

            var query = _db.Documents
                .AsNoTracking()
                .Include(d => d.TipoDoc!)
                    .ThenInclude(td => td.Subserie!)
                        .ThenInclude(s => s.Serie)
                .Where(d => !d.IsDeleted)
                .AsQueryable();

            // Filtro por serie según rol (si no es Admin)
            if (!User.IsAdmin())
                query = query.Where(d => d.TipoDoc != null &&
                                         d.TipoDoc.Subserie != null &&
                                         allowed.Contains(d.TipoDoc.Subserie.SerieId));

            // Filtros opcionales
            if (serieId.HasValue)
                query = query.Where(d => d.TipoDoc!.Subserie!.SerieId == serieId.Value);

            if (subserieId.HasValue)
                query = query.Where(d => d.TipoDoc!.SubserieId == subserieId.Value);

            if (tipoDocId.HasValue)
                query = query.Where(d => d.TipoDocId == tipoDocId.Value);

            if (!string.IsNullOrWhiteSpace(q))
            {
                q = q.Trim();
                // Búsqueda simple por texto (si ya tienes full-text, puedes cambiar aquí)
                query = query.Where(d =>
                    (d.SearchText != null && d.SearchText.ToLower().Contains(q.ToLower())) ||
                    d.OriginalName.ToLower().Contains(q.ToLower()));
            }

            var result = await query
                .OrderByDescending(d => d.Id)
                .Select(d => new
                {
                    d.Id,
                    d.OriginalName,
                    d.ObjectName,
                    d.ContentType,
                    d.SizeBytes,
                    d.TipoDocId,
                    SubserieId = d.TipoDoc!.SubserieId,
                    SerieId = d.TipoDoc!.Subserie!.SerieId,
                    Serie = d.TipoDoc!.Subserie!.Serie!.Nombre,
                    d.CurrentVersion,
                    d.CreatedAt
                })
                .ToListAsync();

            return Ok(result);
        }

        /// <summary>
        /// Obtiene un documento por Id validando acceso por serie según rol.
        /// </summary>
        [HttpGet("{id:long}")]
        public async Task<IActionResult> GetById(long id)
        {
            var allowed = User.AllowedSeries();

            var d = await _db.Documents
                .AsNoTracking()
                .Include(x => x.TipoDoc!)
                    .ThenInclude(td => td.Subserie!)
                        .ThenInclude(s => s.Serie)
                .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);

            if (d is null) return NotFound();

            if (!User.IsAdmin())
            {
                var serieId = d.TipoDoc?.Subserie?.SerieId;
                if (serieId == null || !allowed.Contains(serieId.Value))
                    return Forbid(); // 403
            }

            return Ok(new
            {
                d.Id,
                d.OriginalName,
                d.ObjectName,
                d.ContentType,
                d.SizeBytes,
                d.TipoDocId,
                SubserieId = d.TipoDoc!.SubserieId,
                SerieId = d.TipoDoc!.Subserie!.SerieId,
                Serie = d.TipoDoc!.Subserie!.Serie!.Nombre,
                d.CurrentVersion,
                d.MetadataJson,
                d.ExtractedText,
                d.CreatedAt,
                d.UpdatedAt
            });
        }

        public class UploadRequest
        {
            public long TipoDocId { get; set; }
            public long? FolderId { get; set; }
            public DateTime? DocumentDate { get; set; }
            public string? MetadataJson { get; set; } // opcional
        }

        /// <summary>
        /// Sube un documento y crea versión 1. Valida acceso por serie según rol.
        /// </summary>
        [HttpPost("upload")]
        [RequestSizeLimit(long.MaxValue)]
        public async Task<IActionResult> Upload([FromForm] UploadRequest req, IFormFile file, CancellationToken ct)
        {
            if (file == null || file.Length == 0)
                return BadRequest("Archivo requerido.");

            // Validar que el TipoDoc existe y pertenece a una serie accesible
            var tipoDoc = await _db.TiposDocumentales
                .Include(t => t.Subserie!)
                .FirstOrDefaultAsync(t => t.Id == req.TipoDocId, ct);

            if (tipoDoc is null)
                return BadRequest("Tipo documental no existe.");

            var allowed = User.AllowedSeries();
            if (!User.IsAdmin() && !allowed.Contains(tipoDoc.Subserie!.SerieId))
                return Forbid(); // 403

            // Subir a MinIO
            var bucket = _config["MinIO:Bucket"] ?? "dms";
            var objectName = BuildObjectName(file.FileName, req.TipoDocId);

            // Asegurar bucket
            bool exists = await _minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket), ct);
            if (!exists)
                await _minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket), ct);

            await using (var stream = file.OpenReadStream())
            {
                var put = new PutObjectArgs()
                    .WithBucket(bucket)
                    .WithObject(objectName)
                    .WithStreamData(stream)
                    .WithObjectSize(file.Length)
                    .WithContentType(file.ContentType ?? "application/octet-stream");

                await _minio.PutObjectAsync(put, ct);
            }

            // Crear registro en BD
            var nowUtc = DateTime.UtcNow;

            // (Opcional) Id de usuario si lo pones en el token:
            long? userId = null;
            var sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (long.TryParse(sub, out var parsed)) userId = parsed;

            var doc = new DmsContayPerezIPS.Domain.Entities.Document
            {
                OriginalName = file.FileName,
                ObjectName = objectName,
                ContentType = file.ContentType ?? "application/octet-stream",
                SizeBytes = file.Length,
                FolderId = req.FolderId,
                TipoDocId = req.TipoDocId,
                CurrentVersion = 1,
                MetadataJson = string.IsNullOrWhiteSpace(req.MetadataJson) ? null : req.MetadataJson,
                ExtractedText = null, // si luego extraes, actualiza
                IsDeleted = false,
                CreatedBy = userId,
                CreatedAt = nowUtc,
                UpdatedBy = null,
                UpdatedAt = null,
                DocumentDate = req.DocumentDate,
                // Simple: usar original + metadata como base de búsqueda
                SearchText = BuildSearchText(file.FileName, req.MetadataJson)
            };

            _db.Documents.Add(doc);
            await _db.SaveChangesAsync(ct);

            // Crear versión 1
            _db.DocumentVersions.Add(new DmsContayPerezIPS.Domain.Entities.DocumentVersion
            {
                DocumentId = doc.Id,
                VersionNumber = 1,
                ObjectName = objectName,
                UploadedBy = userId,
                UploadedAt = nowUtc
            });

            await _db.SaveChangesAsync(ct);

            return Ok(new
            {
                message = "Documento subido.",
                doc.Id,
                doc.OriginalName,
                doc.ObjectName,
                doc.TipoDocId
            });
        }

        /// <summary>
        /// Elimina lógico (IsDeleted=true) validando acceso por serie.
        /// </summary>
        [HttpDelete("{id:long}")]
        public async Task<IActionResult> Delete(long id, CancellationToken ct)
        {
            var allowed = User.AllowedSeries();

            var d = await _db.Documents
                .Include(x => x.TipoDoc!)
                    .ThenInclude(td => td.Subserie)
                .FirstOrDefaultAsync(x => x.Id == id, ct);

            if (d is null) return NotFound();

            if (!User.IsAdmin() &&
                (d.TipoDoc?.Subserie == null || !allowed.Contains(d.TipoDoc.Subserie.SerieId)))
                return Forbid(); // 403

            if (d.IsDeleted) return NoContent();

            d.IsDeleted = true;
            d.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);
            return NoContent();
        }

        // ----------------- helpers -----------------

        private static string BuildObjectName(string originalFileName, long tipoDocId)
        {
            var safeName = Path.GetFileName(originalFileName);
            var now = DateTime.UtcNow;
            var guid = Guid.NewGuid().ToString("N");
            return $"docs/{now:yyyy}/{now:MM}/{tipoDocId}/{guid}_{safeName}";
        }

        private static string? BuildSearchText(string originalName, string? metadataJson)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(metadataJson))
                    return originalName;

                using var doc = JsonDocument.Parse(metadataJson);
                var flat = string.Join(" ", doc.RootElement.EnumerateObject().Select(p => $"{p.Name} {p.Value.ToString()}"));
                return $"{originalName} {flat}";
            }
            catch
            {
                return originalName;
            }
        }
    }
}

using System.Security.Claims;
using System.Text.Json;
using DmsContayPerezIPS.API.Authorization;        // AllowedSeries() / IsAdmin()
using DmsContayPerezIPS.Domain.Entities;
using DmsContayPerezIPS.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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
        /// Lista documentos con filtros opcionales y control por serie según rol.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Get(
            [FromQuery] long? serieId,
            [FromQuery] long? subserieId,
            [FromQuery] long? tipoDocId,
            [FromQuery] string? q)
        {
            var allowed = User.AllowedSeries();

            var query = _db.Documents
                .AsNoTracking()
                .Include(d => d.TipoDocumental!)
                    .ThenInclude(td => td.Subserie!)
                        .ThenInclude(s => s.Serie)
                .Where(d => !d.IsDeleted);

            if (!User.IsAdmin())
            {
                query = query.Where(d =>
                    d.TipoDocumental != null &&
                    d.TipoDocumental.Subserie != null &&
                    allowed.Contains(d.TipoDocumental.Subserie.SerieId));
            }

            if (serieId.HasValue)
                query = query.Where(d => d.TipoDocumental!.Subserie!.SerieId == serieId.Value);

            if (subserieId.HasValue)
                query = query.Where(d => d.TipoDocumental!.SubserieId == subserieId.Value);

            if (tipoDocId.HasValue)
                query = query.Where(d => d.TipoDocId == tipoDocId.Value);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var like = $"%{q}%";
                query = query.Where(d =>
                    EF.Functions.ILike(d.SearchText, like) ||
                    (d.ExtractedText != null && EF.Functions.ILike(d.ExtractedText, like)) ||
                    EF.Functions.ILike(d.OriginalName, like));
            }

            var result = await query
                .OrderByDescending(d => d.CreatedAt)
                .Select(d => new
                {
                    d.Id,
                    d.OriginalName,
                    d.ObjectName,
                    d.ContentType,
                    d.SizeBytes,
                    d.TipoDocId,
                    SubserieId = d.TipoDocumental!.SubserieId,
                    SerieId = d.TipoDocumental!.Subserie!.SerieId,
                    Serie = d.TipoDocumental!.Subserie!.Serie!.Nombre,
                    d.CurrentVersion,
                    d.MetadataJson,
                    d.ExtractedText,
                    d.CreatedAt,
                    d.UpdatedAt
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
                .Include(x => x.TipoDocumental!)
                    .ThenInclude(td => td.Subserie!)
                        .ThenInclude(s => s.Serie)
                .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);

            if (d is null) return NotFound("Documento no existe");

            if (!User.IsAdmin())
            {
                var serieId = d.TipoDocumental?.Subserie?.SerieId;
                if (serieId == null || !allowed.Contains(serieId.Value))
                    return Forbid();
            }

            return Ok(new
            {
                d.Id,
                d.OriginalName,
                d.ObjectName,
                d.ContentType,
                d.SizeBytes,
                d.TipoDocId,
                SubserieId = d.TipoDocumental?.SubserieId,
                SerieId = d.TipoDocumental?.Subserie?.SerieId,
                Serie = d.TipoDocumental?.Subserie?.Serie?.Nombre,
                d.CurrentVersion,
                d.MetadataJson,
                d.ExtractedText,
                d.GestionUntil,
                d.CentralUntil,
                d.DocumentDate,
                d.CreatedAt,
                d.UpdatedAt
            });
        }

        /// <summary>
        /// Sube un documento a MinIO y crea el registro. Controla acceso por serie (según TipoDocumental).
        /// </summary>
        [HttpPost("upload")]
        [RequestSizeLimit(long.MaxValue)]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> Upload([FromForm] IFormFile file, [FromForm] UploadRequest req, CancellationToken ct)
        {
            if (file is null || file.Length == 0) return BadRequest("Archivo vacío");

            var tipo = await _db.TiposDocumentales
                .Include(t => t.Subserie!)
                .FirstOrDefaultAsync(t => t.Id == req.TipoDocId, ct);

            if (tipo is null) return BadRequest("Tipo documental no existe");

            // Validar acceso por serie
            if (!User.IsAdmin())
            {
                var allowed = User.AllowedSeries();
                if (tipo.Subserie == null || !allowed.Contains(tipo.Subserie.SerieId))
                    return Forbid();
            }

            var bucket = _config["MinIO:Bucket"] ?? "dms";
            var objectName = BuildObjectName(file.FileName, req.TipoDocId);

            // Subir a MinIO
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
            var userId = GetUserIdOrNull(User);

            var doc = new Document
            {
                OriginalName = file.FileName,
                ObjectName = objectName,
                ContentType = file.ContentType ?? "application/octet-stream",
                SizeBytes = file.Length,
                FolderId = req.FolderId,
                TipoDocId = req.TipoDocId,               // FK se mantiene en el modelo
                CurrentVersion = 1,
                MetadataJson = req.MetadataJson,
                ExtractedText = null,
                IsDeleted = false,
                CreatedBy = userId,
                CreatedAt = nowUtc,
                UpdatedBy = userId,
                UpdatedAt = nowUtc,
                DocumentDate = req.DocumentDate,
                SearchText = $"{file.FileName} {(req.MetadataJson ?? string.Empty)}"
            };

            _db.Documents.Add(doc);
            await _db.SaveChangesAsync(ct);

            // Guardar versión
            _db.DocumentVersions.Add(new DocumentVersion
            {
                DocumentId = doc.Id,
                VersionNumber = 1,
                ObjectName = objectName,
                UploadedBy = userId,
                UploadedAt = nowUtc
            });

            await _db.SaveChangesAsync(ct);

            return Ok(new { message = "Documento subido", id = doc.Id, objectName });
        }

        // ==========================================================
        // Helpers / DTOs
        // ==========================================================
        public class UploadRequest
        {
            public long TipoDocId { get; set; }          // FK a TipoDocumental (mantener nombre de columna)
            public long? FolderId { get; set; }
            public DateTime? DocumentDate { get; set; }
            public string? MetadataJson { get; set; }
        }

        private static long? GetUserIdOrNull(ClaimsPrincipal user)
        {
            var id = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (long.TryParse(id, out var parsed)) return parsed;
            return null;
        }

        private static string BuildObjectName(string originalName, long tipoDocId)
        {
            // Limpio y con prefijo por tipoDoc
            string clean = SanitizeFileName(originalName);
            return $"td/{tipoDocId}/{Guid.NewGuid():N}_{clean}";
        }

        private static string SanitizeFileName(string name)
        {
            try
            {
                var invalid = Path.GetInvalidFileNameChars();
                foreach (var ch in invalid)
                    name = name.Replace(ch, '_');
                // Evitar nombres excesivos
                if (name.Length > 150) name = name[^150..];
                return name;
            }
            catch
            {
                return "file.bin";
            }
        }
    }
}

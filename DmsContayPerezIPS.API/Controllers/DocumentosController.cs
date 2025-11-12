using System.IO;
using System.Security.Claims;
using System.Text.Json;
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

        // ==========================================================
        // GET: api/documentos
        // Lista documentos con filtros + control por serie según rol
        // ==========================================================
        [HttpGet]
        public async Task<IActionResult> Get(
            [FromQuery] long? serieId,
            [FromQuery] long? subserieId,
            [FromQuery] long? tipoDocId,
            [FromQuery] string? q)
        {
            var allowed = AllowedSeries(User);

            var query = _db.Documents
                .AsNoTracking()
                .Include(d => d.TipoDocumental!)
                    .ThenInclude(td => td.Subserie!)
                        .ThenInclude(s => s.Serie)
                .Where(d => !d.IsDeleted);

            if (!IsAdmin(User))
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

        // ==========================================================
        // GET: api/documentos/{id}
        // Detalle por Id con control por serie
        // ==========================================================
        [HttpGet("{id:long}")]
        public async Task<IActionResult> GetById(long id)
        {
            var allowed = AllowedSeries(User);

            var d = await _db.Documents
                .AsNoTracking()
                .Include(x => x.TipoDocumental!)
                    .ThenInclude(td => td.Subserie!)
                        .ThenInclude(s => s.Serie)
                .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);

            if (d is null) return NotFound("Documento no existe");

            if (!IsAdmin(User))
            {
                var sid = d.TipoDocumental?.Subserie?.SerieId;
                if (sid is null || !allowed.Contains(sid.Value))
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

        // ==========================================================
        // POST: api/documentos/upload
        // Upload (Swagger-friendly): un solo parámetro [FromForm]
        // ==========================================================
        public class UploadForm
        {
            [FromForm] public IFormFile File { get; set; } = null!;
            [FromForm] public long TipoDocId { get; set; }
            [FromForm] public long? FolderId { get; set; }
            [FromForm] public DateTime? DocumentDate { get; set; }
            [FromForm] public string? MetadataJson { get; set; }
        }

        [HttpPost("upload")]
        [RequestSizeLimit(long.MaxValue)]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> Upload([FromForm] UploadForm form, CancellationToken ct)
        {
            if (form.File is null || form.File.Length == 0)
                return BadRequest("Archivo vacío");

            var tipo = await _db.TiposDocumentales
                .Include(t => t.Subserie!)
                .FirstOrDefaultAsync(t => t.Id == form.TipoDocId, ct);

            if (tipo is null) return BadRequest("Tipo documental no existe");

            if (!IsAdmin(User))
            {
                var allowed = AllowedSeries(User);
                if (tipo.Subserie == null || !allowed.Contains(tipo.Subserie.SerieId))
                    return Forbid();
            }

            var bucket = _config["MinIO:Bucket"] ?? "dms";
            var objectName = BuildObjectName(form.File.FileName, form.TipoDocId);

            await using (var stream = form.File.OpenReadStream())
            {
                var put = new PutObjectArgs()
                    .WithBucket(bucket)
                    .WithObject(objectName)
                    .WithStreamData(stream)
                    .WithObjectSize(form.File.Length)
                    .WithContentType(form.File.ContentType ?? "application/octet-stream");

                await _minio.PutObjectAsync(put, ct);
            }

            var nowUtc = DateTime.UtcNow;
            var userId = GetUserIdOrNull(User);

            var doc = new Document
            {
                OriginalName = form.File.FileName,
                ObjectName = objectName,
                ContentType = form.File.ContentType ?? "application/octet-stream",
                SizeBytes = form.File.Length,
                FolderId = form.FolderId,
                TipoDocId = form.TipoDocId,
                CurrentVersion = 1,
                MetadataJson = form.MetadataJson,
                ExtractedText = null,
                IsDeleted = false,
                CreatedBy = userId,
                CreatedAt = nowUtc,
                UpdatedBy = userId,
                UpdatedAt = nowUtc,
                DocumentDate = form.DocumentDate,
                SearchText = $"{form.File.FileName} {(form.MetadataJson ?? string.Empty)}"
            };

            _db.Documents.Add(doc);
            await _db.SaveChangesAsync(ct);

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
        // GET: api/documentos/{id}/download
        // Descarga por la API (para archivos pequeños/medianos)
        // ==========================================================
        [HttpGet("{id:long}/download")]
        public async Task<IActionResult> Download(long id, CancellationToken ct)
        {
            var allowed = AllowedSeries(User);

            var d = await _db.Documents
                .AsNoTracking()
                .Include(x => x.TipoDocumental!)
                    .ThenInclude(td => td.Subserie!)
                        .ThenInclude(s => s.Serie)
                .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);

            if (d is null) return NotFound("Documento no existe");

            if (!IsAdmin(User))
            {
                var sid = d.TipoDocumental?.Subserie?.SerieId;
                if (sid is null || !allowed.Contains(sid.Value))
                    return Forbid();
            }

            var bucket = _config["MinIO:Bucket"] ?? "dms";
            if (string.IsNullOrWhiteSpace(d.ObjectName))
                return BadRequest("Documento sin objeto asociado");

            var ms = new MemoryStream();
            await _minio.GetObjectAsync(
                new GetObjectArgs()
                    .WithBucket(bucket)
                    .WithObject(d.ObjectName)
                    .WithCallbackStream(s => s.CopyTo(ms)),
                ct
            );
            ms.Position = 0;

            var contentType = !string.IsNullOrWhiteSpace(d.ContentType) ? d.ContentType : "application/octet-stream";
            var downloadName = !string.IsNullOrWhiteSpace(d.OriginalName) ? d.OriginalName : Path.GetFileName(d.ObjectName);

            return File(ms, contentType, downloadName);
        }

        // ==========================================================
        // GET: api/documentos/{id}/url?expiresSeconds=600
        // URL prefirmada de MinIO para descarga directa (recomendado)
        // ==========================================================
        [HttpGet("{id:long}/url")]
        public async Task<IActionResult> GetPresignedUrl(long id, [FromQuery] int expiresSeconds = 600, CancellationToken ct = default)
        {
            var allowed = AllowedSeries(User);

            var d = await _db.Documents
                .AsNoTracking()
                .Include(x => x.TipoDocumental!)
                    .ThenInclude(td => td.Subserie!)
                        .ThenInclude(s => s.Serie)
                .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);

            if (d is null) return NotFound("Documento no existe");

            if (!IsAdmin(User))
            {
                var sid = d.TipoDocumental?.Subserie?.SerieId;
                if (sid is null || !allowed.Contains(sid.Value))
                    return Forbid();
            }

            var bucket = _config["MinIO:Bucket"] ?? "dms";
            if (string.IsNullOrWhiteSpace(d.ObjectName))
                return BadRequest("Documento sin objeto asociado");

            var expiry = Math.Clamp(expiresSeconds, 60, 24 * 3600);

            var url = await _minio.PresignedGetObjectAsync(
                new PresignedGetObjectArgs()
                    .WithBucket(bucket)
                    .WithObject(d.ObjectName)
                    .WithExpiry(expiry)
            );

            return Ok(new { url, expiresIn = expiry });
        }

        // ==========================================================
        // Helpers locales (evitan depender de clases externas)
        // ==========================================================
        private static bool IsAdmin(ClaimsPrincipal user) => user.IsInRole("Admin");

        private static IReadOnlyCollection<long> AllowedSeries(ClaimsPrincipal user)
        {
            if (IsAdmin(user)) return new long[] { 1, 2, 3, 4, 5, 6, 7 };
            var role = user.FindFirstValue(ClaimTypes.Role);
            if (string.IsNullOrWhiteSpace(role)) return Array.Empty<long>();

            // Map de rol -> SerieId (según tu seed)
            return role switch
            {
                "GestClinica" => new long[] { 1 },
                "GestiAdmin" => new long[] { 2 },
                "GestFinYCon" => new long[] { 3 },
                "GestJurid" => new long[] { 4 },
                "GestCalidad" => new long[] { 5 },
                "SGSST" => new long[] { 6 },
                "AdminEquBiomed" => new long[] { 7 },
                _ => Array.Empty<long>()
            };
        }

        private static long? GetUserIdOrNull(ClaimsPrincipal user)
        {
            var id = user.FindFirstValue(ClaimTypes.NameIdentifier);
            return long.TryParse(id, out var parsed) ? parsed : null;
        }

        private static string BuildObjectName(string originalName, long tipoDocId)
        {
            string clean = SanitizeFileName(originalName);
            return $"td/{tipoDocId}/{Guid.NewGuid():N}_{clean}";
        }

        private static string SanitizeFileName(string name)
        {
            try
            {
                foreach (var ch in Path.GetInvalidFileNameChars())
                    name = name.Replace(ch, '_');
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

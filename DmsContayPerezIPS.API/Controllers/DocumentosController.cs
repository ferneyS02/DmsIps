using System.Globalization;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    [Authorize] // requiere JWT
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
        // GET: /api/Documentos
        // Lista/búsqueda con filtros + control por serie
        // Soporta búsqueda por fecha en:
        //   - parámetros fromDoc/toDoc
        //   - detección dentro de 'q' (2025-11-12, 11/2025, "noviembre 2025", etc.)
        // Incluye quién subió el documento (UploadedById/UploadedByName)
        // ==========================================================
        [HttpGet]
        public async Task<IActionResult> Get(
            [FromQuery] long? serieId,
            [FromQuery] long? subserieId,
            [FromQuery] long? tipoDocId,
            [FromQuery] string? q,
            [FromQuery] DateTime? fromDoc,
            [FromQuery] DateTime? toDoc)
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

            // Rango explícito por fecha del documento
            if (fromDoc.HasValue)
                query = query.Where(d => d.DocumentDate != null && d.DocumentDate >= fromDoc.Value.Date);

            if (toDoc.HasValue)
                query = query.Where(d => d.DocumentDate != null && d.DocumentDate < toDoc.Value.Date.AddDays(1));

            // Texto + detección de fecha en 'q'
            if (!string.IsNullOrWhiteSpace(q))
            {
                var like = $"%{q}%";
                query = query.Where(d =>
                    EF.Functions.ILike(d.SearchText, like) ||
                    (d.ExtractedText != null && EF.Functions.ILike(d.ExtractedText, like)) ||
                    EF.Functions.ILike(d.OriginalName, like));

                if (TryParseDateFromQuery(q, out var start, out var end))
                {
                    query = query.Where(d => d.DocumentDate != null && d.DocumentDate >= start && d.DocumentDate < end);
                }
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
                    d.UpdatedAt,

                    // 👇 Quién subió el documento (usa CreatedBy)
                    UploadedById = d.CreatedBy,
                    UploadedByName = _db.Users
                        .Where(u => u.Id == d.CreatedBy)
                        .Select(u => u.Username)
                        .FirstOrDefault()
                })
                .ToListAsync();

            return Ok(result);
        }

        // ==========================================================
        // GET: /api/Documentos/{id}
        // Detalle con control por serie (incluye quién subió)
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
                d.UpdatedAt,

                // 👇 Quién subió
                UploadedById = d.CreatedBy,
                UploadedByName = _db.Users
                    .Where(u => u.Id == d.CreatedBy)
                    .Select(u => u.Username)
                    .FirstOrDefault()
            });
        }

        // ==========================================================
        // POST: /api/Documentos/upload
        // Upload a MinIO (Swagger-friendly): un solo DTO [FromForm]
        // Guarda CreatedBy a partir del claim NameIdentifier del token
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
            var userId = GetUserIdOrNull(User); // requiere que el JWT tenga ClaimTypes.NameIdentifier

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
        // GET: /api/Documentos/{id}/download
        // Descarga por API (para archivos pequeños/medianos)
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
        // GET: /api/Documentos/{id}/url?expiresSeconds=600
        // URL prefirmada (recomendada para archivos grandes)
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
        // Helpers locales (autorización y utilidades)
        // ==========================================================
        private static bool IsAdmin(ClaimsPrincipal user) => user.IsInRole("Admin");

        private static IReadOnlyCollection<long> AllowedSeries(ClaimsPrincipal user)
        {
            if (IsAdmin(user)) return new long[] { 1, 2, 3, 4, 5, 6, 7 };
            var role = user.FindFirstValue(ClaimTypes.Role);
            if (string.IsNullOrWhiteSpace(role)) return Array.Empty<long>();

            // Map de rol -> SerieId (según tu TRD/seed)
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
            // 👇 Asegúrate de incluir este claim en tu AuthController.Login:
            // new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())
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

        // Detecta fechas en 'q' (día exacto o mes/año)
        private static bool TryParseDateFromQuery(string q, out DateTime start, out DateTime end)
        {
            q = (q ?? "").Trim().ToLowerInvariant();
            var es = new CultureInfo("es-CO");

            // Día exacto (varios formatos)
            string[] exacts = { "yyyy-MM-dd", "dd/MM/yyyy", "dd-MM-yyyy", "MM/dd/yyyy" };
            if (DateTime.TryParseExact(q, exacts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dExact) ||
                DateTime.TryParse(q, es, DateTimeStyles.None, out dExact))
            {
                start = dExact.Date;
                end = start.AddDays(1);
                return true;
            }

            // Año-mes: 2025-11 o 11/2025
            var ym = Regex.Match(q, @"^(?<y>\d{4})[-/](?<m>\d{1,2})$|^(?<m2>\d{1,2})[-/](?<y2>\d{4})$");
            if (ym.Success)
            {
                int y = ym.Groups["y"].Success ? int.Parse(ym.Groups["y"].Value) : int.Parse(ym.Groups["y2"].Value);
                int m = ym.Groups["m"].Success ? int.Parse(ym.Groups["m"].Value) : int.Parse(ym.Groups["m2"].Value);
                start = new DateTime(y, m, 1);
                end = start.AddMonths(1);
                return true;
            }

            // Mes en español + año: "noviembre 2025"
            var mregex = Regex.Match(q, @"^(enero|febrero|marzo|abril|mayo|junio|julio|agosto|septiembre|setiembre|octubre|noviembre|diciembre)\s+(\d{4})$");
            if (mregex.Success)
            {
                var name = mregex.Groups[1].Value;
                var year = int.Parse(mregex.Groups[2].Value);

                string[] meses = { "enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre" };
                int month = Array.IndexOf(meses, name) + 1;
                if (month <= 0 && name == "setiembre") month = 9;

                start = new DateTime(year, month, 1);
                end = start.AddMonths(1);
                return true;
            }

            start = end = default;
            return false;
        }
    }
}

using System.Security.Claims;
using DmsContayPerezIPS.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DmsContayPerezIPS.API.Controllers
{
    [Authorize(Policy = "PwdFresh")]
    [ApiController]
    [Route("api/[controller]")]
    public class TRDController : ControllerBase
    {
        private readonly AppDbContext _db;
        public TRDController(AppDbContext db) => _db = db;

        // ==================== SERIES (según rol) ====================
        // GET: /api/trd/mis-series
        // Devuelve series visibles para el rol actual (Admin ve todas).
        [HttpGet("mis-series")]
        public async Task<IActionResult> GetMySeries(CancellationToken ct)
        {
            var allowed = AllowedSeries(User);

            var q = _db.Series.AsNoTracking();
            if (!IsAdmin(User))
                q = q.Where(s => allowed.Contains(s.Id));

            var list = await q
                .OrderBy(s => s.Nombre)
                .Select(s => new {
                    s.Id,
                    nombre = s.Nombre,
                    displayName = s.Nombre,
                    path = s.Nombre
                })
                .ToListAsync(ct);

            return Ok(list);
        }

        // ==================== SUBSERIES (según rol) ====================
        // GET: /api/trd/mis-subseries?serieId=2
        [HttpGet("mis-subseries")]
        public async Task<IActionResult> GetMySubseries([FromQuery] long? serieId, CancellationToken ct)
        {
            var allowed = AllowedSeries(User);

            var seriesQ = _db.Series.AsNoTracking();
            if (!IsAdmin(User))
                seriesQ = seriesQ.Where(s => allowed.Contains(s.Id));

            // cache series visibles
            var series = await seriesQ
                .Select(s => new { s.Id, s.Nombre })
                .ToListAsync(ct);

            var serieIds = series.Select(s => s.Id).ToList();

            var subsQ = _db.Subseries.AsNoTracking().Where(ss => serieIds.Contains(ss.SerieId));
            if (serieId.HasValue) subsQ = subsQ.Where(ss => ss.SerieId == serieId.Value);

            var subseries = await subsQ
                .Select(ss => new { ss.Id, ss.Nombre, ss.SerieId })
                .OrderBy(ss => ss.Nombre)
                .ToListAsync(ct);

            var result = subseries.Select(ss =>
            {
                var s = series.First(x => x.Id == ss.SerieId);
                var path = $"{s.Nombre} / {ss.Nombre}";
                return new
                {
                    ss.Id,
                    nombre = ss.Nombre,
                    ss.SerieId,
                    displayName = ss.Nombre,
                    path
                };
            });

            return Ok(result);
        }

        // ==================== TIPOS (según rol) ====================
        // GET: /api/trd/mis-tipos?subserieId=10&serieId=2
        [HttpGet("mis-tipos")]
        public async Task<IActionResult> GetMyTipos([FromQuery] long? subserieId, [FromQuery] long? serieId, CancellationToken ct)
        {
            var allowed = AllowedSeries(User);

            // series visibles por rol
            var seriesQ = _db.Series.AsNoTracking();
            if (!IsAdmin(User))
                seriesQ = seriesQ.Where(s => allowed.Contains(s.Id));
            var series = await seriesQ.Select(s => new { s.Id, s.Nombre }).ToListAsync(ct);
            var serieIds = series.Select(s => s.Id).ToList();

            // subseries visibles dentro de esas series
            var subseriesQ = _db.Subseries.AsNoTracking().Where(ss => serieIds.Contains(ss.SerieId));
            if (serieId.HasValue) subseriesQ = subseriesQ.Where(ss => ss.SerieId == serieId.Value);
            if (subserieId.HasValue) subseriesQ = subseriesQ.Where(ss => ss.Id == subserieId.Value);

            var subseries = await subseriesQ
                .Select(ss => new { ss.Id, ss.Nombre, ss.SerieId })
                .ToListAsync(ct);
            var subIds = subseries.Select(ss => ss.Id).ToList();

            if (subIds.Count == 0) return Ok(Array.Empty<object>());

            // tipos activos en esas subseries
            var tipos = await _db.TiposDocumentales.AsNoTracking()
                .Where(t => subIds.Contains(t.SubserieId) && t.IsActive)
                .Select(t => new { t.Id, t.Nombre, t.SubserieId })
                .OrderBy(t => t.Nombre)
                .ToListAsync(ct);

            var list = tipos.Select(t =>
            {
                var ss = subseries.First(x => x.Id == t.SubserieId);
                var s = series.First(x => x.Id == ss.SerieId);
                var path = $"{s.Nombre} / {ss.Nombre} / {t.Nombre}";
                return new
                {
                    t.Id,
                    nombre = t.Nombre,
                    t.SubserieId,
                    serieId = s.Id,
                    displayName = t.Nombre,
                    path
                };
            });

            return Ok(list);
        }

        // ==================== ÁRBOL COMPLETO (Series→Subseries→Tipos) ====================
        // GET: /api/trd/mis-opciones
        [HttpGet("mis-opciones")]
        public async Task<IActionResult> GetMyOptionsTree(CancellationToken ct)
        {
            var allowed = AllowedSeries(User);

            var seriesQ = _db.Series.AsNoTracking();
            if (!IsAdmin(User))
                seriesQ = seriesQ.Where(s => allowed.Contains(s.Id));

            var series = await seriesQ
                .Select(s => new { s.Id, s.Nombre })
                .OrderBy(s => s.Nombre)
                .ToListAsync(ct);

            var serieIds = series.Select(s => s.Id).ToList();

            var subseries = await _db.Subseries.AsNoTracking()
                .Where(ss => serieIds.Contains(ss.SerieId))
                .Select(ss => new { ss.Id, ss.Nombre, ss.SerieId })
                .OrderBy(ss => ss.Nombre)
                .ToListAsync(ct);

            var subIds = subseries.Select(ss => ss.Id).ToList();

            var tipos = await _db.TiposDocumentales.AsNoTracking()
                .Where(t => subIds.Contains(t.SubserieId) && t.IsActive)
                .Select(t => new { t.Id, t.Nombre, t.SubserieId })
                .OrderBy(t => t.Nombre)
                .ToListAsync(ct);

            var tree = series.Select(s => new
            {
                id = s.Id,
                nombre = s.Nombre,
                displayName = s.Nombre,
                path = s.Nombre,
                subseries = subseries
                    .Where(ss => ss.SerieId == s.Id)
                    .Select(ss => new
                    {
                        id = ss.Id,
                        nombre = ss.Nombre,
                        displayName = ss.Nombre,
                        path = $"{s.Nombre} / {ss.Nombre}",
                        tipos = tipos
                            .Where(t => t.SubserieId == ss.Id)
                            .Select(t => new
                            {
                                id = t.Id,
                                nombre = t.Nombre,
                                displayName = t.Nombre,
                                path = $"{s.Nombre} / {ss.Nombre} / {t.Nombre}"
                            })
                            .ToList()
                    })
                    .ToList()
            });

            return Ok(tree);
        }

        // ==================== LISTA PLANA DE TIPOS (path listo p/ select) ====================
        // GET: /api/trd/mis-tipos-flat
        [HttpGet("mis-tipos-flat")]
        public async Task<IActionResult> GetMyTiposFlat(CancellationToken ct)
        {
            var allowed = AllowedSeries(User);

            var seriesQ = _db.Series.AsNoTracking();
            if (!IsAdmin(User))
                seriesQ = seriesQ.Where(s => allowed.Contains(s.Id));
            var series = await seriesQ.Select(s => new { s.Id, s.Nombre }).ToListAsync(ct);
            var serieIds = series.Select(s => s.Id).ToList();

            var subseries = await _db.Subseries.AsNoTracking()
                .Where(ss => serieIds.Contains(ss.SerieId))
                .Select(ss => new { ss.Id, ss.Nombre, ss.SerieId })
                .ToListAsync(ct);
            var subIds = subseries.Select(ss => ss.Id).ToList();

            var tipos = await _db.TiposDocumentales.AsNoTracking()
                .Where(t => subIds.Contains(t.SubserieId) && t.IsActive)
                .Select(t => new { t.Id, t.Nombre, t.SubserieId })
                .ToListAsync(ct);

            var flat = tipos.Select(t =>
            {
                var ss = subseries.First(x => x.Id == t.SubserieId);
                var s = series.First(x => x.Id == ss.SerieId);
                var path = $"{s.Nombre} / {ss.Nombre} / {t.Nombre}";
                return new
                {
                    id = t.Id,
                    nombre = t.Nombre,
                    displayName = t.Nombre,
                    path,
                    subserieId = ss.Id,
                    subserie = ss.Nombre,
                    serieId = s.Id,
                    serie = s.Nombre
                };
            })
            .OrderBy(x => x.path)
            .ToList();

            return Ok(flat);
        }

        // ==================== Helpers de autorización ====================
        private static bool IsAdmin(ClaimsPrincipal user) => user.IsInRole("Admin");

        private static IReadOnlyCollection<long> AllowedSeries(ClaimsPrincipal user)
        {
            if (IsAdmin(user)) return new long[] { 1, 2, 3, 4, 5, 6, 7 };
            var role = user.FindFirstValue(ClaimTypes.Role);
            if (string.IsNullOrWhiteSpace(role)) return Array.Empty<long>();

            // Map de rol -> SerieId (según tu TRD)
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
    }
}

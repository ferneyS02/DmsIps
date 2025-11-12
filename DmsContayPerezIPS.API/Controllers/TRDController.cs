using DmsContayPerezIPS.Infrastructure.Persistence;
using DmsContayPerezIPS.API.Authorization; // ← AllowedSeries() / IsAdmin()
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DmsContayPerezIPS.API.Controllers
{
    [Authorize] // requiere token
    [ApiController]
    [Route("api/trd")]
    public class TRDController : ControllerBase
    {
        private readonly AppDbContext _db;

        public TRDController(AppDbContext db)
        {
            _db = db;
        }

        /// <summary>
        /// Lista Series visibles según el rol (Admin ve todo).
        /// </summary>
        [HttpGet("series")]
        public async Task<IActionResult> GetSeries()
        {
            var allowed = User.AllowedSeries();
            var q = _db.Series.AsNoTracking().AsQueryable();

            if (!User.IsAdmin())
                q = q.Where(s => allowed.Contains(s.Id));

            var series = await q
                .OrderBy(s => s.Id)
                .Select(s => new
                {
                    s.Id,
                    s.Nombre
                })
                .ToListAsync();

            return Ok(series);
        }

        /// <summary>
        /// Obtiene una Serie por Id (valida acceso por rol).
        /// </summary>
        [HttpGet("series/{id:long}")]
        public async Task<IActionResult> GetSerieById(long id)
        {
            var allowed = User.AllowedSeries();

            var serie = await _db.Series
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == id);

            if (serie is null)
                return NotFound();

            if (!User.IsAdmin() && !allowed.Contains(serie.Id))
                return Forbid(); // 403

            return Ok(new { serie.Id, serie.Nombre });
        }

        /// <summary>
        /// Lista Subseries visibles según el rol (Admin ve todo).
        /// Puedes filtrar por SerieId.
        /// </summary>
        [HttpGet("subseries")]
        public async Task<IActionResult> GetSubseries([FromQuery] long? serieId = null)
        {
            var allowed = User.AllowedSeries();

            var q = _db.Subseries
                .AsNoTracking()
                .Include(ss => ss.Serie)
                .AsQueryable();

            // Filtro por rol (serie)
            if (!User.IsAdmin())
                q = q.Where(ss => allowed.Contains(ss.SerieId));

            // Filtro opcional por SerieId
            if (serieId.HasValue)
                q = q.Where(ss => ss.SerieId == serieId.Value);

            var subseries = await q
                .OrderBy(ss => ss.SerieId)
                .ThenBy(ss => ss.Id)
                .Select(ss => new
                {
                    ss.Id,
                    ss.Nombre,
                    ss.SerieId,
                    Serie = ss.Serie!.Nombre,
                    ss.RetencionGestion,
                    ss.RetencionCentral,
                    ss.DisposicionFinal
                })
                .ToListAsync();

            return Ok(subseries);
        }

        /// <summary>
        /// Obtiene una Subserie por Id (valida acceso por rol).
        /// </summary>
        [HttpGet("subseries/{id:long}")]
        public async Task<IActionResult> GetSubserieById(long id)
        {
            var allowed = User.AllowedSeries();

            var ss = await _db.Subseries
                .AsNoTracking()
                .Include(s => s.Serie)
                .FirstOrDefaultAsync(s => s.Id == id);

            if (ss is null)
                return NotFound();

            if (!User.IsAdmin() && !allowed.Contains(ss.SerieId))
                return Forbid(); // 403

            return Ok(new
            {
                ss.Id,
                ss.Nombre,
                ss.SerieId,
                Serie = ss.Serie!.Nombre,
                ss.RetencionGestion,
                ss.RetencionCentral,
                ss.DisposicionFinal
            });
        }
    }
}

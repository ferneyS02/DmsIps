using DmsContayPerezIPS.Infrastructure.Persistence;
using DmsContayPerezIPS.API.Authorization;            // ← helper para AllowedSeries / IsAdmin
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DmsContayPerezIPS.API.Controllers
{
    [Authorize] // exige token
    [ApiController]
    [Route("api/[controller]")]
    public class TiposDocumentalesController : ControllerBase
    {
        private readonly AppDbContext _db;

        public TiposDocumentalesController(AppDbContext db)
        {
            _db = db;
        }

        /// <summary>
        /// Lista Tipos Documentales visibles según el rol (Admin ve todo).
        /// Filtro opcional por subserieId.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Get([FromQuery] long? subserieId = null)
        {
            var allowed = User.AllowedSeries();

            var q = _db.TiposDocumentales
                .AsNoTracking()
                .Include(t => t.Subserie!)
                    .ThenInclude(s => s.Serie)
                .AsQueryable();

            // Filtro por serie según rol (si no es Admin)
            if (!User.IsAdmin())
                q = q.Where(t => t.Subserie != null && allowed.Contains(t.Subserie.SerieId));

            // Filtro adicional por subserie (opcional)
            if (subserieId.HasValue)
                q = q.Where(t => t.SubserieId == subserieId.Value);

            var res = await q
                .OrderBy(t => t.SubserieId)
                .ThenBy(t => t.Nombre)
                .Select(t => new
                {
                    t.Id,
                    t.Nombre,
                    t.SubserieId,
                    Subserie = t.Subserie!.Nombre,
                    SerieId = t.Subserie!.SerieId,
                    Serie = t.Subserie!.Serie != null ? t.Subserie!.Serie!.Nombre : null,
                    t.DisposicionFinal,
                    t.RetencionGestion,
                    t.RetencionCentral,
                    t.IsActive
                })
                .ToListAsync();

            return Ok(res);
        }

        /// <summary>
        /// Obtiene un Tipo Documental por Id (valida visibilidad según rol).
        /// </summary>
        [HttpGet("{id:long}")]
        public async Task<IActionResult> GetById(long id)
        {
            var allowed = User.AllowedSeries();

            var t = await _db.TiposDocumentales
                .AsNoTracking()
                .Include(x => x.Subserie!)
                    .ThenInclude(s => s.Serie)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (t is null)
                return NotFound();

            // Si no es Admin, verificar que pertenezca a su serie
            if (!User.IsAdmin())
            {
                var serieId = t.Subserie?.SerieId;
                if (serieId == null || !allowed.Contains(serieId.Value))
                    return Forbid(); // 403
            }

            return Ok(new
            {
                t.Id,
                t.Nombre,
                t.SubserieId,
                Subserie = t.Subserie!.Nombre,
                SerieId = t.Subserie!.SerieId,
                Serie = t.Subserie!.Serie != null ? t.Subserie!.Serie!.Nombre : null,
                t.DisposicionFinal,
                t.RetencionGestion,
                t.RetencionCentral,
                t.IsActive
            });
        }
    }
}

using System.Security.Claims;

namespace DmsContayPerezIPS.API.Authorization
{
    public static class SeriesAccess
    {
        // IDs según tu seed de Series:
        // 1: Gestión Clínica
        // 2: Gestión Administrativa
        // 3: Gestión Financiera y Contable
        // 4: Gestión Jurídica
        // 5: Gestión de Calidad
        // 6: SG-SST
        // 7: Administración de equipos biomédicos
        private static readonly Dictionary<string, long> RoleToSerieId = new(StringComparer.OrdinalIgnoreCase)
        {
            ["GestClinica"] = 1,
            ["GestiAdmin"] = 2,
            ["GestFinYCon"] = 3,
            ["GestJurid"] = 4,
            ["GestCalidad"] = 5,
            ["SGSST"] = 6,
            ["AdminEquBiomed"] = 7
        };

        public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole("Admin");

        public static IReadOnlyCollection<long> AllowedSeries(this ClaimsPrincipal user)
        {
            if (user.IsAdmin()) return new long[] { 1, 2, 3, 4, 5, 6, 7 };

            var role = user.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Role)?.Value;
            if (role != null && RoleToSerieId.TryGetValue(role, out var serieId))
                return new long[] { serieId };

            return Array.Empty<long>();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DmsContayPerezIPS.API.Services
{
    public static class SpanishDateParser
    {
        private static readonly Dictionary<string, int> Meses = new(StringComparer.OrdinalIgnoreCase)
        {
            ["enero"] = 1,
            ["febrero"] = 2,
            ["marzo"] = 3,
            ["abril"] = 4,
            ["mayo"] = 5,
            ["junio"] = 6,
            ["julio"] = 7,
            ["agosto"] = 8,
            ["septiembre"] = 9,
            ["setiembre"] = 9,
            ["octubre"] = 10,
            ["noviembre"] = 11,
            ["diciembre"] = 12
        };

        // Acepta: "25 de febrero del 2025", "25 febrero 2025", "25/02/2025", "25-02-2025", "2025-02-25"
        public static bool TryParse(string input, out DateTime result)
        {
            result = default;

            if (string.IsNullOrWhiteSpace(input)) return false;
            input = input.Trim();

            // 1) Formatos numéricos comunes
            string[] formatos = {
                "dd/MM/yyyy", "d/M/yyyy",
                "dd-MM-yyyy", "d-M-yyyy",
                "yyyy-MM-dd", "yyyy/M/d", "yyyy/M/dd", "yyyy-MM-d"
            };
            if (DateTime.TryParseExact(input, formatos, CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out result))
                return true;

            // 2) "25 de febrero del 2025" / "25 de febrero de 2025" / "25 febrero 2025"
            var rx = new Regex(@"(?<d>\d{1,2})\s*(de)?\s*(?<m>[A-Za-zÁÉÍÓÚáéíóúñÑ]+)\s*(de|del)?\s*(?<y>\d{4})",
                               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var m = rx.Match(input);
            if (m.Success)
            {
                var d = int.Parse(m.Groups["d"].Value);
                var y = int.Parse(m.Groups["y"].Value);
                var nombreMes = m.Groups["m"].Value.Normalize(NormalizationForm.FormD);
                nombreMes = Regex.Replace(nombreMes, @"\p{Mn}", ""); // quitar acentos

                if (Meses.TryGetValue(nombreMes.ToLower(), out var mes))
                {
                    try
                    {
                        result = new DateTime(y, mes, d);
                        return true;
                    }
                    catch { /* fuera de rango */ }
                }
            }

            // 3) Último intento con DateTime.Parse (regional)
            return DateTime.TryParse(input, new CultureInfo("es-CO"), DateTimeStyles.None, out result);
        }
    }
}

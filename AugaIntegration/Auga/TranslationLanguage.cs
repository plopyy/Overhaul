using System;
using System.Globalization;

namespace Auga
{
    internal static class TranslationLanguage
    {
        public static string GetCode(string language)
        {
            if (string.IsNullOrWhiteSpace(language)) return "EN";
            string normalized = language.Trim().Replace('_', '-');
            // Valheim names that differ from the culture names supplied by .NET.
            switch (normalized.ToLowerInvariant())
            {
                case "portuguese-brazilian": case "brazilian portuguese": case "brazilian": return "PT";
                case "chinese": case "chinese-traditional": case "chinese-simplified": return "ZH";
            }
            foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures | CultureTypes.SpecificCultures))
            {
                if (culture.Equals(CultureInfo.InvariantCulture)) continue;
                if (string.Equals(culture.Name, normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(culture.EnglishName, normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(culture.NativeName, normalized, StringComparison.OrdinalIgnoreCase))
                    return culture.TwoLetterISOLanguageName.ToUpperInvariant();
            }
            return "EN";
        }
    }
}

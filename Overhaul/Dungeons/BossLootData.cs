using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Overhaul.Dungeons
{
    internal static class BossLootData
    {
        internal sealed class Entry
        {
            internal string Prefab;
            internal int Minimum, Maximum;
            internal double Chance;
        }
        internal const string FileName = "DungeonBossLoot.cfg";
        private static Dictionary<string, Entry[]> tables;
        internal static void Initialize()
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(BossLootData).Assembly.Location), FileName);
            if (!File.Exists(path)) File.WriteAllText(path, Utility.ConfigSections.Defaults(FileName));
            string text=File.ReadAllText(path);tables=Read(text);
            if(!text.Split('\n').Any(l=>l.Split('#',';')[0].Trim()=="[Mistlands]")){if(!File.Exists(path+".mistlands.bak"))File.Copy(path,path+".mistlands.bak");File.AppendAllText(path,MistlandsDefaults);}
        }
        private const string MistlandsDefaults="\n# Mistlands : mines infestees\n[Mistlands]\nBlackCore = 1, 1, 100\nRoyalJelly = 6, 10, 100\nCoins = 150, 200, 100\n";
        internal static Dictionary<string, Entry[]> Read(string text)
        {
            var result = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
            string section = null; int number = 0;
            foreach (string raw in text.Split('\n'))
            {
                number++;
                string line = raw.Split('#', ';')[0].Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2);
                    if ((section != "Forest" && section != "Swamp" && section != "Mountain" && section != "Mistlands") || result.ContainsKey(section))
                        throw Invalid(number, "section inconnue ou dupliquee");
                    result.Add(section, new List<Entry>()); continue;
                }
                int equal = line.IndexOf('=');
                if (section == null || equal <= 0) throw Invalid(number, "format attendu : Prefab = minimum, maximum, chance");
                string prefab = line.Substring(0, equal).Trim();
                var values = line.Substring(equal + 1).Split(',').Select(v => v.Trim()).ToArray();
                double chance = 100;
                if (prefab.Any(char.IsWhiteSpace) || result[section].Any(e => e.Prefab == prefab) ||
                    values.Length < 2 || values.Length > 3 ||
                    !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int min) ||
                    !int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int max) ||
                    min < 0 || max < min || max > 10000 ||
                    (values.Length == 3 && (!double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out chance) || double.IsNaN(chance) || chance < 0 || chance > 100)))
                    throw Invalid(number, "objet duplique, quantites invalides (0..10000) ou chance invalide (0..100)");
                if (result[section].Count >= 64) throw Invalid(number, "maximum 64 objets par section");
                result[section].Add(new Entry { Prefab = prefab, Minimum = min, Maximum = max, Chance = chance });
            }
            if(!result.ContainsKey("Mistlands"))result.Add("Mistlands",new List<Entry>{new Entry{Prefab="BlackCore",Minimum=1,Maximum=1,Chance=100},new Entry{Prefab="RoyalJelly",Minimum=6,Maximum=10,Chance=100},new Entry{Prefab="Coins",Minimum=150,Maximum=200,Chance=100}});
            if (result.Count != 4) throw Invalid(number, "sections Forest, Swamp et Mountain requises (peuvent etre vides)");
            return result.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
        }
        private static InvalidDataException Invalid(int line, string reason) => new InvalidDataException(FileName + " ligne " + line + " : " + reason);
        internal static Entry[] For(string family) { if (tables == null) Initialize(); return tables[family]; }
    }
}

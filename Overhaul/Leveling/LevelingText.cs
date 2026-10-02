using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Overhaul.Leveling
{
    internal static class LevelingText
    {
        private static readonly Dictionary<string,Dictionary<string,string>> Languages=new Dictionary<string,Dictionary<string,string>>();
        internal static string Get(string key)
        {
            string lang=PlatformPrefs.GetString("language","English")=="French"?"FR":"EN";
            return Get(key,lang);
        }
        internal static string Get(string key,string language)
        {
            if(!Languages.TryGetValue(language,out var words))
            {
                string file="translations"+language+".json";
                string path=Path.Combine(LevelingConfig.DirectoryPath,"Localisation",file);
                using(var stream=typeof(LevelingText).Assembly.GetManifestResourceStream("Overhaul.Defaults."+file))
                    using(var reader=new StreamReader(stream))words=JsonConvert.DeserializeObject<Dictionary<string,string>>(reader.ReadToEnd());
                if(File.Exists(path))foreach(var entry in JsonConvert.DeserializeObject<Dictionary<string,string>>(File.ReadAllText(path)))words[entry.Key]=entry.Value;
                Languages[language]=words;
            }
            return words.TryGetValue(key,out string value)?value:language=="EN"?key:Get(key,"EN");
        }
    }
}

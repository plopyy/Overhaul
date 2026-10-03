using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace Overhaul.Persistence
{
    internal static class NameCatalog
    {
        private static readonly ConcurrentDictionary<int,string> Prefabs = new ConcurrentDictionary<int,string>();
        private static readonly ConcurrentDictionary<int,string> Keys = new ConcurrentDictionary<int,string>();
        static NameCatalog()
        {
            using(var stream=typeof(NameCatalog).Assembly.GetManifestResourceStream("Overhaul.Persistence.names.tsv"))
            using(var reader=new StreamReader(stream ?? throw new InvalidDataException("Missing persistence name catalogue")))
            {
                string line;while((line=reader.ReadLine())!=null)
                {
                    var parts=line.Split('\t');int hash=int.Parse(parts[1]);string name=Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
                    if(NativeFormat.Hash(name)!=hash)throw new InvalidDataException("Invalid name hash");
                    (parts[0]=="p"?Prefabs:Keys).TryAdd(hash,name);
                }
            }
        }
        internal static string Prefab(int hash) => Prefabs.TryGetValue(hash,out var value)?value:null;
        internal static string Key(int hash) => Keys.TryGetValue(hash,out var value)?value:null;
        internal static void AddPrefab(string name) { if(!string.IsNullOrEmpty(name))Prefabs.TryAdd(NativeFormat.Hash(name),name); }
        internal static void AddKey(string name) { if(!string.IsNullOrEmpty(name))Keys.TryAdd(NativeFormat.Hash(name),name); }
    }
}

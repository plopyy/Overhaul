using System;
using System.IO;
using System.Reflection;
class ColdStartCheck
{
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (s,e) => {
            string name=new AssemblyName(e.Name).Name;
            if(name=="Unity.Auga" || name=="fastJSON" || name=="APIManager") { Console.WriteLine("EMBEDDED NOT YET LOADED: "+name);return null; }
            string path=Path.Combine(@"D:\ValheimModdings\Libs",name+".dll");
            if(!File.Exists(path))path=Path.Combine(@"D:\ValheimModdings\Libs",name+"_publicized.dll");
            if(!File.Exists(path))path=Path.Combine(@"D:\GameServeur\Valheim\valheim_server_Data\Managed",name+".dll");
            return File.Exists(path)?Assembly.LoadFrom(path):null;
        };
        try {
            var assembly=Assembly.LoadFrom(args[0]);
            var type=assembly.GetType("Overhaul.Overhaul",true);
            Console.WriteLine("TYPE OK "+assembly.GetName().Version);
            var method=type.GetMethod("Awake");
            method.MethodHandle.GetFunctionPointer();
            Console.WriteLine("AWAKE JIT OK before embedded dependencies");
            assembly.GetType("Overhaul.IntegratedUi",true).GetMethod("LoadDependencies",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
            Console.WriteLine("EMBEDDED DEPENDENCIES LOADED");
            type.GetMethod("Initialize",BindingFlags.Instance|BindingFlags.NonPublic).MethodHandle.GetFunctionPointer();
            Console.WriteLine("INITIALIZE JIT OK after embedded dependencies");
            return 0;
        } catch(Exception e){Console.WriteLine(e);return 1;}
    }
}

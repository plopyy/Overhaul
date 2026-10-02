const fs=require('fs'),p=require('path');const src=p.join(__dirname,'../Tools/Auga-main/AugaUnity/Assets/Editor'),dst=p.join(src,'OverhaulV2');fs.mkdirSync(dst,{recursive:true});
fs.mkdirSync(p.join(__dirname,'../Tools/OverhaulV2Work'),{recursive:true});
for(const [old,now] of [['OverhaulDungeonCheck','OverhaulV2Check'],['OverhaulLevelingChecks','OverhaulV2LevelingChecks'],['AugaOverhaulChecks','OverhaulV2UiChecks']]){
 let s=fs.readFileSync(p.join(src,old+'.cs'),'utf8').replaceAll(old,now);
 if(old==='OverhaulDungeonCheck'){
  s=s.replaceAll('OverhaulLevelingChecks','OverhaulV2LevelingChecks');
  s=s.replace('runtime = plugin.GetType','Call(plugin.GetType("Overhaul.IntegratedUi"), "LoadDependencies");\n        runtime = plugin.GetType');
  s=s.replace('await SwampBossChecks.Run(plugin);','await SwampBossChecks.Run(plugin);\n            await MountainBossChecks.Run(plugin);');
  s=s.replace('            LevelingNetworkChecks();','            OverhaulV2BootstrapChecks.Run(plugin);\n            LevelingNetworkChecks();');
  s=s.replaceAll('../../../Tools/DungeonResearch/check-results.txt','../../../Tools/OverhaulV2Work/check-results.txt');
 }
 if(old==='OverhaulLevelingChecks'){
  s=s.replace('        UiContract(plugin);','');
  s=s.replace(/    static void UiContract\(Assembly plugin\)[\s\S]*?    static void FeedChecks/,'    static void FeedChecks');
  s=s.replaceAll('AugaOverhaulChecks','OverhaulV2UiChecks');
 }
 if(old==='AugaOverhaulChecks'){
  s=s.replace('Assembly.LoadFrom(Path.GetFullPath("../Auga/bin/Release/Auga.dll"))','overhaul');
  s=s.replace('var bundle=AssetBundle.LoadFromFile("AssetBundles/augaassets");','AssetBundle bundle; using(var stream=overhaul.GetManifestResourceStream("Overhaul.augaassets"))using(var buffer=new MemoryStream()){stream.CopyTo(buffer);bundle=AssetBundle.LoadFromMemory(buffer.ToArray());}');
  s=s.replace(/var tabs=right.GetComponent<AugaTabController>\(\);bridge.Invoke[\s\S]*?            var valueType=/,'var tabs=right.GetComponent<AugaTabController>();\n            var valueType=');
  s=s.replace(/            overhaul.GetType\("Overhaul.Leveling.LevelingCloseWindowPatch"\)[^\n]+/,'            Check(overhaul.GetType("Overhaul.Leveling.LevelingOpenWindowPatch")==null,"No separate vanilla leveling window hook");');
  s=s.replaceAll('no-plugin fallback, optional second tab','integrated mandatory second tab');
  s=s.replaceAll('../../../Tools/AugaWork/overhaul-check-results.txt','../../../Tools/OverhaulV2Work/ui-check-results.txt');
 }
 fs.writeFileSync(p.join(dst,now+'.cs'),s);
}

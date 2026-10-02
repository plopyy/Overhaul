using System;
using System.IO;
using System.Reflection;
using UnityEngine.InputSystem;
public static class RelocationShortcutCheck
{
 public static void Run()
 {
  var mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));
  var shortcut=mod.GetType("Overhaul.Storage.RelocationShortcut");
  var resolve=shortcut.GetMethod("Resolve",BindingFlags.Static|BindingFlags.NonPublic);
  InputSystem.RegisterLayout("{\"name\":\"RelocationLayoutCheck\",\"extend\":\"Keyboard\",\"controls\":[{\"name\":\"h\",\"displayName\":\"X\"},{\"name\":\"j\",\"displayName\":\"H\"}]}");
  var standard=InputSystem.AddDevice<Keyboard>();
  if(resolve.Invoke(null,new object[]{standard})!=standard.hKey)throw new Exception("Standard H resolution");
  InputSystem.RemoveDevice(standard);
  var moved=(Keyboard)InputSystem.AddDevice("RelocationLayoutCheck");
  if(resolve.Invoke(null,new object[]{moved})!=moved.jKey)throw new Exception("Printed H must win over physical H: "+moved.hKey.displayName+" / "+moved.jKey.displayName);
  InputSystem.RemoveDevice(moved);
  File.WriteAllText("../../../Tools/AugaWork/relocation-shortcut-checks.txt","PASS standard H\nPASS printed H follows changed keyboard layout\n");
  RelocationHintPreview.Run();
 }
}

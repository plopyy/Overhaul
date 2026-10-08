using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Valheim.SettingsGui;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.EventSystems;

namespace AugaUnity {
 public class AugaModsSettings : MonoBehaviour, ISettingsTab {
  public static Func<int,string> ReadShortcut;
  public static Func<KeyCode[],string> FormatShortcut;
  public static Func<string,string> DisplayShortcut;
  public GameObject BindingDialog;
  InputAction bindingAction;
  InputActionRebindingExtensions.RebindingOperation rebind;
  public static Action<int,string> WriteShortcut;
  public static Func<bool> IsEqsActive;
  public static Func<bool> ReadTrash;
  public static Action<bool> WriteTrash;
  public Button AugaTab;
  public GameObject AugaPage;
  public Toggle TrashToggle;
  Button backButton, applyButton;
  bool Available=>ReadShortcut!=null && WriteShortcut!=null && IsEqsActive!=null && IsEqsActive();
  void RefreshVisibility(){
   EqsTab.gameObject.SetActive(true);
   Notice.transform.parent.gameObject.SetActive(true);
   if(AugaPage)AugaPage.SetActive(false);
   if(AugaTab)AugaTab.gameObject.SetActive(false);
   EqsTab.transform.Find("Selected").gameObject.SetActive(true);
   var divider=transform.Find("VerticalDivider");if(divider)divider.gameObject.SetActive(true);
  }
  public Button[] BindButtons;
  public AugaBindingDisplay[] Displays;
  public Button EqsTab;
  public TMP_Text Notice;
  // Sized from BindButtons: Overhaul can append shortcut rows at runtime.
  string[] pending=new string[0];
  bool[] dirty=new bool[0];
  int capture=-1;
  bool restoreNavigation;
  // ZInput accepts legacy KeyCode values, but unmapped keys resolve to Key.None
  // and throw when read from the current keyboard. Only poll supported controls.
  static readonly KeyCode[] CaptureKeys=((KeyCode[])Enum.GetValues(typeof(KeyCode)))
   .Where(k=>(k>=KeyCode.Mouse0 && k<=KeyCode.Mouse4) ||
    (ZInput.TryKeyCodeToKey(k,out var mapped) && mapped!=UnityEngine.InputSystem.Key.None))
   .Distinct().ToArray();
  Settings owner;
  public event Action<string,int> SharedSettingChanged {add{} remove{}}
  public void Initialize(){owner=GetComponentInParent<Settings>();Reload();}
  void Reload(){
   bool available=Available;RefreshVisibility();
   if(pending.Length!=BindButtons.Length){pending=new string[BindButtons.Length];dirty=new bool[BindButtons.Length];}
   for(int i=0;i<BindButtons.Length;i++){pending[i]=available?ReadShortcut(i):"";dirty[i]=false;BindButtons[i].interactable=available;Displays[i].SetText(DisplayShortcut!=null?DisplayShortcut(pending[i]):pending[i]);}
   Notice.text=Localization.instance.Localize(available?"$auga_mods_eqs_help":"$auga_mods_eqs_missing");
  }
  public void SelectEqs(){EndCapture();RefreshVisibility();UpdateNavigation();}
  public void SelectAuga(){SelectEqs();}
  public void BeginBinding(int index){
   if(!Available || index<0 || index>=BindButtons.Length)return;
   EndCapture();capture=index;
   if(owner)owner.BlockNavigation(true);
   foreach(var b in BindButtons)b.interactable=false;
   BindingDialog.SetActive(true);
   if(EventSystem.current)EventSystem.current.SetSelectedGameObject(BindingDialog);
   bindingAction=new InputAction("AugaEqsBinding",InputActionType.Button,"<Keyboard>/space");
   // Same interactive rebinding mechanism used by ZInput.StartBindKey.
   rebind=bindingAction.PerformInteractiveRebinding().WithExpectedControlType("Button")
    .WithCancelingThrough("<Keyboard>/escape").WithControlsExcluding("<Gamepad>/*")
    .OnComplete(op=>{AcceptControl(op.selectedControl);EndCapture(true);})
    .OnCancel(op=>EndCapture(true));
   foreach(var path in new[]{"leftShift","rightShift","leftCtrl","rightCtrl","leftAlt","rightAlt","leftMeta","rightMeta"})rebind.WithControlsExcluding("<Keyboard>/"+path);
   rebind.Start();
  }
  static bool Modifier(KeyCode k)=>k==KeyCode.LeftShift||k==KeyCode.RightShift||k==KeyCode.LeftControl||k==KeyCode.RightControl||k==KeyCode.LeftAlt||k==KeyCode.RightAlt||k==KeyCode.LeftCommand||k==KeyCode.RightCommand;
  void AcceptControl(InputControl control){
   KeyCode key=KeyCode.None;
   if(control is KeyControl keyboardKey)key=CaptureKeys.FirstOrDefault(k=>ZInput.TryKeyCodeToKey(k,out var mapped)&&mapped==keyboardKey.keyCode);
   else if(Mouse.current!=null){var mouse=Mouse.current;var controls=new InputControl[]{mouse.leftButton,mouse.rightButton,mouse.middleButton,mouse.forwardButton,mouse.backButton};int i=Array.IndexOf(controls,control);if(i>=0)key=(KeyCode)((int)KeyCode.Mouse0+i);}
   if(key==KeyCode.None)return;
   var keys=new System.Collections.Generic.List<KeyCode>{key};
   foreach(var modifier in CaptureKeys.Where(Modifier))if(ZInput.GetKey(modifier,false))keys.Add(modifier);
   SetPending(capture,FormatShortcut(keys.ToArray()));
  }
  public static string KeyLabel(KeyCode key){
   if(Keyboard.current!=null && ZInput.TryKeyCodeToKey(key,out var mapped) && mapped!=Key.None){var name=Keyboard.current[mapped].displayName;if(!string.IsNullOrEmpty(name))return name;}
   return key==KeyCode.None?"":key.ToString();
  }
  public void SetPending(int index,string value){pending[index]=value;dirty[index]=true;Displays[index].SetText(DisplayShortcut!=null?DisplayShortcut(value):value);}
  void EndCapture(bool deferNavigation=false){
   if(capture<0)return;capture=-1;
   var operation=rebind;rebind=null;if(operation!=null)operation.Dispose();
   bindingAction?.Dispose();bindingAction=null;
   if(BindingDialog)BindingDialog.SetActive(false);
   if(deferNavigation)restoreNavigation=true;else if(owner)owner.BlockNavigation(false);
   foreach(var b in BindButtons)b.interactable=ReadShortcut!=null;
   Notice.text=Localization.instance.Localize("$auga_mods_eqs_help");
  }
  void LateUpdate(){if(restoreNavigation){restoreNavigation=false;if(owner)owner.BlockNavigation(false);}}
  void OnDisable(){EndCapture();LateUpdate();}
  public void OnBack(){EndCapture();Reload();}
  public void Terminate(){EndCapture();}
  public void OnSharedSettingChanged(string key,int value){}
  public void OnOkAsync(OkActionCompletedHandler callback){
   EndCapture();
   for(int i=0;i<BindButtons.Length;i++)if(dirty[i] && Available){WriteShortcut(i,pending[i]);dirty[i]=false;}
   callback?.Invoke();
  }
  public void OnTabOpen(Button back,Button apply){
   backButton=back;applyButton=apply;RefreshVisibility();UpdateNavigation();
  }
  void UpdateNavigation(){
   var back=backButton;var apply=applyButton;if(!back||!apply)return;
   RefreshVisibility();
   Selectable first=Available ? BindButtons[0] : apply;
   Selectable last=Available ? BindButtons[BindButtons.Length-1] : first;
   EqsTab.navigation=new Navigation{mode=Navigation.Mode.Explicit,selectOnRight=first,selectOnDown=apply};
   for(int i=0;i<BindButtons.Length;i++){
    BindButtons[i].navigation=new Navigation{mode=Navigation.Mode.Explicit,selectOnUp=i==0?EqsTab:BindButtons[i-1],selectOnDown=i==BindButtons.Length-1?apply:BindButtons[i+1],selectOnLeft=EqsTab};
   }
   GuiUtils.SetNavigationUp(back,last);GuiUtils.SetNavigationUp(apply,last);
  }
 }
}

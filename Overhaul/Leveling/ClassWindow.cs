using AugaUnity;
using EquipmentAndQuickSlots;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Overhaul.Leveling
{
    internal static class ClassWindow
    {
        static GameObject root;
        static Player owner;
        static int closedFrame=-1;
        internal static bool Visible=>root&&root.activeInHierarchy;
        internal static bool BlocksInput=>Visible||closedFrame==Time.frameCount;
        internal static bool Shortcut(bool held=false)=>ValConfig.ClassWindowKey!=null&&
            (held?PreventSimilarHotkeys.IsShortcutPressed(ValConfig.ClassWindowKey.Value):PreventSimilarHotkeys.IsShortcutDown(ValConfig.ClassWindowKey.Value));
        internal static void Tick()
        {
            var player=Player.m_localPlayer;
            if(!player||player!=owner){Clear();owner=player;}
            if(!player)return;
            if(Visible){
                if(player.IsDead()||player.IsTeleporting()||Menu.IsVisible()||Console.IsVisible()||InventoryGui.IsVisible()||Minimap.IsOpen()||StoreGui.IsVisible()||TextInput.IsVisible()||(Chat.instance&&Chat.instance.HasFocus())){Close();return;}
                if(Shortcut()||ZInput.GetKeyDown(KeyCode.Escape)||ZInput.GetButtonDown("JoyButtonB")){Close();return;}
            }
            else if(player.TakeInput()&&!player.IsDead()&&!player.InPlaceMode()&&!InventoryGui.IsVisible()&&!Minimap.IsOpen()&&!StoreGui.IsVisible()&&Shortcut())Open();
        }
        static void Open()
        {
            if(!Auga.Auga.Assets.ClassPanel||!Hud.instance)return;
            bool changed=ClassSkillConfig.Reload();
            if(!root){
                root=new GameObject("OverhaulClasses",typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster));
                root.SetActive(false);root.transform.SetParent(Hud.instance.transform,false);
                var canvas=root.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.overrideSorting=true;canvas.sortingOrder=1200;
                var scaler=root.GetComponent<CanvasScaler>();scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;scaler.referenceResolution=new Vector2(1920,1080);scaler.matchWidthOrHeight=.5f;scaler.referencePixelsPerUnit=50;
                var panel=Object.Instantiate(Auga.Auga.Assets.ClassPanel,root.transform,false);
                Localization.instance.Localize(panel.transform);
                var demo=panel.GetComponent<ClassPreviewPanel>();demo.CloseRequested=Close;demo.Generate(ClassSkillConfig.Current);
            }
            else if(changed)root.GetComponentInChildren<ClassPreviewPanel>(true).Generate(ClassSkillConfig.Current);
            root.SetActive(true);ZCursor.LockState=CursorLockMode.None;ZCursor.Show();
        }
        internal static void Close(){if(root)root.SetActive(false);closedFrame=Time.frameCount;if(GameCamera.instance)GameCamera.instance.UpdateMouseCapture();}
        internal static void Clear(){if(root){Close();Object.Destroy(root);}root=null;owner=null;}
        [HarmonyPatch(typeof(Player),nameof(Player.TakeInput))]
        static class PlayerInput {static bool Prefix(ref bool __result){if(!BlocksInput)return true;__result=false;return false;}}
        [HarmonyPatch(typeof(PlayerController),"TakeInput")]
        static class ControllerInput {static bool Prefix(ref bool __result){if(!BlocksInput)return true;__result=false;return false;}}
        [HarmonyPatch(typeof(GameCamera),nameof(GameCamera.UpdateMouseCapture))]
        static class CursorInput {static bool Prefix(){if(!Visible)return true;ZCursor.LockState=CursorLockMode.None;ZCursor.Show();return false;}}
        [HarmonyPatch(typeof(Menu),"Update")]
        static class MenuInput {static bool Prefix()=>!BlocksInput;}
    }
}

using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AugaUnity
{
    // All references are authored on the original Auga BuildHud prefab.
    public class AugaBuildHudBindings : MonoBehaviour
    {
        public TMP_Text Name;
        public TMP_Text Description;
        public Image Icon;
        public Image SnappingIcon;
        public GameObject[] Requirements;
        public GameObject Selection;
        public UIInputHandler CloseButton;
        public RectTransform PieceList;
        public GameObject CategoryRoot;
        public GameObject[] Categories;
        public BuildMenuPaginationController Pagination;

        public void Bind(Hud hud)
        {
            hud.m_buildHud = gameObject;
            hud.m_buildSelection = Name;
            hud.m_pieceDescription = Description;
            hud.m_buildIcon = Icon;
            hud.m_snappingIcon = SnappingIcon;
            hud.m_requirementItems = Requirements;
            hud.m_pieceSelectionWindow = Selection;
            hud.m_closePieceSelectionButton = CloseButton;
            hud.m_pieceListRoot = PieceList;
            hud.m_pieceCategoryRoot = CategoryRoot;
            hud.m_pieceCategoryTabs = Categories;
            CloseButton.m_onLeftClick += hud.OnClosePieceSelection;
            CloseButton.m_onRightClick += hud.OnClosePieceSelection;
            foreach (var category in Categories)
                category.GetComponent<UIInputHandler>().m_onLeftDown += hud.OnLeftClickCategory;
            Pagination.hud = hud;
            Selection.SetActive(false);
            CloseButton.gameObject.SetActive(false);
        }
    }
}

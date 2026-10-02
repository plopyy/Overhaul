using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
namespace AugaUnity {
 // Must follow AugaPanelNotches: shadow samples use the already-cut panel mesh.
 [RequireComponent(typeof(Graphic))]
 public class AugaPanelShadow : BaseMeshEffect {
  public float BlurRadius=10;
  public Vector2 Offset=new Vector2(0,-4);
  [Range(0,1)] public float Opacity=.45f;
  public override void ModifyMesh(VertexHelper helper){
   if(!IsActive())return;
   var source=new List<UIVertex>();helper.GetUIVertexStream(source);
   var result=new List<UIVertex>(source.Count*26);
   float total=0;for(int y=-2;y<=2;y++)for(int x=-2;x<=2;x++)total+=Mathf.Exp(-(x*x+y*y)/2f);
   for(int y=-2;y<=2;y++)for(int x=-2;x<=2;x++){
    float weight=Mathf.Exp(-(x*x+y*y)/2f)/total;
    float alpha=1-Mathf.Pow(1-Opacity,weight);
    var shift=(Vector3)(Offset+new Vector2(x,y)*(BlurRadius/2));
    foreach(var original in source){var v=original;v.position+=shift;v.color=new Color32(0,0,0,(byte)Mathf.RoundToInt(original.color.a*alpha));result.Add(v);}
   }
   result.AddRange(source);helper.Clear();helper.AddUIVertexTriangleStream(result);
  }
 }
}

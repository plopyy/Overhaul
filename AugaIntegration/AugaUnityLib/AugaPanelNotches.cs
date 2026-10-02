using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace AugaUnity
{
    // Authored background geometry: subtract diamond holes, preserving sprite UVs and gradients.
    // This component belongs to the prefab, not to a runtime window replacement patch.
    [RequireComponent(typeof(Graphic))]
    public class AugaPanelNotches : BaseMeshEffect, ICanvasRaycastFilter
    {
        public Vector2[] CentersFromTopLeft=new Vector2[0];
        public float Radius=26;

        static readonly Vector2[] Normals={new Vector2(1,1),new Vector2(1,-1),new Vector2(-1,-1),new Vector2(-1,1)};
        Vector2 Center(Vector2 offset){var r=((RectTransform)transform).rect;return new Vector2(r.xMin+offset.x,r.yMax-offset.y);}
        public override void ModifyMesh(VertexHelper helper)
        {
            if(!IsActive()||CentersFromTopLeft==null||CentersFromTopLeft.Length==0)return;
            var source=new List<UIVertex>();helper.GetUIVertexStream(source);var result=new List<UIVertex>();
            for(int i=0;i+2<source.Count;i+=3)
            {
                var pieces=new List<List<UIVertex>>{new List<UIVertex>{source[i],source[i+1],source[i+2]}};
                foreach(var offset in CentersFromTopLeft)
                {
                    var next=new List<List<UIVertex>>();var center=Center(offset);
                    foreach(var piece in pieces)
                    {
                        var inside=piece;
                        foreach(var normal in Normals)
                        {
                            var outside=Clip(inside,normal,center,Radius,false);if(outside.Count>=3)next.Add(outside);
                            inside=Clip(inside,normal,center,Radius,true);if(inside.Count<3)break;
                        }
                    }
                    pieces=next;
                }
                foreach(var p in pieces)for(int j=1;j+1<p.Count;j++){result.Add(p[0]);result.Add(p[j]);result.Add(p[j+1]);}
            }
            helper.Clear();helper.AddUIVertexTriangleStream(result);
        }
        static List<UIVertex> Clip(List<UIVertex> polygon,Vector2 normal,Vector2 center,float radius,bool inside)
        {
            var output=new List<UIVertex>();if(polygon.Count==0)return output;
            var a=polygon[polygon.Count-1];float da=Vector2.Dot(normal,(Vector2)a.position-center)-radius;bool keepA=inside?da<=0:da>=0;
            foreach(var b in polygon)
            {
                float db=Vector2.Dot(normal,(Vector2)b.position-center)-radius;bool keepB=inside?db<=0:db>=0;
                if(keepA!=keepB){float t=da/(da-db);var v=a;v.position=Vector3.LerpUnclamped(a.position,b.position,t);v.color=Color32.Lerp(a.color,b.color,t);v.uv0=Vector4.LerpUnclamped(a.uv0,b.uv0,t);v.uv1=Vector4.LerpUnclamped(a.uv1,b.uv1,t);output.Add(v);}
                if(keepB)output.Add(b);a=b;da=db;keepA=keepB;
            }
            return output;
        }
        public bool IsRaycastLocationValid(Vector2 screenPoint,Camera eventCamera)
        {
            if(!IsActive())return true;
            if(!RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)transform,screenPoint,eventCamera,out var p))return true;
            foreach(var offset in CentersFromTopLeft){var d=p-Center(offset);if(Mathf.Abs(d.x)+Mathf.Abs(d.y)<Radius)return false;}
            return true;
        }
    }
}

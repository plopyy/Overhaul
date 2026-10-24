using UnityEngine;
using UnityEngine.UI;

namespace AugaUnity
{
    // Vector faces stay crisp at the small native health-bar size and need no font glyphs.
    public sealed class IntelligenceFace : MaskableGraphic
    {
        public bool Smart;
        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            var ink=new Color32(29,24,23,255);
            Disc(mesh,0,0,.49f,ink);
            Disc(mesh,0,0,.42f,Smart?new Color32(112,215,228,255):new Color32(255,201,86,255));
            if(Smart)
            {
                Disc(mesh,-.18f,.10f,.145f,ink);Disc(mesh,.18f,.10f,.145f,ink);
                Disc(mesh,-.18f,.10f,.095f,new Color32(222,250,251,255));Disc(mesh,.18f,.10f,.095f,new Color32(222,250,251,255));
                Line(mesh,-.06f,.11f,.06f,.11f,.035f,ink);
                Disc(mesh,-.16f,.09f,.036f,ink);Disc(mesh,.20f,.09f,.036f,ink);
                Line(mesh,-.16f,-.17f,-.04f,-.23f,.045f,ink);Line(mesh,-.04f,-.23f,.13f,-.15f,.045f,ink);
            }
            else
            {
                var white=new Color32(250,249,235,255);
                var drool=new Color32(24,156,205,255);
                // Exaggerated silhouettes: even at 20 px the eyes, tooth and drool stay distinct.
                Disc(mesh,-.17f,.16f,.205f,white);Disc(mesh,.20f,.12f,.13f,white);
                Disc(mesh,-.20f,.20f,.078f,ink);Disc(mesh,.22f,.10f,.060f,ink);
                Line(mesh,-.10f,-.20f,-.10f,-.29f,.14f,white);Disc(mesh,-.10f,-.29f,.07f,white);
                Line(mesh,.27f,-.04f,.27f,-.28f,.11f,drool);Disc(mesh,.27f,-.28f,.065f,drool);
                Line(mesh,.21f,-.05f,.21f,-.16f,.10f,drool);Disc(mesh,.21f,-.16f,.05f,drool);
                Line(mesh,-.29f,-.13f,-.13f,-.19f,.065f,ink);
                Line(mesh,-.13f,-.19f,.01f,-.17f,.065f,ink);
                Line(mesh,.01f,-.17f,.17f,-.05f,.065f,ink);
                Line(mesh,.17f,-.05f,.29f,-.025f,.065f,ink);
                Disc(mesh,-.29f,-.13f,.0325f,ink);Disc(mesh,.29f,-.025f,.0325f,ink);
            }
        }
        private Vector2 Point(float x,float y)=>rectTransform.rect.center+new Vector2(x,y)*Mathf.Min(rectTransform.rect.width,rectTransform.rect.height);
        private void Disc(VertexHelper mesh,float x,float y,float radius,Color32 tint)
        {
            int first=mesh.currentVertCount;mesh.AddVert(Point(x,y),tint,Vector2.zero);
            const int count=32;
            for(int i=0;i<count;i++){float a=i*Mathf.PI*2/count;mesh.AddVert(Point(x+Mathf.Cos(a)*radius,y+Mathf.Sin(a)*radius),tint,Vector2.zero);}
            for(int i=0;i<count;i++)mesh.AddTriangle(first,first+1+i,first+1+(i+1)%count);
        }
        private void Line(VertexHelper mesh,float x1,float y1,float x2,float y2,float width,Color32 tint)
        {
            var a=new Vector2(x1,y1);var b=new Vector2(x2,y2);var d=(b-a).normalized;var n=new Vector2(-d.y,d.x)*width*.5f;
            int first=mesh.currentVertCount;
            foreach(var p in new[]{a+n,b+n,b-n,a-n})mesh.AddVert(Point(p.x,p.y),tint,Vector2.zero);
            mesh.AddTriangle(first,first+1,first+2);mesh.AddTriangle(first,first+2,first+3);
        }
    }
}

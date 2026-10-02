using UnityEngine;

namespace Overhaul.Storage
{
    // Map symbols drawn as small, outlined silhouettes, with four samples per axis.
    internal static class VehicleMarkerIcons
    {
        static readonly Sprite[] sprites=new Sprite[3];
        internal static Color ColorFor(VehicleMarkers.Kind type)=>type==VehicleMarkers.Kind.Ship?new Color(.06f,.28f,1f):type==VehicleMarkers.Kind.Cart?new Color(.3f,.9f,.35f):new Color(1f,.28f,.25f);
        static bool Box(float x,float y,float left,float bottom,float right,float top)=>x>=left&&x<=right&&y>=bottom&&y<=top;
        static bool Disc(float x,float y,float cx,float cy,float radius)=>(x-cx)*(x-cx)+(y-cy)*(y-cy)<=radius*radius;
        static bool Polygon(float x,float y,Vector2[] points)
        {
            bool inside=false;
            for(int i=0,j=points.Length-1;i<points.Length;j=i++)
                if((points[i].y>y)!=(points[j].y>y)&&x<(points[j].x-points[i].x)*(y-points[i].y)/(points[j].y-points[i].y)+points[i].x)inside=!inside;
            return inside;
        }
        static readonly Vector2[] Hull={new Vector2(7,25),new Vector2(57,25),new Vector2(47,13),new Vector2(18,13)};
        static readonly Vector2[] Sail={new Vector2(29,53),new Vector2(29,29),new Vector2(9,29)};
        static readonly Vector2[] Sail2={new Vector2(35,51),new Vector2(53,29),new Vector2(35,29)};
        static readonly Vector2[] Roof={new Vector2(8,43),new Vector2(18,53),new Vector2(46,53),new Vector2(54,43)};
        static bool Shape(int type,float x,float y)
        {
            if(type==0)return Polygon(x,y,Hull)||Polygon(x,y,Sail)||Polygon(x,y,Sail2)||Box(x,y,30,24,34,56);
            bool wheels=Disc(x,y,19,14,6)||Disc(x,y,46,14,6);
            if(type==1)return wheels||Box(x,y,12,24,48,44)||Box(x,y,9,20,52,24)||Box(x,y,48,35,60,39);
            return wheels||Polygon(x,y,Roof)||Box(x,y,14,21,18,44)||Box(x,y,46,21,50,44)||Box(x,y,10,20,54,24)||Box(x,y,7,29,57,36)||Disc(x,y,57,33,5);
        }
        internal static Sprite Get(VehicleMarkers.Kind kind)
        {
            int type=(int)kind;if(sprites[type])return sprites[type];
            var pixels=new Color[64*64];
            for(int y=0;y<64;y++)for(int x=0;x<64;x++)
            {
                float fill=0,outline=0;
                for(int sy=0;sy<4;sy++)for(int sx=0;sx<4;sx++)
                {
                    float px=x+(sx+.5f)/4,py=y+(sy+.5f)/4;
                    if(Shape(type,px,py)){fill++;outline++;}
                    else for(int angle=0;angle<8;angle++)if(Shape(type,px+Mathf.Cos(angle*Mathf.PI/4)*1.6f,py+Mathf.Sin(angle*Mathf.PI/4)*1.6f)){outline++;break;}
                }
                float gray=outline>0?fill/outline:0;pixels[y*64+x]=new Color(gray,gray,gray,outline/16);
            }
            var texture=new Texture2D(64,64,TextureFormat.RGBA32,false){name="Overhaul vehicle "+kind,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};
            texture.SetPixels(pixels);texture.Apply();return sprites[type]=Sprite.Create(texture,new Rect(0,0,64,64),new Vector2(.5f,.5f),64);
        }
    }
}

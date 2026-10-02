using System;
using System.Collections.Generic;
using UnityEngine;

namespace Overhaul.Storage
{
    internal sealed class RelocationGuide:IDisposable
    {
        readonly GameObject root=new GameObject("Overhaul relocation guide");
        readonly Material material;
        readonly LineRenderer from,to;
        readonly List<LineRenderer> dashes=new List<LineRenderer>();
        internal RelocationGuide()
        {
            material=new Material(Shader.Find("Sprites/Default"));material.color=Color.white;
            from=Line("Origin",65);to=Line("Destination",65);
        }
        LineRenderer Line(string name,int count)
        {
            var go=new GameObject(name);go.transform.SetParent(root.transform,false);var line=go.AddComponent<LineRenderer>();
            line.sharedMaterial=material;line.useWorldSpace=true;line.widthMultiplier=.035f;line.positionCount=count;
            line.startColor=line.endColor=Color.white;line.numCapVertices=2;line.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;
            return line;
        }
        static void Ring(LineRenderer line,Vector3 point)
        {for(int i=0;i<65;i++){float angle=i*Mathf.PI*2/64;line.SetPosition(i,point+new Vector3(Mathf.Cos(angle)*.65f,.06f,Mathf.Sin(angle)*.65f));}}
        internal void Update(Vector3 origin,Vector3 destination,bool visible)
        {
            root.SetActive(visible);if(!visible)return;Ring(from,origin);Ring(to,destination);
            var delta=destination-origin;float distance=delta.magnitude;var dir=distance>.001f?delta/distance:Vector3.zero;
            int count=Mathf.Min(100,Mathf.CeilToInt(Mathf.Max(0,distance-1.3f)/.5f));
            while(dashes.Count<count)dashes.Add(Line("Dash",2));
            for(int i=0;i<dashes.Count;i++)
            {
                dashes[i].gameObject.SetActive(i<count);if(i>=count)continue;
                float start=.65f+i*.5f,end=Mathf.Min(start+.26f,distance-.65f);
                dashes[i].SetPosition(0,origin+dir*start+Vector3.up*.06f);dashes[i].SetPosition(1,origin+dir*end+Vector3.up*.06f);
            }
        }
        internal static void Release(UnityEngine.Object value)
        {if(!value)return;if(Application.isPlaying)UnityEngine.Object.Destroy(value);else UnityEngine.Object.DestroyImmediate(value);}
        public void Dispose(){Release(root);Release(material);}
    }
}

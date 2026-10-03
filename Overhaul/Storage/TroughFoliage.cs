using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overhaul.Storage
{
    // Only the decorative leaves are trimmed. Shared native meshes and vegetable bodies remain intact.
    internal static class TroughFoliage
    {
    struct LeafVertex {
        public Vector3 p,n;public Vector2 uv;public Vector4 tangent;public Color color;public float height;
        public static LeafVertex Lerp(LeafVertex a,LeafVertex b,float t){return new LeafVertex{p=Vector3.Lerp(a.p,b.p,t),n=Vector3.Lerp(a.n,b.n,t),uv=Vector2.Lerp(a.uv,b.uv,t),tangent=Vector4.Lerp(a.tangent,b.tangent,t),color=Color.Lerp(a.color,b.color,t),height=Mathf.Lerp(a.height,b.height,t)};}
    }
    internal static void Trim(Transform food,Transform root){
        const float cutoff=.345f;
        foreach(var filter in food.GetComponentsInChildren<MeshFilter>()){
            string mat=filter.GetComponent<MeshRenderer>().sharedMaterial.name;
            if(mat!="oak_leaf"&&mat!="carrot_blast"&&mat!="Turnip")continue;
            var source=filter.sharedMesh;
            if(!source.isReadable){UnityEngine.Object.DestroyImmediate(filter.gameObject);continue;}
            var v=source.vertices;var n=source.normals;var uv=source.uv;var tangents=source.tangents;var colors=source.colors;var triangles=source.triangles;
            var matrix=root.worldToLocalMatrix*filter.transform.localToWorldMatrix;
            var output=new List<LeafVertex>();
            for(int i=0;i<triangles.Length;i+=3){
                var polygon=new List<LeafVertex>();
                for(int j=0;j<3;j++){int k=triangles[i+j];polygon.Add(new LeafVertex{p=v[k],n=n.Length>k?n[k]:Vector3.up,uv=uv.Length>k?uv[k]:Vector2.zero,tangent=tangents.Length>k?tangents[k]:new Vector4(1,0,0,1),color=colors.Length>k?colors[k]:Color.white,height=matrix.MultiplyPoint3x4(v[k]).y});}
                var cut=new List<LeafVertex>();
                for(int j=0;j<polygon.Count;j++){
                    var a=polygon[j];var b=polygon[(j+1)%polygon.Count];bool inside=a.height>=cutoff,next=b.height>=cutoff;
                    if(inside)cut.Add(a);if(inside!=next)cut.Add(LeafVertex.Lerp(a,b,(cutoff-a.height)/(b.height-a.height)));
                }
                for(int j=1;j+1<cut.Count;j++){output.Add(cut[0]);output.Add(cut[j]);output.Add(cut[j+1]);}
            }
            if(output.Count==0){UnityEngine.Object.DestroyImmediate(filter.gameObject);continue;}
            var mesh=new Mesh{name="Trough trimmed "+source.name};mesh.SetVertices(output.Select(x=>x.p).ToList());mesh.SetNormals(output.Select(x=>x.n).ToList());mesh.SetUVs(0,output.Select(x=>x.uv).ToList());mesh.SetTangents(output.Select(x=>x.tangent).ToList());mesh.SetColors(output.Select(x=>x.color).ToList());mesh.SetTriangles(Enumerable.Range(0,output.Count).ToArray(),0);mesh.RecalculateBounds();filter.sharedMesh=mesh;
        }

    }    }
}

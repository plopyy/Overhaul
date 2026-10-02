using UnityEngine;
using UnityEngine.UI;

namespace AugaUnity
{
    // Monochrome outline glyphs, deliberately unlike filled item artwork.
    public sealed class EquipmentSlotHint : MaskableGraphic
    {
        public int Slot;
        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            switch (Slot)
            {
                case 8: // Apple, stem and leaf: food only.
                    Line(mesh,50,68, 35,75, 23,70, 17,57, 20,40, 30,22, 42,18, 50,23, 58,18, 70,22, 80,40, 83,57, 77,70, 65,75, 50,68);
                    Line(mesh,50,68, 48,86); Line(mesh,50,79, 61,89, 73,88, 64,78, 50,79); break;
                case 9: // Two arrows.
                    Line(mesh,31,17, 31,80); Line(mesh,20,67, 31,84, 42,67);
                    Line(mesh,20,21, 31,32, 42,21); Line(mesh,60,17, 60,69);
                    Line(mesh,49,56, 60,73, 71,56); Line(mesh,49,21, 60,32, 71,21); break;
                case 0: // Helmet, brow and nose guard.
                    Line(mesh, 20,35, 20,57, 29,76, 50,84, 71,76, 80,57, 80,35);
                    Line(mesh, 20,48, 50,43, 80,48); Line(mesh,50,80,50,24); break;
                case 1: // Tunic.
                    Line(mesh,36,82, 21,76, 9,56, 25,46, 31,57, 31,18, 69,18, 69,57, 75,46, 91,56, 79,76, 64,82);
                    Line(mesh,36,82, 40,69, 60,69, 64,82); break;
                case 2: // Trousers.
                    Line(mesh,28,82, 72,82, 79,18, 57,18, 50,55, 43,18, 21,18, 28,82);
                    Line(mesh,28,72,72,72); break;
                case 3: // Cloak and clasp.
                    Line(mesh,38,83, 62,83, 68,70, 83,18, 63,22, 50,16, 37,22, 17,18, 32,70, 38,83);
                    Line(mesh,38,83,50,66,62,83); Line(mesh,50,60,50,30); break;
                case 5: // Pendant / trinket.
                    Line(mesh,25,83, 35,58, 50,46, 65,58, 75,83);
                    Line(mesh,50,46, 65,30, 50,14, 35,30, 50,46); break;
                default: // Utility belt with buckle.
                    Line(mesh,12,62, 39,62, 39,38, 12,38, 12,62);
                    Line(mesh,61,62, 88,62, 88,38, 61,38);
                    Line(mesh,39,68, 61,68, 61,32, 39,32, 39,68);
                    Line(mesh,49,50,68,50); break;
            }
        }

        void Line(VertexHelper mesh, params float[] coordinates)
        {
            var rect = GetPixelAdjustedRect();
            float scale = Mathf.Min(rect.width, rect.height) / 100f;
            float halfWidth = 1.6f * scale;
            for (int i = 0; i + 3 < coordinates.Length; i += 2)
            {
                var a = rect.center + new Vector2(coordinates[i]-50, coordinates[i+1]-50) * scale;
                var b = rect.center + new Vector2(coordinates[i+2]-50, coordinates[i+3]-50) * scale;
                var delta = b-a;
                var normal = new Vector2(-delta.y, delta.x).normalized * halfWidth;
                var quad = new UIVertex[4];
                var points = new[] { a-normal, a+normal, b+normal, b-normal };
                for (int v=0;v<4;v++) { quad[v]=UIVertex.simpleVert; quad[v].position=points[v]; quad[v].color=color; }
                mesh.AddUIVertexQuad(quad);
            }
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace BabyBlocks
{
    internal static class PrimitiveMeshGen
    {
        public static Mesh BuildTorus(float majorRadius = 0.35f, float minorRadius = 0.15f,
                                      int segments = 24, int sides = 16)
        {
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs   = new List<Vector2>();
            var tris  = new List<int>();

            for (int i = 0; i <= segments; i++)
            {
                float u      = (float)i / segments;
                float theta  = u * Mathf.PI * 2f;
                var   centre = new Vector3(Mathf.Cos(theta) * majorRadius, 0f, Mathf.Sin(theta) * majorRadius);
                var   outDir = new Vector3(Mathf.Cos(theta), 0f, Mathf.Sin(theta));

                for (int j = 0; j <= sides; j++)
                {
                    float v   = (float)j / sides;
                    float phi = v * Mathf.PI * 2f;
                    var   n   = Mathf.Cos(phi) * outDir + new Vector3(0f, Mathf.Sin(phi), 0f);
                    verts.Add(centre + n * minorRadius);
                    norms.Add(n);
                    uvs.Add(new Vector2(u, v));
                }
            }

            for (int i = 0; i < segments; i++)
            {
                for (int j = 0; j < sides; j++)
                {
                    int a = i * (sides + 1) + j;
                    int b = a + sides + 1;
                    tris.Add(a); tris.Add(a + 1); tris.Add(b);
                    tris.Add(b); tris.Add(a + 1); tris.Add(b + 1);
                }
            }

            return BuildFromLists(verts, norms, uvs, tris, "Torus");
        }

        public static Mesh BuildCone(float radius = 0.5f, float height = 1f, int segments = 24)
        {
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs   = new List<Vector2>();
            var tris  = new List<int>();

            float half  = height * 0.5f;
            float slant = Mathf.Sqrt(radius * radius + height * height);
            float sinA  = height / slant;
            float cosA  = radius / slant;

            for (int i = 0; i <= segments; i++)
            {
                float t     = (float)i / segments;
                float theta = t * Mathf.PI * 2f;
                float cx    = Mathf.Cos(theta);
                float cz    = Mathf.Sin(theta);

                verts.Add(new Vector3(cx * radius, -half, cz * radius));
                norms.Add(new Vector3(cx * sinA, cosA, cz * sinA));
                uvs.Add(new Vector2(t, 0f));

                verts.Add(new Vector3(0f, half, 0f));
                norms.Add(new Vector3(cx * sinA, cosA, cz * sinA));
                uvs.Add(new Vector2(t + 0.5f / segments, 1f));
            }

            for (int i = 0; i < segments; i++)
            {
                int a = i * 2;
                tris.Add(a); tris.Add(a + 3); tris.Add(a + 2);
                tris.Add(a); tris.Add(a + 1); tris.Add(a + 3);
            }

            // Bottom cap
            int capBase = verts.Count;
            verts.Add(new Vector3(0f, -half, 0f));
            norms.Add(Vector3.down);
            uvs.Add(new Vector2(0.5f, 0.5f));

            for (int i = 0; i <= segments; i++)
            {
                float t     = (float)i / segments;
                float theta = t * Mathf.PI * 2f;
                float cx    = Mathf.Cos(theta);
                float cz    = Mathf.Sin(theta);
                verts.Add(new Vector3(cx * radius, -half, cz * radius));
                norms.Add(Vector3.down);
                uvs.Add(new Vector2(cx * 0.5f + 0.5f, cz * 0.5f + 0.5f));
            }

            for (int i = 0; i < segments; i++)
            {
                int center = capBase;
                int a      = capBase + 1 + i;
                int b      = capBase + 2 + i;
                tris.Add(center); tris.Add(a); tris.Add(b);
            }

            return BuildFromLists(verts, norms, uvs, tris, "Cone");
        }

        public static Mesh BuildHelix(float innerRadius = 0.15f, float outerRadius = 0.5f,
                                      float height = 0.6f, float turns = 2f,
                                      int segments = 128)
        {
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs   = new List<Vector2>();
            var tris  = new List<int>();

            float totalAngle = turns * Mathf.PI * 2f;
            float slope      = height / totalAngle; // height gained per radian

            // Top face
            int topBase = 0;
            for (int i = 0; i <= segments; i++)
            {
                float t     = (float)i / segments;
                float theta = t * totalAngle;
                float h     = t * height - height * 0.5f;
                float cosT  = Mathf.Cos(theta);
                float sinT  = Mathf.Sin(theta);

                var topN = new Vector3(sinT * slope, 1f, -cosT * slope).normalized;

                verts.Add(new Vector3(cosT * innerRadius, h, sinT * innerRadius));
                norms.Add(topN);
                uvs.Add(new Vector2(0f, t));

                verts.Add(new Vector3(cosT * outerRadius, h, sinT * outerRadius));
                norms.Add(topN);
                uvs.Add(new Vector2(1f, t));
            }

            for (int i = 0; i < segments; i++)
            {
                int a = topBase + i * 2;
                int b = topBase + i * 2 + 1;
                int c = topBase + (i + 1) * 2;
                int d = topBase + (i + 1) * 2 + 1;
                tris.Add(a); tris.Add(c); tris.Add(d);
                tris.Add(a); tris.Add(d); tris.Add(b);
            }

            // Bottom face
            int botBase = verts.Count;
            for (int i = 0; i <= segments; i++)
            {
                float t     = (float)i / segments;
                float theta = t * totalAngle;
                float h     = t * height - height * 0.5f;
                float cosT  = Mathf.Cos(theta);
                float sinT  = Mathf.Sin(theta);

                var botN = -new Vector3(sinT * slope, 1f, -cosT * slope).normalized;

                verts.Add(new Vector3(cosT * innerRadius, h, sinT * innerRadius));
                norms.Add(botN);
                uvs.Add(new Vector2(0f, t));

                verts.Add(new Vector3(cosT * outerRadius, h, sinT * outerRadius));
                norms.Add(botN);
                uvs.Add(new Vector2(1f, t));
            }

            // Reverse winding so front face points downward
            for (int i = 0; i < segments; i++)
            {
                int a = botBase + i * 2;
                int b = botBase + i * 2 + 1;
                int c = botBase + (i + 1) * 2;
                int d = botBase + (i + 1) * 2 + 1;
                tris.Add(a); tris.Add(d); tris.Add(c);
                tris.Add(a); tris.Add(b); tris.Add(d);
            }

            return BuildFromLists(verts, norms, uvs, tris, "Helix");
        }

        public static Mesh BuildEgg(int latSegs = 40, int lonSegs = 40)
        {
            const float a = 0.5f;   // half-height
            const float k = 0.9f;   // asymmetry: higher = more pronounced egg shape
            const float b = 0.38f;  // equatorial radius scale

            var verts = new List<Vector3>();
            var uvs   = new List<Vector2>();
            var tris  = new List<int>();

            for (int i = 0; i <= latSegs; i++)
            {
                float v     = (float)i / latSegs * Mathf.PI;
                float sinV  = Mathf.Sin(v);
                float cosV  = Mathf.Cos(v);
                float denom = 1f + k * a * cosV;
                float r     = denom > 1e-4f ? b * sinV / Mathf.Sqrt(denom) : 0f;
                float y     = a * cosV;

                for (int j = 0; j <= lonSegs; j++)
                {
                    float u     = (float)j / lonSegs;
                    float theta = u * Mathf.PI * 2f;
                    verts.Add(new Vector3(Mathf.Cos(theta) * r, y, Mathf.Sin(theta) * r));
                    uvs.Add(new Vector2(u, 1f - v / Mathf.PI));
                }
            }

            for (int i = 0; i < latSegs; i++)
            {
                for (int j = 0; j < lonSegs; j++)
                {
                    int a0 = i * (lonSegs + 1) + j;
                    int b0 = a0 + lonSegs + 1;
                    tris.Add(a0); tris.Add(a0 + 1); tris.Add(b0);
                    tris.Add(b0); tris.Add(a0 + 1); tris.Add(b0 + 1);
                }
            }

            var mesh = new Mesh { name = "Egg" };
            mesh.vertices  = verts.ToArray();
            mesh.uv        = uvs.ToArray();
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }


        static Mesh BuildFromLists(List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs,
                                   List<int> tris, string name)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices  = verts.ToArray();
            mesh.normals   = norms.ToArray();
            mesh.uv        = uvs.ToArray();
            mesh.triangles = tris.ToArray();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}

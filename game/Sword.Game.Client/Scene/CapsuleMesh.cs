using System.Numerics;
using System.Runtime.InteropServices;

namespace Sword.Game.Client.Scene;

/// <summary>Вершина единичной капсулы: нормаль и полушарие (−1 низ, +1 верх); форму задаёт вершинный шейдер.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct CapsuleVertex
{
    public Vector3 Normal;
    public float Cap;
}

/// <summary>Единичная капсула: колонны, позже сегменты.</summary>
public static class CapsuleMesh
{
    /// <summary>Кольца от нижнего полюса к верхнему; два экваториальных кольца — между ними шейдер растягивает цилиндр.</summary>
    public static void Build(List<CapsuleVertex> verts, List<uint> idx, int around = 16, int ringsPerHemi = 5)
    {
        uint baseIndex = (uint)verts.Count;
        verts.Add(new CapsuleVertex { Normal = -Vector3.UnitY, Cap = -1 });

        var ringStarts = new List<uint>();
        for (int hemi = 0; hemi < 2; hemi++)
        {
            float cap = hemi == 0 ? -1 : 1;
            for (int r = 0; r < ringsPerHemi; r++)
            {
                // нижняя: от полюса к экватору; верхняя: от экватора к полюсу
                float t = hemi == 0 ? (r + 1f) / ringsPerHemi : (float)r / ringsPerHemi;
                float lat = hemi == 0 ? -MathF.PI / 2 + t * MathF.PI / 2 : t * MathF.PI / 2;
                ringStarts.Add((uint)verts.Count);
                for (int a = 0; a < around; a++)
                {
                    float phi = a * MathF.Tau / around;
                    verts.Add(new CapsuleVertex
                    {
                        Normal = new Vector3(MathF.Cos(lat) * MathF.Cos(phi), MathF.Sin(lat), MathF.Cos(lat) * MathF.Sin(phi)),
                        Cap = cap,
                    });
                }
            }
        }
        uint top = (uint)verts.Count;
        verts.Add(new CapsuleVertex { Normal = Vector3.UnitY, Cap = 1 });

        for (int a = 0; a < around; a++)
        {
            uint a1 = (uint)((a + 1) % around);
            idx.Add(baseIndex); idx.Add(ringStarts[0] + a1); idx.Add(ringStarts[0] + (uint)a);
        }
        for (int r = 0; r + 1 < ringStarts.Count; r++)
            for (int a = 0; a < around; a++)
            {
                uint a1 = (uint)((a + 1) % around);
                uint p0 = ringStarts[r] + (uint)a, p1 = ringStarts[r] + a1;
                uint q0 = ringStarts[r + 1] + (uint)a, q1 = ringStarts[r + 1] + a1;
                idx.Add(p0); idx.Add(p1); idx.Add(q1);
                idx.Add(p0); idx.Add(q1); idx.Add(q0);
            }
        uint last = ringStarts[^1];
        for (int a = 0; a < around; a++)
        {
            uint a1 = (uint)((a + 1) % around);
            idx.Add(top); idx.Add(last + (uint)a); idx.Add(last + a1);
        }
    }
}

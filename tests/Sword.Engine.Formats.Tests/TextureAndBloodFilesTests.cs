using System.Numerics;
using Sword.Game.Data;

namespace Sword.Engine.Formats.Tests;

/// <summary>
/// <c>.swtex</c> и <c>.swblood</c>: запись и чтение — те же данные и байты; обрезка и испорченная длина —
/// <see cref="InvalidDataException"/>.
/// </summary>
public sealed class TextureAndBloodFilesTests
{
    /// <summary>Два слоя 8×8 BC4, 4 мипа: блоков 2×2, 1×1, 1×1, 1×1 по 8 Б.</summary>
    private static TextureData Texture()
    {
        int[] mipBytes = [32, 8, 8, 8];
        var subs = new byte[8][];
        for (int s = 0; s < 2; s++)
            for (int m = 0; m < 4; m++)
                subs[s * 4 + m] = [.. Enumerable.Range(0, mipBytes[m]).Select(i => (byte)(i * 7 + s * 31 + m))];
        return new TextureData
        {
            Format = TexturePixelFormat.Bc4, Width = 8, Height = 8, Mips = 4, Slices = 2, Subresources = subs,
            Layers = [new TextureLayer("disc", 1f), new TextureLayer("брызги", 2f)],
        };
    }

    private static WeaponBloodData Blood() => new()
    {
        Base = new Vector3(0, 0.1f, 0.2f), Tip = new Vector3(0, 0.1f, 1f),
        Zones =
        [
            new WeaponBloodZone { Name = "handle", Kind = BloodZoneKind.Cylinder, Origin = Vector3.Zero, AxisU = Vector3.UnitZ, AxisV = Vector3.UnitY,
                                  Length = 0.2f, Half = 0.015f, CellsU = 4, CellsV = 4 },
            new WeaponBloodZone { Name = "blade", Kind = BloodZoneKind.Plane, Origin = new Vector3(0, 0, 0.24f), AxisU = Vector3.UnitZ, AxisV = Vector3.UnitY,
                                  Length = 0.76f, Half = 0.025f, CellsU = 32, CellsV = 4, PrevZone = 0 },
        ],
        VertexZones = [0, 0, WeaponBloodData.NoZone, 1, 1, 1],
    };

    private static byte[] Save(Action<Stream> write)
    {
        using var s = new MemoryStream();
        write(s);
        return s.ToArray();
    }

    [Fact]
    public void Texture_RoundTrip()
    {
        var bytes = Save(Texture().Write);
        var back = TextureData.Read(new MemoryStream(bytes), "mem");
        Assert.Equal((TexturePixelFormat.Bc4, 8, 8, 4, 2), (back.Format, back.Width, back.Height, back.Mips, back.Slices));
        Assert.Equal(Texture().Layers, back.Layers);
        Assert.Equal(1, back.FindLayer("БРЫЗГИ"));
        Assert.Equal(Texture().Subresource(1, 2), back.Subresource(1, 2));
        Assert.Equal(bytes, Save(back.Write));
    }

    [Fact]
    public void Texture_EveryTruncationIsInvalidData()
    {
        var bytes = Save(Texture().Write);
        for (int n = 0; n < bytes.Length; n++)
            Assert.Throws<InvalidDataException>(() => TextureData.Read(new MemoryStream(bytes[..n]), $"обрезан до {n}"));
    }

    [Fact]
    public void Texture_WrongSubresourceLengthIsRejected()
    {
        var t = Texture();
        t.Subresources[3] = new byte[9];
        Assert.Throws<InvalidDataException>(() => t.Write(new MemoryStream()));
    }

    [Fact]
    public void Blood_RoundTrip()
    {
        var bytes = Save(Blood().Write);
        var back = WeaponBloodData.Read(new MemoryStream(bytes), "mem");
        Assert.Equal(Blood().Zones, back.Zones);
        Assert.Equal(Blood().VertexZones, back.VertexZones);
        Assert.Equal((Blood().Base, Blood().Tip), (back.Base, back.Tip));
        Assert.Equal(bytes, Save(back.Write));
    }

    [Fact]
    public void Blood_EveryTruncationIsInvalidData()
    {
        var bytes = Save(Blood().Write);
        for (int n = 0; n < bytes.Length; n++)
            Assert.Throws<InvalidDataException>(() => WeaponBloodData.Read(new MemoryStream(bytes[..n]), $"обрезан до {n}"));
    }

    [Fact]
    public void Blood_ZoneOutsideListIsRejected()
    {
        var bytes = Save(Blood().Write);
        bytes[^5] = 7;   // зона последней вершины — 7 при двух зонах
        Assert.Throws<InvalidDataException>(() => WeaponBloodData.Read(new MemoryStream(bytes), "mem"));
    }
}

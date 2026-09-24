using GlassesRemote.Server.Media;

namespace GlassesRemote.Server.Tests.Media;

public sealed class Nv12Tests
{
    private static byte[] Solid(int width, int height, byte r, byte g, byte b)
    {
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = b;
            bgra[i + 1] = g;
            bgra[i + 2] = r;
            bgra[i + 3] = 255;
        }
        return bgra;
    }

    [Theory]
    [InlineData(0, 0, 0, 16, 128, 128)]       // black: Y at the bottom of limited range
    [InlineData(255, 255, 255, 235, 128, 128)] // white: Y at the top of limited range
    [InlineData(255, 0, 0, 82, 90, 240)]      // red
    [InlineData(0, 0, 255, 41, 240, 110)]     // blue
    public void Converts_solid_colours_to_bt601_limited_range(byte r, byte g, byte b, int y, int u, int v)
    {
        const int w = 4, h = 4;
        var nv12 = new byte[Nv12.BufferSize(w, h)];
        Nv12.FromBgra(Solid(w, h, r, g, b), w, h, nv12);

        Assert.All(nv12[..(w * h)], value => Assert.InRange(value, y - 1, y + 1));
        Assert.InRange(nv12[w * h], u - 1, u + 1);
        Assert.InRange(nv12[w * h + 1], v - 1, v + 1);
    }

    [Fact]
    public void Buffer_size_is_one_and_a_half_bytes_per_pixel()
    {
        Assert.Equal(600 * 600 * 3 / 2, Nv12.BufferSize(600, 600));
    }

    [Fact]
    public void Rejects_odd_dimensions()
    {
        Assert.Throws<ArgumentException>(() => Nv12.FromBgra(new byte[3 * 3 * 4], 3, 3, new byte[100]));
    }
}

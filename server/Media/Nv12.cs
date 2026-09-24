namespace GlassesRemote.Server.Media;

/// <summary>
/// BGRA → NV12 (BT.601, limited range), the input format of the Windows H.264
/// encoder. NV12 is a full-resolution Y plane followed by one interleaved UV
/// plane at half resolution in both directions.
/// </summary>
public static class Nv12
{
    public static int BufferSize(int width, int height) => width * height + width * height / 2;

    public static void FromBgra(ReadOnlySpan<byte> bgra, int width, int height, Span<byte> nv12)
    {
        if (width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException("NV12 needs even dimensions");
        }
        if (bgra.Length < width * height * 4 || nv12.Length < BufferSize(width, height))
        {
            throw new ArgumentException("Buffer too small");
        }

        var yPlane = nv12[..(width * height)];
        var uvPlane = nv12[(width * height)..];

        for (var y = 0; y < height; y += 2)
        {
            for (var x = 0; x < width; x += 2)
            {
                int sumB = 0, sumG = 0, sumR = 0;

                for (var dy = 0; dy < 2; dy++)
                {
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var i = ((y + dy) * width + x + dx) * 4;
                        int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                        yPlane[(y + dy) * width + x + dx] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                        sumB += b;
                        sumG += g;
                        sumR += r;
                    }
                }

                int avgB = sumB >> 2, avgG = sumG >> 2, avgR = sumR >> 2;
                var uv = (y / 2) * width + x;
                uvPlane[uv] = (byte)(((-38 * avgR - 74 * avgG + 112 * avgB + 128) >> 8) + 128);
                uvPlane[uv + 1] = (byte)(((112 * avgR - 94 * avgG - 18 * avgB + 128) >> 8) + 128);
            }
        }
    }
}

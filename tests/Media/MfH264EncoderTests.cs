using GlassesRemote.Server.Media;

namespace GlassesRemote.Server.Tests.Media;

/// <summary>Runs the real Windows H.264 encoder; skipped on PCs without one.</summary>
public sealed class MfH264EncoderTests
{
    private static byte[] Frame(int width, int height, int shift)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                bgra[i] = (byte)(x + shift);
                bgra[i + 1] = (byte)(y * 2);
                bgra[i + 2] = (byte)((x ^ y) + shift);
                bgra[i + 3] = 255;
            }
        }
        return bgra;
    }

    private static List<int> NalTypes(byte[] annexB)
    {
        var types = new List<int>();
        for (var i = 0; i + 3 < annexB.Length; i++)
        {
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
            {
                types.Add(annexB[i + 3] & 0x1F);
                i += 3;
            }
        }
        return types;
    }

    [Fact]
    public void Encodes_600px_frames_to_annex_b_with_a_keyframe_first()
    {
        if (!MfH264Encoder.IsAvailable())
        {
            return; // No H.264 MFT on this machine (e.g. Windows N without the media pack).
        }

        using var encoder = new MfH264Encoder(targetKbps: 2000, framesPerSecond: 20);
        var outputs = new List<byte[]>();
        for (var i = 0; i < 10; i++)
        {
            if (encoder.Encode(Frame(600, 600, i * 3), 600, 600) is { } encoded)
            {
                outputs.Add(encoded);
            }
        }

        Assert.NotEmpty(outputs);
        var first = NalTypes(outputs[0]);
        Assert.Contains(7, first); // SPS
        Assert.Contains(8, first); // PPS
        Assert.Contains(5, first); // IDR slice
        Assert.True(outputs.Count >= 8, $"expected ~1 output per input in low-latency mode, got {outputs.Count}");
        Assert.True(outputs[^1].Length < outputs[0].Length, "delta frames should be smaller than the keyframe");
    }

    [Fact]
    public void Forced_keyframe_produces_an_idr_frame()
    {
        if (!MfH264Encoder.IsAvailable())
        {
            return;
        }

        using var encoder = new MfH264Encoder(targetKbps: 2000, framesPerSecond: 20);
        for (var i = 0; i < 5; i++)
        {
            encoder.Encode(Frame(600, 600, i), 600, 600);
        }

        encoder.ForceKeyFrame();
        byte[]? keyframe = null;
        for (var i = 0; i < 3 && keyframe is null; i++)
        {
            var output = encoder.Encode(Frame(600, 600, 50 + i), 600, 600);
            if (output is not null && NalTypes(output).Contains(5))
            {
                keyframe = output;
            }
        }

        Assert.NotNull(keyframe);
    }
}

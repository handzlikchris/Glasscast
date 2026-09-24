using System.Drawing;

namespace WebRtcSpike;

/// <summary>
/// Stamps the server clock into each video frame as a row of black/white blocks,
/// so the browser can read it back from the decoded pixels and measure
/// end-to-end latency (render on PC → decode in browser).
///
/// Layout: 24 data bits (server Unix time in ms, modulo 2^24, MSB first) followed
/// by a 4-bit checksum (sum of the six data nibbles, modulo 16). Blocks are large
/// so they survive lossy video compression. The decoder lives in wwwroot/spike.js
/// and must stay in sync with this layout.
/// </summary>
public static class TimestampBarcode
{
    public const int DataBits = 24;
    public const int ChecksumBits = 4;
    public const int TotalBits = DataBits + ChecksumBits;
    public const int BlockWidth = 21;
    public const int BlockHeight = 24;

    public static bool[] Encode(long unixMs)
    {
        var value = (int)(unixMs & ((1 << DataBits) - 1));
        var bits = new bool[TotalBits];

        for (var i = 0; i < DataBits; i++)
        {
            bits[i] = ((value >> (DataBits - 1 - i)) & 1) == 1;
        }

        var checksum = Checksum(value);
        for (var i = 0; i < ChecksumBits; i++)
        {
            bits[DataBits + i] = ((checksum >> (ChecksumBits - 1 - i)) & 1) == 1;
        }

        return bits;
    }

    public static int Checksum(int value)
    {
        var sum = 0;
        for (var shift = 0; shift < DataBits; shift += 4)
        {
            sum += (value >> shift) & 0xF;
        }
        return sum & 0xF;
    }

    public static void Draw(Graphics g, long unixMs)
    {
        var bits = Encode(unixMs);
        for (var i = 0; i < bits.Length; i++)
        {
            g.FillRectangle(bits[i] ? Brushes.White : Brushes.Black, i * BlockWidth, 0, BlockWidth, BlockHeight);
        }
    }
}

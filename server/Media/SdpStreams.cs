namespace GlassesRemote.Server.Media;

/// <summary>
/// Puts the audio and the video in media streams of their own (<c>a=msid</c>), so the glasses
/// never hold one back to line it up with the other.
///
/// A browser lip-syncs tracks of the same stream, from each stream's RTCP sender reports. The
/// video would be the one delayed (it arrives later than the sound), and SIPSorcery's video
/// sender reports don't follow our RTP clock (<see cref="SipsorceryMediaPeer.SendFrame"/> keeps
/// its own), so the delay could be anything. Video latency matters more than lip sync here: the
/// sound may lead the picture by the video's extra delay (~100 ms).
///
/// SIPSorcery writes no <c>a=msid</c> at all, which leaves the grouping to the browser.
/// </summary>
public static class SdpStreams
{
    public const string AudioStream = "pc-audio";
    public const string VideoStream = "pc-video";

    public static string Separate(string sdp)
    {
        var lines = sdp.Replace("\r\n", "\n").Split('\n').ToList();
        string? kind = null;
        var hasMsid = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                kind = line.StartsWith("m=audio", StringComparison.Ordinal) ? "audio"
                    : line.StartsWith("m=video", StringComparison.Ordinal) ? "video"
                    : null;
                hasMsid = SectionHasMsid(lines, i);
            }
            else if (kind is not null && !hasMsid && line.StartsWith("a=mid:", StringComparison.Ordinal))
            {
                var stream = kind == "audio" ? AudioStream : VideoStream;
                lines.Insert(i + 1, $"a=msid:{stream} {kind}");
                hasMsid = true;
                i++;
            }
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join("\r\n", lines) + "\r\n";
    }

    private static bool SectionHasMsid(List<string> lines, int mLine)
    {
        for (var i = mLine + 1; i < lines.Count && !lines[i].StartsWith("m=", StringComparison.Ordinal); i++)
        {
            if (lines[i].StartsWith("a=msid:", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}

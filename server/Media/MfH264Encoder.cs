using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace GlassesRemote.Server.Media;

/// <summary>
/// H.264 through the Windows Media Foundation software encoder (built into Windows,
/// nothing to install). Constrained Baseline, low-latency mode, CBR, no B-frames,
/// one output frame per input frame. Keyframes on request and every GOP.
///
/// Uses the synchronous software MFT; hardware encoders (NVENC/QSV via MF) are
/// asynchronous and can be added later behind the same interface.
/// </summary>
public sealed class MfH264Encoder : IFrameEncoder
{
    private const int MFT_ENUM_FLAG_SYNCMFT = 0x00000001;
    private const int MFT_ENUM_FLAG_LOCALMFT = 0x00000004;
    private const int MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    private const uint BaselineProfile = 66; // eAVEncH264VProfile_Base
    private const uint Progressive = 2;      // MFVideoInterlace_Progressive
    private const uint RateControlCbr = 0;   // eAVEncCommonRateControlMode_CBR

    private static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid BPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid ForceKeyFrameApi = new("398c1b98-8353-475a-9ef2-8f265d260345");

    private static readonly Lazy<bool> MediaFoundationStarted = new(() =>
    {
        MediaFactory.MFStartup(useLightVersion: true).CheckError();
        return true;
    });

    private readonly int _fps;
    private readonly int _kbps;
    private readonly int _gopFrames;
    private readonly long _frameDuration;
    private IMFTransform? _transform;
    private ICodecAPI? _codecApi;
    private OutputStreamInfo _outputInfo;
    private byte[] _nv12 = [];
    private int _width;
    private int _height;
    private long _frameIndex;
    private bool _keyFrameRequested = true;

    /// <param name="keyframeIntervalSeconds">The encoder's own keyframe interval (GOP), which also
    /// applies when the frame pump doesn't force one.</param>
    public MfH264Encoder(int targetKbps, int framesPerSecond, int keyframeIntervalSeconds = 2)
    {
        _kbps = Math.Max(200, targetKbps);
        _fps = Math.Clamp(framesPerSecond, 1, 60);
        _gopFrames = _fps * Math.Max(1, keyframeIntervalSeconds);
        _frameDuration = 10_000_000L / _fps; // 100 ns units
        _ = MediaFoundationStarted.Value;
    }

    public string Codec => "H264";

    /// <summary>Checks that an H.264 encoder MFT exists (it doesn't on Windows "N" editions without the media pack).</summary>
    public static bool IsAvailable()
    {
        try
        {
            _ = MediaFoundationStarted.Value;
            using var activates = EnumerateEncoders();
            return activates.Any(IsSynchronous);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void ForceKeyFrame() => _keyFrameRequested = true;

    public byte[]? Encode(byte[] bgra, int width, int height)
    {
        EnsureInitialised(width, height);
        Nv12.FromBgra(bgra, width, height, _nv12);

        if (_keyFrameRequested && _codecApi is not null)
        {
            object one = 1u;
            _codecApi.SetValue(ForceKeyFrameApi, ref one);
            _keyFrameRequested = false;
        }

        using (var input = MediaFactory.MFCreateSample())
        using (var buffer = MediaFactory.MFCreateMemoryBuffer(_nv12.Length))
        {
            buffer.Lock(out var pointer, out _, out _);
            Marshal.Copy(_nv12, 0, pointer, _nv12.Length);
            buffer.Unlock();
            buffer.CurrentLength = _nv12.Length;

            input.AddBuffer(buffer);
            input.SampleTime = _frameIndex * _frameDuration;
            input.SampleDuration = _frameDuration;
            _frameIndex++;

            _transform!.ProcessInput(0, input, 0);
        }

        return DrainOutput();
    }

    private byte[]? DrainOutput()
    {
        using var encoded = new MemoryStream();
        var bufferSize = Math.Max(_outputInfo.Size, _width * _height * 2);

        while (true)
        {
            using var sample = MediaFactory.MFCreateSample();
            using var buffer = MediaFactory.MFCreateMemoryBuffer(bufferSize);
            sample.AddBuffer(buffer);

            var output = new OutputDataBuffer { StreamID = 0, Sample = sample };
            var result = _transform!.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            output.Events?.Dispose();

            if (result.Code == MF_E_TRANSFORM_NEED_MORE_INPUT)
            {
                break;
            }
            if (result.Code == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                using var type = _transform.GetOutputAvailableType(0, 0);
                _transform.SetOutputType(0, type, 0);
                _outputInfo = _transform.GetOutputStreamInfo(0);
                continue;
            }
            result.CheckError();

            using var contiguous = sample.ConvertToContiguousBuffer();
            contiguous.Lock(out var pointer, out _, out var length);
            try
            {
                var bytes = new byte[length];
                Marshal.Copy(pointer, bytes, 0, length);
                encoded.Write(bytes);
            }
            finally
            {
                contiguous.Unlock();
            }
        }

        return encoded.Length > 0 ? encoded.ToArray() : null;
    }

    private void EnsureInitialised(int width, int height)
    {
        if (_transform is not null && width == _width && height == _height)
        {
            return;
        }

        Release();
        _width = width;
        _height = height;
        _nv12 = new byte[Nv12.BufferSize(width, height)];

        using (var activates = EnumerateEncoders())
        {
            var activate = activates.FirstOrDefault(IsSynchronous)
                           ?? throw new NotSupportedException("No synchronous H.264 encoder MFT available");
            _transform = activate.ActivateObject<IMFTransform>();
        }

        // Encoder settings must be in place before the media types are set; low-latency
        // mode applied afterwards is ignored and the encoder buffers frames.
        _transform.Attributes.Set(LowLatencyMode, 1u); // MF_LOW_LATENCY shares the CODECAPI GUID
        _codecApi = Marshal.GetObjectForIUnknown(_transform.NativePointer) as ICodecAPI;
        if (_codecApi is not null)
        {
            TrySet(LowLatencyMode, true);
            TrySet(RateControlMode, RateControlCbr);
            TrySet(MeanBitRate, (uint)(_kbps * 1000));
            TrySet(GopSize, (uint)_gopFrames);
            TrySet(BPictureCount, 0u);
        }

        using (var outputType = MediaFactory.MFCreateMediaType())
        {
            outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            outputType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)(_kbps * 1000));
            outputType.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)width, (uint)height));
            outputType.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)_fps, 1));
            outputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            outputType.Set(MediaTypeAttributeKeys.InterlaceMode, Progressive);
            outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, BaselineProfile);
            _transform.SetOutputType(0, outputType, 0);
        }

        using (var inputType = MediaFactory.MFCreateMediaType())
        {
            inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            inputType.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)width, (uint)height));
            inputType.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)_fps, 1));
            inputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            inputType.Set(MediaTypeAttributeKeys.InterlaceMode, Progressive);
            _transform.SetInputType(0, inputType, 0);
        }

        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        _outputInfo = _transform.GetOutputStreamInfo(0);
        _frameIndex = 0;
        _keyFrameRequested = true;
    }

    private void TrySet(Guid api, object value)
    {
        try
        {
            _codecApi!.SetValue(api, ref value);
        }
        catch (COMException)
        {
            // Not every setting is supported by every encoder; the defaults are acceptable.
        }
    }

    private static IMFActivateCollection EnumerateEncoders() =>
        MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_LOCALMFT | MFT_ENUM_FLAG_SORTANDFILTER,
            null,
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 });

    /// <summary>
    /// Hardware encoders (NVENC, QSV, AMF) come back from the enumeration too, even with the
    /// sync flag, but they are asynchronous and refuse synchronous use. Keep the sync ones.
    /// </summary>
    private static bool IsSynchronous(IMFActivate activate)
    {
        try
        {
            var flags = activate.GetUInt32(TransformAttributeKeys.TransformFlagsAttribute);
            return (flags & MFT_ENUM_FLAG_SYNCMFT) != 0;
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            // No flags attribute: software MFTs registered the classic way are synchronous.
            return true;
        }
    }

    private static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    private void Release()
    {
        if (_codecApi is not null)
        {
            Marshal.ReleaseComObject(_codecApi);
            _codecApi = null;
        }

        if (_transform is not null)
        {
            try
            {
                _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
                _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
            }
            _transform.Dispose();
            _transform = null;
        }
    }

    public void Dispose() => Release();

    /// <summary>ICodecAPI, declared by hand (not wrapped by Vortice). Only SetValue is used.</summary>
    [ComImport]
    [Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecAPI
    {
        [PreserveSig] int IsSupported([In] in Guid api);

        [PreserveSig] int IsModifiable([In] in Guid api);

        [PreserveSig] int GetParameterRange([In] in Guid api, out object valueMin, out object valueMax, out object steppingDelta);

        [PreserveSig] int GetParameterValues([In] in Guid api, out nint values, out uint valuesCount);

        [PreserveSig] int GetDefaultValue([In] in Guid api, out object value);

        [PreserveSig] int GetValue([In] in Guid api, out object value);

        void SetValue([In] in Guid api, [In, MarshalAs(UnmanagedType.Struct)] ref object value);
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using GlassesRemote.Server.Desktop;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GlassesRemote.Server.Windows;

/// <summary>
/// WASAPI loopback: what the default output device plays, every app mixed, as Windows sends it
/// to the speakers. Windows is asked to convert to 48 kHz stereo float itself (AUTOCONVERTPCM);
/// where it won't, the device's own mix format is taken and converted here
/// (<see cref="AudioConverter"/>). Polled by the audio pump; no thread of its own.
/// Must be created, read and disposed on one thread (COM).
/// </summary>
internal sealed class LoopbackAudioCapture : IAudioCapture
{
    /// <summary>Windows' buffer. The pump drains it every 5 ms, so this is only slack for a stall.</summary>
    private const long BufferHns = 100 * 10_000;

    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    private readonly MMDeviceEnumerator _devices;
    private readonly MMDevice _device;
    private readonly AudioClient _client;
    private readonly AudioCaptureClient _capture;
    private readonly string _deviceId;
    private readonly int _channels;
    private readonly bool _pcm16;
    private readonly AudioConverter? _converter;
    private float[] _samples = new float[4096];
    private long _checkedAt = Stopwatch.GetTimestamp();

    private LoopbackAudioCapture(MMDeviceEnumerator devices, MMDevice device, AudioClient client, WaveFormat format, bool converted)
    {
        _devices = devices;
        _device = device;
        _client = client;
        _deviceId = device.ID;
        _channels = format.Channels;
        _pcm16 = IsPcm16(format);
        _converter = converted ? null : new AudioConverter(format.SampleRate, format.Channels);
        _capture = client.AudioCaptureClient;
        client.Start();
    }

    public string DeviceName => _device.FriendlyName;

    /// <summary>Opens the default output device, or returns null when there is none.</summary>
    public static LoopbackAudioCapture? Start()
    {
        var devices = new MMDeviceEnumerator();
        MMDevice? device = null;
        AudioClient? client = null;
        try
        {
            if (!devices.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                devices.Dispose();
                return null;
            }
            device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            var wanted = WaveFormat.CreateIeeeFloatWaveFormat(AudioFormat48k.SampleRate, AudioFormat48k.Channels);
            client = device.AudioClient; // a new client on every read of the property
            try
            {
                client.Initialize(AudioClientShareMode.Shared,
                    AudioClientStreamFlags.Loopback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                    BufferHns, 0, wanted, Guid.Empty);
                return new LoopbackAudioCapture(devices, device, client, wanted, converted: true);
            }
            catch (COMException)
            {
                client.Dispose();
                client = device.AudioClient;
                var mix = client.MixFormat;
                if (!IsFloat(mix) && !IsPcm16(mix))
                {
                    throw new NotSupportedException($"Audio output format not supported: {mix}");
                }
                client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback, BufferHns, 0, mix, Guid.Empty);
                return new LoopbackAudioCapture(devices, device, client, mix, converted: false);
            }
        }
        catch
        {
            client?.Dispose();
            device?.Dispose();
            devices.Dispose();
            throw;
        }
    }

    public bool IsStale
    {
        get
        {
            // Asking Windows every 5 ms would be wasteful; once a second is plenty for a plug-in.
            if (Stopwatch.GetElapsedTime(_checkedAt) < TimeSpan.FromSeconds(1))
            {
                return false;
            }
            _checkedAt = Stopwatch.GetTimestamp();
            try
            {
                using var current = _devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return current.ID != _deviceId;
            }
            catch (COMException)
            {
                return true; // no default output any more
            }
        }
    }

    public void ReadInto(AudioFifo fifo)
    {
        while (_capture.GetNextPacketSize() > 0)
        {
            var data = _capture.GetBuffer(out var frames, out var flags);
            try
            {
                var count = frames * _channels;
                if (_samples.Length < count)
                {
                    _samples = new float[count];
                }
                var samples = _samples.AsSpan(0, count);
                if ((flags & AudioClientBufferFlags.Silent) != 0)
                {
                    samples.Clear();
                }
                else if (_pcm16)
                {
                    var pcm = new short[count];
                    Marshal.Copy(data, pcm, 0, count);
                    for (var i = 0; i < count; i++)
                    {
                        samples[i] = pcm[i] / 32768f;
                    }
                }
                else
                {
                    Marshal.Copy(data, _samples, 0, count);
                }

                if (_converter is null)
                {
                    fifo.Write(samples);
                }
                else
                {
                    _converter.Convert(samples, fifo);
                }
            }
            finally
            {
                _capture.ReleaseBuffer(frames);
            }
        }
    }

    private static bool IsFloat(WaveFormat format) => format.BitsPerSample == 32 && (format.Encoding == WaveFormatEncoding.IeeeFloat
        || (format is WaveFormatExtensible ext && ext.SubFormat == FloatSubFormat));

    private static bool IsPcm16(WaveFormat format) => format.BitsPerSample == 16 && (format.Encoding == WaveFormatEncoding.Pcm
        || (format is WaveFormatExtensible ext && ext.SubFormat == PcmSubFormat));

    public void Dispose()
    {
        try
        {
            _client.Stop();
        }
        catch (COMException)
        {
            // The device is gone already.
        }
        _capture.Dispose();
        _client.Dispose();
        _device.Dispose();
        _devices.Dispose();
    }
}

/// <summary>
/// Opens <see cref="ProcessLoopbackCapture"/> (every app, before the PC's volume) where Windows
/// has it, else <see cref="LoopbackAudioCapture"/> on the default output device (follows the PC's
/// volume and mute).
/// </summary>
public sealed class LoopbackAudioCaptureFactory(ILogger<LoopbackAudioCaptureFactory> logger) : IAudioCaptureFactory
{
    private bool _processLoopbackFailed;

    public IAudioCapture? Start()
    {
        if (ProcessLoopbackCapture.IsSupported && !_processLoopbackFailed)
        {
            try
            {
                var capture = ProcessLoopbackCapture.Start();
                logger.LogInformation("Capturing every app's sound (process loopback: the PC's volume doesn't matter)");
                return capture;
            }
            catch (Exception ex)
            {
                _processLoopbackFailed = true;
                logger.LogWarning(ex, "Process loopback unavailable; capturing the default output device (follows the PC's volume)");
            }
        }

        try
        {
            var capture = LoopbackAudioCapture.Start();
            if (capture is null)
            {
                logger.LogWarning("No audio output device: the glasses get no sound");
            }
            else
            {
                logger.LogInformation("Capturing sound from {Device}", capture.DeviceName);
            }
            return capture;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't capture the PC's sound");
            return null;
        }
    }
}

using System.Runtime.InteropServices;
using GlassesRemote.Server.Desktop;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace GlassesRemote.Server.Windows;

/// <summary>
/// Process loopback (Windows 10 2004+): the sound of every app but this one, taken before the
/// PC's volume and mute are applied, whatever output device each app plays on. So the glasses
/// get the sound at full level however low (or muted) the PC's speakers are, and the user sets
/// the loudness on the glasses. Endpoint loopback (<see cref="LoopbackAudioCapture"/>) records
/// after the master volume, which made the glasses follow the PC's volume slider.
///
/// Activated through the virtual "VAD\Process_Loopback" device; NAudio does this internally but
/// doesn't expose it. Windows converts to 48 kHz stereo float (the format is ours to choose:
/// there's no mix format). Polled like the endpoint capture; must stay on one thread.
/// </summary>
internal sealed partial class ProcessLoopbackCapture : IAudioCapture
{
    private const string VirtualDevice = @"VAD\Process_Loopback";
    private const long BufferHns = 100 * 10_000;
    private const int ActivationTypeProcessLoopback = 1;
    private const int IncludeTargetProcessTree = 0;
    private const int ExcludeTargetProcessTree = 1;
    private const ushort VtBlob = 65;

    private static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    private readonly AudioClient _client;
    private readonly AudioCaptureClient _capture;
    private readonly EventWaitHandle _packetReady = new(false, EventResetMode.AutoReset);
    private float[] _samples = new float[4096];

    private ProcessLoopbackCapture(AudioClient client)
    {
        _client = client;
        var format = WaveFormat.CreateIeeeFloatWaveFormat(AudioFormat48k.SampleRate, AudioFormat48k.Channels);
        // Process loopback wants event-driven initialisation; the pump still polls, the event is unused.
        client.Initialize(AudioClientShareMode.Shared,
            AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback
            | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
            BufferHns, 0, format, Guid.Empty);
        client.SetEventHandle(_packetReady.SafeWaitHandle.DangerousGetHandle());
        _capture = client.AudioCaptureClient;
        client.Start();
    }

    /// <summary>Windows 10 2004 (build 19041) and later have process loopback.</summary>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    /// <summary>
    /// Every app's sound except this process's (<paramref name="includeOnly"/>: only this
    /// process tree's, for tests).
    /// </summary>
    public static ProcessLoopbackCapture Start(bool includeOnly = false)
    {
        var parameters = Marshal.AllocHGlobal(12);
        var variant = Marshal.AllocHGlobal(24);
        try
        {
            // AUDIOCLIENT_ACTIVATION_PARAMS { type; { TargetProcessId; ProcessLoopbackMode } }
            Marshal.WriteInt32(parameters, 0, ActivationTypeProcessLoopback);
            Marshal.WriteInt32(parameters, 4, Environment.ProcessId);
            Marshal.WriteInt32(parameters, 8, includeOnly ? IncludeTargetProcessTree : ExcludeTargetProcessTree);
            // PROPVARIANT { vt = VT_BLOB; 3 reserved words; BLOB { cbSize; pBlobData } }
            for (var i = 0; i < 24; i += 4)
            {
                Marshal.WriteInt32(variant, i, 0);
            }
            Marshal.WriteInt16(variant, 0, (short)VtBlob);
            Marshal.WriteInt32(variant, 8, 12);
            Marshal.WriteIntPtr(variant, 8 + IntPtr.Size, parameters);

            var handler = new CompletionHandler();
            ActivateAudioInterfaceAsync(VirtualDevice, AudioClientId, variant, handler, out _);
            if (!handler.Done.Wait(TimeSpan.FromSeconds(3)))
            {
                throw new TimeoutException("Process loopback activation didn't complete");
            }
            Marshal.ThrowExceptionForHR(handler.Result);
            return new ProcessLoopbackCapture(new AudioClient((IAudioClient)handler.Client!));
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(parameters);
        }
    }

    /// <summary>Not tied to an output device, so a new default device changes nothing.</summary>
    public bool IsStale => false;

    public void ReadInto(AudioFifo fifo)
    {
        while (_capture.GetNextPacketSize() > 0)
        {
            var data = _capture.GetBuffer(out var frames, out var flags);
            try
            {
                var count = frames * AudioFormat48k.Channels;
                if (_samples.Length < count)
                {
                    _samples = new float[count];
                }
                if ((flags & AudioClientBufferFlags.Silent) != 0)
                {
                    Array.Clear(_samples, 0, count);
                }
                else
                {
                    Marshal.Copy(data, _samples, 0, count);
                }
                fifo.Write(_samples.AsSpan(0, count));
            }
            finally
            {
                _capture.ReleaseBuffer(frames);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _client.Stop();
        }
        catch (COMException)
        {
        }
        _capture.Dispose();
        _client.Dispose();
        _packetReady.Dispose();
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
#pragma warning disable SYSLIB1054 // COM interface parameters: LibraryImport can't marshal these
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);
#pragma warning restore SYSLIB1054

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    /// <summary>The completion handler must be agile: Windows calls it on a worker thread.</summary>
    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAgileObject
    {
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public ManualResetEventSlim Done { get; } = new();

        public int Result { get; private set; }

        public object? Client { get; private set; }

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            activateOperation.GetActivateResult(out var result, out var client);
            Result = result;
            Client = client;
            Done.Set();
        }
    }
}

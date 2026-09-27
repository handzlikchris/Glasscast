using System.Runtime.InteropServices;

namespace GlassesRemote.Server.Windows;

/// <summary>
/// Sleeps with sub-millisecond precision on one thread. <c>Thread.Sleep</c> and
/// <c>Task.Delay</c> wake on the ~15.6 ms system tick, far too coarse to space packets a
/// millisecond or two apart; a high-resolution waitable timer (Windows 10 1803+) isn't.
/// Falls back to <c>Thread.Sleep</c> where the timer can't be created.
/// </summary>
internal sealed partial class PreciseSleep : IDisposable
{
    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x001F0003;
    private const uint INFINITE = 0xFFFFFFFF;

    private readonly nint _timer;

    public PreciseSleep()
    {
        _timer = CreateWaitableTimerExW(0, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    }

    public void Sleep(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        // Relative due time: negative, in 100 ns units.
        var due = -duration.Ticks;
        if (_timer != 0 && SetWaitableTimerEx(_timer, ref due, 0, 0, 0, 0, 0))
        {
            WaitForSingleObject(_timer, INFINITE);
        }
        else
        {
            Thread.Sleep(duration);
        }
    }

    public void Dispose()
    {
        if (_timer != 0)
        {
            CloseHandle(_timer);
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimerEx(nint timer, ref long dueTime, int period, nint completion,
        nint completionArg, nint wakeContext, uint tolerableDelay);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

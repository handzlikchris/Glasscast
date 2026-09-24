using System.Runtime.InteropServices;
using GlassesRemote.Server.Desktop;
using GlassesRemote.Server.Protocol;
using static GlassesRemote.Server.Windows.NativeMethods;

namespace GlassesRemote.Server.Windows;

/// <summary>
/// Mouse and keyboard through SendInput, as the ordinary logged-in user.
/// Windows' UIPI blocks input to elevated windows; that is accepted by design.
/// </summary>
public sealed class Win32InputInjector : IInputInjector
{
    private const ushort VK_BACK = 0x08;
    private const ushort VK_TAB = 0x09;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_ESCAPE = 0x1B;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_RIGHT = 0x27;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_C = 0x43;
    private const ushort VK_V = 0x56;

    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    private readonly ILogger<Win32InputInjector> _logger;

    public Win32InputInjector(ILogger<Win32InputInjector> logger)
    {
        _logger = logger;
    }

    public void MoveTo(int x, int y)
    {
        // Absolute coordinates are normalised to 0..65535 across the whole virtual desktop.
        // The primary monitor's top-left is (0,0) in virtual-desktop coordinates.
        var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        var height = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));

        var nx = (int)Math.Round((x - left) * 65535.0 / (width - 1));
        var ny = (int)Math.Round((y - top) * 65535.0 / (height - 1));
        Send(Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny));
    }

    public void Click(MouseButton button) =>
        Send(Mouse(MOUSEEVENTF_LEFTDOWN), Mouse(MOUSEEVENTF_LEFTUP));

    public void Wheel(int delta) =>
        Send(Mouse(MOUSEEVENTF_WHEEL, data: unchecked((uint)delta)));

    public void TypeText(string text)
    {
        // KEYEVENTF_UNICODE sends each UTF-16 unit as-is, independent of the keyboard layout.
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var c in text)
        {
            inputs.Add(Key(0, c, KEYEVENTF_UNICODE));
            inputs.Add(Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }

        foreach (var chunk in inputs.Chunk(200))
        {
            Send(chunk);
        }
    }

    public void Press(KeyCommand key)
    {
        switch (key)
        {
            case KeyCommand.Enter: Tap(VK_RETURN); break;
            case KeyCommand.Escape: Tap(VK_ESCAPE); break;
            case KeyCommand.Tab: Tap(VK_TAB); break;
            case KeyCommand.Backspace: Tap(VK_BACK); break;
            case KeyCommand.CtrlC: Chord(VK_C, VK_CONTROL); break;
            case KeyCommand.CtrlV: Chord(VK_V, VK_CONTROL); break;
            case KeyCommand.AltTab: Chord(VK_TAB, VK_MENU); break;
            case KeyCommand.WinShiftLeft: Chord(VK_LEFT, VK_LWIN, VK_SHIFT); break;
            case KeyCommand.WinShiftRight: Chord(VK_RIGHT, VK_LWIN, VK_SHIFT); break;
            default: throw new ArgumentOutOfRangeException(nameof(key), key, null);
        }
    }

    private void Tap(ushort vk) => Send(Key(vk, 0, Flags(vk)), Key(vk, 0, Flags(vk) | KEYEVENTF_KEYUP));

    private void Chord(ushort vk, params ushort[] modifiers)
    {
        var inputs = new List<INPUT>();
        inputs.AddRange(modifiers.Select(m => Key(m, 0, Flags(m))));
        inputs.Add(Key(vk, 0, Flags(vk)));
        inputs.Add(Key(vk, 0, Flags(vk) | KEYEVENTF_KEYUP));
        inputs.AddRange(modifiers.Reverse().Select(m => Key(m, 0, Flags(m) | KEYEVENTF_KEYUP)));
        Send(inputs.ToArray());
    }

    private static uint Flags(ushort vk) => vk is VK_LEFT or VK_RIGHT or VK_LWIN ? KEYEVENTF_EXTENDEDKEY : 0;

    private void Send(params INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, InputSize);
        if (sent != inputs.Length)
        {
            // Typically: the target window is elevated (UIPI), or the secure desktop is showing.
            _logger.LogWarning("SendInput injected {Sent} of {Total} events (error {Error})",
                sent, inputs.Length, Marshal.GetLastPInvokeError());
        }
    }

    private static INPUT Mouse(uint flags, int dx = 0, int dy = 0, uint data = 0) => new()
    {
        type = INPUT_MOUSE,
        u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } },
    };

    private static INPUT Key(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };
}

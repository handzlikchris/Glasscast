using GlassesRemote.Server.Desktop;
using GlassesRemote.Server.Protocol;

namespace GlassesRemote.Server.Tests.Fakes;

/// <summary>Records injected input instead of touching the real desktop.</summary>
public sealed class FakeInput : IInputInjector
{
    public List<string> Actions { get; } = new();

    public void MoveTo(int x, int y) => Record($"move {x},{y}");

    public void Click(MouseButton button) => Record($"click {button}");

    public void Wheel(int delta) => Record($"wheel {delta}");

    public void TypeText(string text) => Record($"type {text}");

    public void Press(KeyCommand key) => Record($"key {key}");

    private void Record(string action)
    {
        lock (Actions)
        {
            Actions.Add(action);
        }
    }
}

public sealed class FakeScreen(int width = 2560, int height = 1440) : IScreen
{
    public PixelSize PrimarySize { get; } = new(width, height);
}

public sealed class FakeKeepAwake : IKeepAwake
{
    public int Active { get; private set; }

    public IDisposable Acquire()
    {
        Active++;
        return new Release(this);
    }

    private sealed class Release(FakeKeepAwake owner) : IDisposable
    {
        public void Dispose() => owner.Active--;
    }
}

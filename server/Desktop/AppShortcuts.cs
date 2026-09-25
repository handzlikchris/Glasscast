namespace GlassesRemote.Server.Desktop;

/// <summary>
/// An app the glasses can switch to with one button. Configured only on the PC
/// (appsettings.Local.json, section "Apps"): the glasses see the names and send a slot
/// number, never a process name or a path. Only windows that are already open are used;
/// nothing is ever launched.
/// </summary>
public sealed class AppShortcut
{
    /// <summary>Short label, shown on the glasses (at most <see cref="AppShortcutOptions.MaxNameLength"/> characters).</summary>
    public string Name { get; set; } = "";

    /// <summary>Process name without ".exe" (e.g. "chrome"), case-insensitive. Optional if <see cref="Title"/> is set.</summary>
    public string? Process { get; set; }

    /// <summary>Text the window title must contain, case-insensitive. Optional if <see cref="Process"/> is set.</summary>
    public string? Title { get; set; }

    public bool IsValid =>
        Name.Trim().Length is > 0 and <= AppShortcutOptions.MaxNameLength
        && (!string.IsNullOrWhiteSpace(Process) || !string.IsNullOrWhiteSpace(Title));
}

public sealed class AppShortcutOptions
{
    public const string SectionName = "Apps";
    public const int MaxShortcuts = 9;
    public const int MaxNameLength = 16;

    public List<AppShortcut> Shortcuts { get; set; } = [];

    /// <summary>The usable shortcuts, in order; slot N (1-based) is element N-1.</summary>
    public IReadOnlyList<AppShortcut> Usable => Shortcuts.Where(s => s.IsValid).Take(MaxShortcuts).ToList();
}

public enum AppSwitchResult
{
    Switched,
    NotRunning,
    Failed,
}

/// <summary>Brings an app's most recent window to the front and fits it to an area of the primary monitor.</summary>
public interface IWindowSwitcher
{
    AppSwitchResult Switch(AppShortcut app, CaptureRegion area);
}

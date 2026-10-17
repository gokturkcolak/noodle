using Microsoft.Win32;

namespace Noodle;

/// "Are you on a call right now?" from the mic and camera. Windows keeps a per-app record of who is
/// using them (the same one behind the mic/camera icon in the taskbar): an app whose LastUsedTimeStop
/// is 0 is using it right now. Reading it needs no permission and never touches the devices.
static class Calls
{
    const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    public static bool InProgress() => InUse("microphone") || InUse("webcam");

    public static bool InUse(string capability)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey($@"{Store}\{capability}");
            if (root == null) return false;
            if (AnyRunning(root)) return true;   // Store apps (Teams, Camera…)
            using var desktop = root.OpenSubKey("NonPackaged");   // everything else (Zoom, Chrome…)
            return desktop != null && AnyRunning(desktop);
        }
        catch (Exception e)
        {
            Log.Debug($"{capability} check failed: {e.Message}");
            return false;
        }
    }

    static bool AnyRunning(RegistryKey parent)
    {
        foreach (var name in parent.GetSubKeyNames())
        {
            if (name == "NonPackaged") continue;
            using var app = parent.OpenSubKey(name);
            if (app?.GetValue("LastUsedTimeStart") is long start && start != 0 &&
                app.GetValue("LastUsedTimeStop") is long stop && stop == 0) return true;
        }
        return false;
    }
}

/// Open at login, via the per-user Run key so it shows up under Settings > Apps > Startup.
static class LoginItem
{
    const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Noodle";

    static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsOn
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Run);
            return key?.GetValue(Name) is string v && v.Equals(Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Run);
            if (on) key.SetValue(Name, Command); else key.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch (Exception e) { Log.Debug($"login item {(on ? "on" : "off")} failed: {e.Message}"); }
    }
}

using System.Diagnostics;
using System.Reflection;

namespace Noodle;

/// `Noodle.exe setup` / `remove` / `version`: what scripts/noodle does on the Mac, with the same rules as
/// the in-app Connect step. Run them through noodle.cmd so the terminal waits for the answers.
static class Cli
{
    /// SKIP_APP=1 edits settings.json only: no launching, quitting, or login item. For tests.
    static bool SkipApp => Environment.GetEnvironmentVariable("SKIP_APP") == "1";

    public static int Setup(bool yes)
    {
        Program.UseConsole();
        var hooks = new ClaudeHooks();
        if (!ApplySettings(hooks, connect: true, yes)) return 1;
        if (SkipApp) return 0;
        if (!Program.IsRunning()) Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
        if (!LoginItem.IsOn && Ask("Open Noodle at login?", yes))
        {
            LoginItem.Set(true);
            Console.WriteLine("Added login item. Toggle it anytime under Settings in the tray menu.");
        }
        Console.WriteLine("Running. Look for the little noodle in your system tray.");
        return 0;
    }

    public static int Remove(bool yes)
    {
        Program.UseConsole();
        if (!SkipApp) LoginItem.Set(false);
        var hooks = new ClaudeHooks();
        if (File.Exists(hooks.FilePath) && !ApplySettings(hooks, connect: false, yes)) return 1;
        if (!SkipApp) Program.Notify("quit");
        Console.WriteLine("Hooks removed. Your config and log are still in ~/.claude-posture.");
        return 0;
    }

    public static int Version()
    {
        Program.UseConsole();
        var v = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        Console.WriteLine(v.Split('+')[0]);
        return 0;
    }

    public static int Help()
    {
        Program.UseConsole();
        Console.WriteLine("""
            noodle setup [-y]    connect Noodle to Claude Code: add the two hooks, open the app
            noodle remove [-y]   disconnect: remove the hooks and the login item, quit the app
            noodle version
            """);
        return 0;
    }

    /// Shows the diff, asks, backs up, writes.
    static bool ApplySettings(ClaudeHooks hooks, bool connect, bool yes)
    {
        ClaudeHooks.Plan plan;
        try { plan = hooks.MakePlan(connect); }
        catch (Exception e) { Console.WriteLine(e.Message); return false; }
        if (!plan.Changed)
        {
            Console.WriteLine($"settings.json: nothing to {(connect ? "add" : "remove")}.");
            return true;
        }
        Console.WriteLine($"Changes to {hooks.FilePath}:");
        foreach (var row in plan.Diff())
        {
            Console.ForegroundColor = row[0] switch { "+" => ConsoleColor.Green, "-" => ConsoleColor.Red, _ => ConsoleColor.Gray };
            Console.WriteLine(row[0] == "…" ? "  …" : $"{row[0]} {row[1]}");
        }
        Console.ResetColor();
        if (!Ask("Write these changes?", yes)) { Console.WriteLine("Skipped settings.json."); return true; }
        try
        {
            if (hooks.Apply(plan) is string backup) Console.WriteLine($"Backed up to {backup}");
            Console.WriteLine($"Updated {hooks.FilePath}");
            return true;
        }
        catch (Exception e) { Console.WriteLine($"Couldn't write {hooks.FilePath}: {e.Message}"); return false; }
    }

    static bool Ask(string question, bool yes)
    {
        if (yes) return true;
        Console.Write($"{question} [y/N] ");
        var ans = Console.ReadLine() ?? "";
        return ans.StartsWith('y') || ans.StartsWith('Y');
    }
}

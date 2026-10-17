using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace Noodle;

/// One Noodle per user session. Running Noodle.exe again hands its event to the one that's up, the way
/// `open -g claudeposture://prompt` reaches the running app on the Mac:
///   Noodle.exe                    open Noodle (or bring up the Noodle window if it's running)
///   Noodle.exe --event <name>     prompt | stop | show | intro | home | film-<id> | quit (what the hooks run)
///   Noodle.exe setup [-y]         connect Noodle to Claude Code: add the two hooks, open the app
///   Noodle.exe remove [-y]        disconnect: remove the hooks and the login item, quit the app
///   Noodle.exe version
static partial class Program
{
    static readonly string Id = $"Noodle.ClaudePosture.{Environment.UserName}.{Process.GetCurrentProcess().SessionId}";

    [STAThread]
    static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        switch (cmd)
        {
            case "setup": return Cli.Setup(args.Contains("-y"));
            case "remove": return Cli.Remove(args.Contains("-y"));
            case "version" or "--version": return Cli.Version();
            case "help" or "-h" or "--help": return Cli.Help();
        }
        string ev = cmd == "--event" && args.Length > 1 ? args[1] : "home";

        using var mutex = new Mutex(true, $@"Local\{Id}", out bool first);
        if (!first) return Send(ev) ? 0 : 1;
        if (ev == "quit") return 0;   // nothing running to quit

        try { CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show("Noodle needs the Microsoft Edge WebView2 Runtime, which comes with Windows 11. "
                + "Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and open Noodle again.", "Noodle");
            return 1;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var ui = SynchronizationContext.Current!;

        var app = new TrayApp(ResourcesDir());
        Listen(name => ui.Post(_ =>
        {
            if (name == "quit") app.ExitThread(); else app.HandleEvent(name);
        }, null));
        // Launched by a hook or a link: handle what launched us, like applicationWillFinishLaunching does.
        // A plain launch doesn't open the window; TrayApp decides whether there's a first-run step to show.
        if (cmd == "--event") ui.Post(_ => app.HandleEvent(ev), null);
        Application.Run(app);
        return 0;
    }

    /// Resources next to the exe once built or installed, else app/Resources in the repo.
    static string ResourcesDir()
    {
        var here = Path.Combine(AppContext.BaseDirectory, "Resources");
        if (File.Exists(Path.Combine(here, "card.html"))) return here;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "app", "Resources", "card.html"))) dir = dir.Parent;
        return dir != null ? Path.Combine(dir.FullName, "app", "Resources") : here;
    }

    /// Hands `ev` to the running Noodle. It may still be starting up, so keep trying for a few seconds.
    static bool Send(string ev)
    {
        var until = DateTime.Now.AddSeconds(5);
        while (DateTime.Now < until)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", Id, PipeDirection.Out);
                pipe.Connect(500);
                using var w = new StreamWriter(pipe);
                w.WriteLine(ev);
                return true;
            }
            catch (Exception e) when (e is TimeoutException or IOException) { Thread.Sleep(100); }
        }
        return false;
    }

    /// Reads one event per connection, forever, on a background thread.
    static void Listen(Action<string> handle) => new Thread(() =>
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte);
                pipe.WaitForConnection();
                using var r = new StreamReader(pipe);
                if (r.ReadLine()?.Trim() is { Length: > 0 } line) handle(line);
            }
            catch (Exception e) { Log.Debug($"event pipe: {e.Message}"); Thread.Sleep(200); }
        }
    }) { IsBackground = true, Name = "events" }.Start();

    public static bool IsRunning()
    {
        using var m = new Mutex(false, $@"Local\{Id}", out bool created);
        return !created;
    }

    public static bool Notify(string ev) => IsRunning() && Send(ev);

    // MARK: Console, for the CLI commands of a windowed exe

    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int pid);

    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetStdHandle(int which);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetFileType(IntPtr handle);

    /// Borrows the terminal Noodle was run from (or opens one), so setup and remove can print and ask.
    /// Output that's piped or redirected (scripts, tests) already has somewhere to go.
    public static void UseConsole()
    {
        if (GetFileType(GetStdHandle(-11 /* STD_OUTPUT_HANDLE */)) != 0) return;
        if (!AttachConsole(-1)) AllocConsole();
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetError(stdout);
        Console.SetIn(new StreamReader(Console.OpenStandardInput()));
        Console.WriteLine();
    }
}

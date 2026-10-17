using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace Noodle;

/// The tray icon and everything hanging off it. The Windows twin of AppDelegate.swift.
sealed class TrayApp : ApplicationContext
{
    readonly Store store;
    readonly Posture posture;
    readonly HomeWindow home;
    readonly NotifyIcon tray;
    readonly ContextMenuStrip menu = new();
    readonly ClaudeHooks hooks = new();
    readonly Icon icon, pausedIcon;
    readonly System.Windows.Forms.Timer minute = new() { Interval = 60_000 };
    ClaudeHooks.Plan? hooksPlan;   // what the open sheet previewed, written as-is on confirm
    string? sheetMode;             // "connect", "disconnect", or "done" while the sheet is up

    public TrayApp(string resources)
    {
        var dir = Environment.GetEnvironmentVariable("CLAUDE_POSTURE_HOME") is { Length: > 0 } d ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude-posture");
        store = new Store(dir, resources);
        posture = new Posture(store, new CardController(resources));
        posture.OnChange += Refresh;
        home = new HomeWindow(resources);
        home.OnAction += (action, value) =>
        {
            switch (action)
            {
                case "character": if (value != null) Update(c => c.Character = value); break;
                case "stretch": posture.ShowNow(); break;
                case "intro": posture.ShowWelcome(); break;
                case "connect" or "disconnect": ShowHooksSheet(action == "connect"); break;
                case "apply": ApplyHooks(value == "login"); break;
                case "timer": UseTimer(fromSheet: true); break;
                case "sheet-dismissed" or "closed": SheetClosed(); break;
            }
        };
        ApplyFullScreen();

        var iconPath = Path.Combine(AppContext.BaseDirectory, "noodle.ico");
        icon = File.Exists(iconPath) ? new Icon(iconPath, SystemInformation.SmallIconSize) : SystemIcons.Application;
        pausedIcon = Dimmed(icon);
        tray = new NotifyIcon { Icon = icon, ContextMenuStrip = menu, Visible = true, Text = "Noodle" };
        tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) OpenHome(); };
        menu.Opening += (_, _) => BuildMenu();
        Refresh();

        // Not hooked up to Claude Code yet (a fresh download): open the Noodle window on the Connect step,
        // unless you've said "Not now" before. First launch, Toni says hi, after that step if it's showing.
        // Waits a beat so a launching event is handled first.
        bool needsConnect = NeedsHooks && !Prefs.Bool("connectDismissed");
        if (needsConnect || store.State().Onboarded != true)
            Later(1.5, () => { if (needsConnect) OpenConnect(activate: false); else posture.ShowWelcome(); });

        // Keeps the done count honest across midnight and the icon in sync when a pause expires.
        minute.Tick += (_, _) => Refresh();
        minute.Start();
        SystemEvents.UserPreferenceChanged += (_, _) => Refresh();
    }

    /// Events from the hooks (`Noodle.exe --event prompt`) and from opening Noodle again.
    public void HandleEvent(string name)
    {
        name = name.Trim('/').ToLowerInvariant();
        if (name is "" or "home") { OpenHome(); return; }
        if (name.StartsWith("film-")) { posture.Film(name[5..], FilmOptions); return; }   // film-doorway
        posture.Handle(name);
    }

    static async void Later(double seconds, Action run)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        run();
    }

    // MARK: Tray

    DateTime? PausedUntil => store.State().PausedUntil is DateTime p && p > DateTime.Now ? p : null;

    void Refresh()
    {
        int done = store.DoneToday();
        tray.Icon = PausedUntil == null ? icon : pausedIcon;
        tray.Text = PausedUntil == null ? $"Noodle: {done} stretch{(done == 1 ? "" : "es")} today" : "Noodle: paused";
        home.UpdateState(HomeState());
    }

    static Icon Dimmed(Icon source)
    {
        using var bmp = source.ToBitmap();
        using var faded = new Bitmap(bmp.Width, bmp.Height);
        using (var g = Graphics.FromImage(faded))
        {
            var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.45f };
            using var attrs = new System.Drawing.Imaging.ImageAttributes();
            attrs.SetColorMatrix(cm);
            g.DrawImage(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, attrs);
        }
        return Icon.FromHandle(faded.GetHicon());
    }

    // MARK: Noodle window

    void OpenHome() => home.ShowWith(HomeState());

    static string Time(DateTime t) => t.ToString("t", CultureInfo.CurrentCulture);

    Dictionary<string, object?> HomeState()
    {
        var c = store.Config();
        bool needsConnect = NeedsHooks;
        var status = ("Watching for long prompts", "on");
        if (needsConnect) status = ("Not connected to Claude Code", "off");
        else if (PausedUntil is DateTime until)
            status = ($"Paused until {Time(until)}{(until.Date == DateTime.Today ? "" : " tomorrow")}", "paused");
        else if (c.QuietHours.Contains(DateTime.Now)) status = ("Quiet hours", "paused");
        else if (posture.BlockedReason() == "on a call") status = ("Holding cards while you're on a call", "hold");
        else if (posture.TimerDue() is DateTime due)
            status = ($"On a timer, next stretch around {Time(due > DateTime.Now ? due : DateTime.Now)}", "on");
        return new()
        {
            ["cast"] = CastInfo.All.Select(x => new Dictionary<string, string>
                { ["id"] = x.Raw(), ["name"] = x.Name(), ["title"] = $"the {x}" }).ToList(),
            ["character"] = c.Character, ["doneToday"] = store.DoneToday(), ["status"] = status.Item1, ["statusKind"] = status.Item2,
            ["needsConnect"] = needsConnect, ["mode"] = c.Mode,
        };
    }

    void BuildMenu()
    {
        Refresh();
        var items = menu.Items;
        items.Clear();
        items.Add(Item("Open Noodle", OpenHome, bold: true));
        if (NeedsHooks) items.Add(Item("Connect to Claude Code…", OpenConnect));
        items.Add(new ToolStripSeparator());
        if (PausedUntil is DateTime until)
        {
            items.Add(Info($"Paused until {Time(until)}{(until.Date == DateTime.Today ? "" : " tomorrow")}"));
            items.Add(Item("Resume", () => posture.Pause(null)));
            items.Add(new ToolStripSeparator());
        }
        else if (store.Config().QuietHours.Contains(DateTime.Now))
        {
            items.Add(Info("Quiet hours"));
            items.Add(new ToolStripSeparator());
        }
        else if (posture.BlockedReason() == "on a call")
        {
            items.Add(Info("Holding cards: you're on a call"));
            items.Add(new ToolStripSeparator());
        }
        else if (posture.TimerDue() is DateTime due)
        {
            items.Add(Info($"Next stretch around {Time(due > DateTime.Now ? due : DateTime.Now)}"));
            items.Add(new ToolStripSeparator());
        }
        items.Add(Item("Pause for 1 hour", () => posture.Pause(DateTime.Now.AddHours(1))));
        items.Add(Item("Pause until tomorrow", () => posture.Pause(DateTime.Today.AddDays(1))));
        items.Add(new ToolStripSeparator());
        items.Add(Item("Show one now", posture.ShowNow));
        items.Add(Item($"Say hi to {CastInfo.From(store.Config().Character).Name()}", posture.ShowWelcome));
        items.Add(new ToolStripSeparator());
        items.Add(SettingsMenu());
        items.Add(FilmMenu());
        items.Add(new ToolStripSeparator());
        items.Add(Item("Quit", Quit));
    }

    // MARK: Settings submenu. Every change writes config.json and applies on the next card.

    ToolStripMenuItem SettingsMenu()
    {
        var c = store.Config();
        var focusItems = FocusInfo.All.Select(f =>
            (ToolStripItem)Option(f.Title(), c.Focus == f.Raw(), () => { store.Apply(f); Refresh(); })).ToList();
        if (FocusInfo.From(c.Focus) == null)
        {
            var custom = Info("Custom (picked by hand)");
            custom.Checked = true;
            focusItems.Add(custom);
        }
        string every = $"every {(int)c.CooldownMinutes} min";
        var items = new List<ToolStripItem>
        {
            Submenu("Remind me",
                Option("While Claude works", !c.OnTimer, UseClaude),
                Option($"On a timer, {every}", c.OnTimer, () => UseTimer(fromSheet: false))),
            new ToolStripSeparator(),
            Submenu("Focus", [.. focusItems]),
            Submenu("Character", [.. CastInfo.All.Select(x => Option(x.FullName(), c.Character == x.Raw(), () => Update(k => k.Character = x.Raw())))]),
            Submenu("Entrance", [.. new[] { ("card", "Slide in"), ("noodle", "Drop in on a noodle") }
                .Select(e => Option(e.Item2, c.Entrance == e.Item1, () => Update(k => k.Entrance = e.Item1)))]),
        };
        if (!c.OnTimer)   // only means something with Claude
            items.Add(Submenu("Show a card after", Choices([10, 15, 30, 60], c.DelaySeconds,
                v => $"{(int)v} sec of Claude working", v => Update(k => k.DelaySeconds = v))));
        items.AddRange([
            Submenu(c.OnTimer ? "Every" : "At most every", Choices([10, 20, 30, 45, 60], c.CooldownMinutes,
                v => $"{(int)v} min", v => Update(k => k.CooldownMinutes = v))),
            Submenu("Snooze for", Choices([5, 10, 15, 30], c.SnoozeMinutes,
                v => $"{(int)v} min", v => Update(k => k.SnoozeMinutes = v))),
            Submenu("Corner", [.. new[] { ("top-right", "Top right"), ("top-left", "Top left"), ("bottom-right", "Bottom right"), ("bottom-left", "Bottom left") }
                .Select(p => Option(p.Item2, c.Position == p.Item1, () => Update(k => k.Position = p.Item1)))]),
            Submenu("Exercises", ExerciseItems(c)),
            new ToolStripSeparator(),
            Option("Hold during calls (mic or camera on)", c.HoldDuringCalls, () => Update(k => k.HoldDuringCalls = !k.HoldDuringCalls)),
            Option($"Quiet hours, {c.QuietHours.Start} to {c.QuietHours.End}", c.QuietHours.Enabled,
                () => Update(k => k.QuietHours.Enabled = !k.QuietHours.Enabled)),
            Option("Always show steps", c.ExpandSteps, () => Update(k => k.ExpandSteps = !k.ExpandSteps)),
            Option("Say \"It's time to stretch!\" first", c.Announce, () => Update(k => k.Announce = !k.Announce)),
            Option("Open at login", LoginItem.IsOn, () => LoginItem.Set(!LoginItem.IsOn)),
            new ToolStripSeparator(),
            HooksConnected ? Item("Disconnect from Claude Code…", OpenDisconnect) : Item("Connect to Claude Code…", OpenConnect),
            Item("Edit config file…", OpenConfig),
        ]);
        return Submenu("Settings", [.. items]);
    }

    // MARK: Claude Code hooks. The same change `Noodle.exe setup` / `remove` make, previewed in the Noodle window.

    bool HooksConnected { get { try { return hooks.IsConnected(); } catch { return false; } } }
    /// Waiting on the Connect step: Claude mode without the hooks. Timer mode doesn't need them.
    bool NeedsHooks => !store.Config().OnTimer && !HooksConnected;

    /// Settings › Remind me › While Claude works. Without the hooks that's the Connect step.
    void UseClaude()
    {
        Update(c => c.Mode = "claude");
        if (!HooksConnected) OpenConnect();
    }

    /// A card every cooldownMinutes, Claude or not. From the Connect step it's the "Use a timer instead" way out.
    void UseTimer(bool fromSheet)
    {
        Update(c => c.Mode = "timer");
        posture.RestartTimer();
        int every = (int)store.Config().CooldownMinutes;
        Log.Debug($"mode timer, every {every} min");
        if (!fromSheet) return;
        ShowDone("On a timer", $"Your noodle will drop by with a stretch every {every} minutes while you're at your computer, Claude or not. Time away from the keyboard counts as a break. Switch anytime under Settings › Remind me.");
        if (store.State().Onboarded != true) posture.ShowWelcome();
    }

    void OpenConnect() => OpenConnect(activate: true);

    void OpenConnect(bool activate)
    {
        home.ShowWith(HomeState(), activate);
        ShowHooksSheet(connect: true);
    }

    void OpenDisconnect()
    {
        home.ShowWith(HomeState());
        ShowHooksSheet(connect: false);
    }

    /// Shows what connecting (or disconnecting) would change in settings.json, and waits for "apply".
    void ShowHooksSheet(bool connect, string? error = null)
    {
        string mode = connect ? "connect" : "disconnect";
        var s = new Dictionary<string, object?> { ["mode"] = mode, ["path"] = ClaudeHooks.Tilde(hooks.FilePath) };
        hooksPlan = null;
        try
        {
            var plan = hooks.MakePlan(connect);
            if (!plan.Changed)
            {
                ShowDone(connect ? "Already connected" : "Not connected",
                    connect ? "Noodle's hooks are already in your Claude Code settings. Nothing to change."
                            : "Noodle's hooks aren't in your Claude Code settings. Nothing to remove.");
                return;
            }
            hooksPlan = plan;
            s["diff"] = plan.Diff();
            s["creates"] = !File.Exists(hooks.FilePath);
        }
        catch (Exception e) { s["error"] = e.Message; }
        if (error != null) s["error"] = error;
        if (connect && !LoginItem.IsOn) s["login"] = true;
        if (connect && (Environment.ProcessPath ?? "").Contains(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            s["note"] = "Noodle is running from inside a zip or a temp folder. Install it (or unzip it somewhere to keep) and open it from there, so Claude Code can find it after a restart.";
        sheetMode = mode;
        home.Sheet(s);
    }

    void ApplyHooks(bool login)
    {
        if (hooksPlan is not { } plan || sheetMode is not { } mode || mode == "done") return;
        bool connect = mode == "connect";
        try
        {
            var backup = hooks.Apply(plan);
            Log.Debug($"{mode}ed Claude Code hooks in {hooks.FilePath}{(backup != null ? $", backup {Path.GetFileName(backup)}" : "")}");
            if (connect)
            {
                Prefs.Set("connectDismissed", false);
                if (store.Config().OnTimer) Update(c => c.Mode = "claude");   // connecting means you want cards with Claude
                if (login) LoginItem.Set(true);
            }
            Refresh();
            ShowDone(connect ? "Connected to Claude Code" : "Disconnected",
                connect ? "Next time Claude works on something for a while, your noodle pops up with a stretch. Claude Code sessions that were already open may need a restart to notice."
                        : "Noodle won't hear from Claude Code anymore. Connect again anytime from Settings in the tray menu, or have it remind you on a timer under Settings › Remind me.",
                backup);
            if (connect && store.State().Onboarded != true) posture.ShowWelcome();
        }
        catch (ClaudeHooks.Failure f) when (f.ChangedOnDisk) { ShowHooksSheet(connect, f.Message); }
        catch (Exception e) { ShowHooksSheet(connect, $"Couldn't write {ClaudeHooks.Tilde(hooks.FilePath)}: {e.Message}"); }
    }

    void ShowDone(string title, string message, string? backup = null)
    {
        hooksPlan = null;
        sheetMode = "done";
        var s = new Dictionary<string, object?> { ["mode"] = "done", ["title"] = title, ["message"] = message };
        if (backup != null) s["backup"] = Path.GetFileName(backup);
        home.Sheet(s);
    }

    /// "Not now", Done, Esc, or the window closing. Passing on Connect stops it opening at every launch.
    void SheetClosed()
    {
        if (sheetMode == "connect")
        {
            Prefs.Set("connectDismissed", true);
            if (store.State().Onboarded != true) posture.ShowWelcome();
        }
        sheetMode = null;
        hooksPlan = null;
    }

    // MARK: Film submenu. Cues shots on a countdown; film cards never touch the count, cooldown, or rotation.

    static double FilmDelay => Prefs.Double("filmDelay") ?? 5;
    static Posture.FilmOptions FilmOptions => new()
    {
        PeekAfter = Prefs.Bool("filmQuickPeek") ? 3 : null,
        Timer = Prefs.Bool("filmShortTimer") ? 5 : null,
        Announce = Prefs.Bool("filmAnnounce"),
    };

    ToolStripMenuItem FilmMenu()
    {
        double delay = FilmDelay;
        string when = delay == 0 ? "now" : $"in {(int)delay} sec";
        void Toggle(string key) => Prefs.Set(key, !Prefs.Bool(key));
        return Submenu("Film",
            Submenu("Countdown", [.. new double[] { 0, 3, 5, 10 }.Select(v =>
                Option(v == 0 ? "None" : $"{(int)v} sec", v == delay, () => Prefs.Set("filmDelay", v)))]),
            new ToolStripSeparator(),
            Submenu($"Stretch {when}", [.. store.Exercises.Select(ex => Option(ex.Name, false, () => Cue(() => posture.Film(ex.Id, FilmOptions))))]),
            Option($"Intro {when}", false, () => Cue(posture.FilmIntro)),
            Option($"Claude finishes {when}", false, () => Cue(() => posture.Handle("stop"))),
            Option("Clear the screen", false, () => posture.AfterClearing(() => { })),
            Option($"Open Noodle {when}", false, () => Cue(OpenHome)),
            new ToolStripSeparator(),
            Option("Open with \"It's time to stretch!\"", Prefs.Bool("filmAnnounce"), () => Toggle("filmAnnounce")),
            Option("Peek after 3 sec, not 20", Prefs.Bool("filmQuickPeek"), () => Toggle("filmQuickPeek")),
            Option("5 sec timer after Start", Prefs.Bool("filmShortTimer"), () => Toggle("filmShortTimer")),
            Option("Keep the Noodle window on top", Prefs.Bool("filmOverFullScreen"), () => { Toggle("filmOverFullScreen"); ApplyFullScreen(); }),
            new ToolStripSeparator(),
            Info("Film cards skip your count and cooldown"));
    }

    void Cue(Action run)
    {
        Log.Debug($"film cue in {(int)FilmDelay}s");
        Later(FilmDelay, run);
    }

    void ApplyFullScreen() => home.FloatsOverFullScreen = Prefs.Bool("filmOverFullScreen");

    /// One checkable row per exercise. Keeps exercises.json order and never lets you turn off the last one.
    ToolStripItem[] ExerciseItems(Config c)
    {
        var all = store.Exercises.Select(e => e.Id).ToList();
        var on = new HashSet<string>(c.EnabledExercises ?? all);
        return [.. store.Exercises.Select(ex =>
        {
            bool isOn = on.Contains(ex.Id);
            var i = Option(ex.Name, isOn, () =>
            {
                var next = new HashSet<string>(on);
                if (isOn) next.Remove(ex.Id); else next.Add(ex.Id);
                if (next.Count == 0) return;
                Update(k => { k.EnabledExercises = all.Where(next.Contains).ToList(); k.Focus = "custom"; });
            });
            if (isOn && on.Count == 1) i.Enabled = false;
            return (ToolStripItem)i;
        })];
    }

    /// Preset values plus the current one if it was set by hand in config.json.
    static ToolStripItem[] Choices(double[] presets, double current, Func<double, string> label, Action<double> set)
    {
        var values = presets.ToList();
        if (!values.Contains(current)) { values.Add(current); values.Sort(); }
        return [.. values.Select(v => (ToolStripItem)Option(label(v), v == current, () => set(v)))];
    }

    void Update(Action<Config> change)
    {
        var c = store.Config();
        change(c);
        store.Save(c);
        Refresh();
    }

    static ToolStripMenuItem Submenu(string title, params ToolStripItem[] items)
    {
        var i = new ToolStripMenuItem(title);
        i.DropDownItems.AddRange(items);
        return i;
    }

    static ToolStripMenuItem Item(string title, Action run, bool bold = false)
    {
        var i = new ToolStripMenuItem(title, null, (_, _) => run());
        if (bold) i.Font = new Font(i.Font, FontStyle.Bold);
        return i;
    }

    static ToolStripMenuItem Option(string title, bool check, Action run) =>
        new(title, null, (_, _) => run()) { Checked = check };

    static ToolStripMenuItem Info(string title) => new(title) { Enabled = false };

    void OpenConfig()
    {
        store.EnsureConfig();
        try { Process.Start(new ProcessStartInfo(store.ConfigPath) { UseShellExecute = true }); }
        catch { Process.Start("notepad.exe", $"\"{store.ConfigPath}\""); }
    }

    void Quit()
    {
        tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) tray.Dispose();
        base.Dispose(disposing);
    }
}

/// Small per-user preferences that aren't part of config.json (film options, "Not now" on Connect), like
/// UserDefaults on the Mac. Lives under HKCU\Software\Noodle.
static class Prefs
{
    const string Key = @"Software\Noodle";

    static object? Get(string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValue(name);
    }

    public static bool Bool(string name) => Get(name) is int v && v != 0;
    public static double? Double(string name) =>
        Get(name) is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    public static void Set(string name, bool value) => Write(name, value ? 1 : 0);
    public static void Set(string name, double value) => Write(name, value.ToString(CultureInfo.InvariantCulture));

    static void Write(string name, object value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        k.SetValue(name, value);
    }
}

namespace Noodle;

/// All scheduling logic. Lives in the single app process, so concurrent Claude sessions
/// just send events here and can never produce two cards. Runs on the UI thread.
sealed class Posture
{
    const double WaitSeconds = 60;          // an untouched card bows out after this
    const double ReadingSeconds = 180;      // How to open, or the intro
    const double SetGraceSeconds = 20;      // a started set finishes itself; this is only a backstop
    const string WorkingSubtitle = "Claude's on it. Good time for a quick one.";
    const string ManualSubtitle = "Quick one while you're here.";
    const string StoppedSubtitle = "Claude's done, finish your set";
    const string TimerSubtitle = "Been a while. Quick one?";
    const double IdleBreakSeconds = 300;    // this long away from the keyboard counts as a break, and restarts the timer
    const double IdleSkipSeconds = 90;      // the timer holds a card while you're not at the keyboard

    public readonly Store Store;
    public readonly CardController Card;
    public event Action? OnChange;

    CancellationTokenSource? delayTask;
    CancellationTokenSource? dismissTask;
    Exercise? current;
    double setSeconds;                       // how long Start runs, shorter when filming
    bool filming;                            // a card for the camera: no log, no cooldown, no rotation
    bool stopped;
    bool started;
    bool welcomeShowing;
    bool hovering;
    DateTime? dismissDeadline;
    string dismissOutcome = "ignored";
    DateTime timerSince = DateTime.Now;      // timer mode: the interval runs from here (launch, switching modes, or a break)
    readonly System.Windows.Forms.Timer ticker = new() { Interval = 15_000 };

    public Posture(Store store, CardController card)
    {
        Store = store;
        Card = card;
        card.OnAction += action =>
        {
            switch (action)
            {
                case "start": StartSet(); break;
                case "done": Finish("done"); break;
                case "snooze": Finish("snoozed"); break;
                case "stop": Finish("stopped"); break;
                case "howto": ExtendForReading(); break;
                case "hover:on": Hover(true); break;
                case "hover:off": Hover(false); break;
                case "welcome-close": CloseWelcome(); break;
                case var a when a.StartsWith("focus:"):
                    if (FocusInfo.From(a[6..]) is Focus f)
                    {
                        Store.Apply(f);
                        Log.Debug($"focus {f.Raw()}");
                    }
                    break;
                case "welcome-try":   // the intro card turns into a stretch in place
                    if (!welcomeShowing) return;
                    welcomeShowing = false;
                    dismissTask?.Cancel();
                    dismissDeadline = null;
                    ShowNow();
                    if (current == null) Card.Hide("closed");   // nothing enabled, just close up
                    break;
                default: Log.Debug($"unknown card action {action}"); break;
            }
        };
        ticker.Tick += (_, _) => TimerTick();
        ticker.Start();
    }

    public void Handle(string ev)
    {
        Log.Debug($"event {ev}");
        switch (ev)
        {
            case "prompt": Prompt(); break;
            case "stop": Stop(); break;
            case "show": ShowNow(); break;
            case "intro": ShowWelcome(); break;
            case "login-on": LoginItem.Set(true); break;
            case "login-off": LoginItem.Set(false); break;
        }
    }

    static async Task<bool> Sleep(double seconds, CancellationTokenSource cts)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, seconds)), cts.Token); return true; }
        catch (TaskCanceledException) { return false; }
    }

    // MARK: Events

    /// Start the delay. A pending delay isn't restarted, so back-to-back prompts
    /// from several sessions still fire on the first one's schedule.
    async void Prompt()
    {
        if (Store.Config().OnTimer) return;   // the timer runs the show, Claude's hooks are just ignored
        if (current != null || delayTask != null) return;
        var cts = delayTask = new CancellationTokenSource();
        if (!await Sleep(Store.Config().DelaySeconds, cts)) return;
        delayTask = null;
        DelayFired();
    }

    void Stop()
    {
        CancelDelay();
        if (current != null && !stopped && !Store.Config().OnTimer)
        {
            stopped = true;
            Card.SetSubtitle(StoppedSubtitle);
        }
    }

    void CancelDelay()
    {
        delayTask?.Cancel();
        delayTask = null;
    }

    public void ShowNow()
    {
        if (current != null) return;
        CancelDelay();
        Show(ManualSubtitle);
    }

    void DelayFired()
    {
        if (current != null) return;
        if (welcomeShowing) { Log.Debug("not showing: intro is up"); return; }
        if (BlockedReason() is string reason) { Log.Debug($"not showing: {reason}"); return; }
        Show(WorkingSubtitle);
    }

    // MARK: Timer mode

    /// Restarts the interval, e.g. on switching to timer mode, so the first card is a full interval away.
    public void RestartTimer()
    {
        timerSince = DateTime.Now;
        OnChange?.Invoke();
    }

    /// When the next timer card is due (holds aside), or null outside timer mode.
    public DateTime? TimerDue()
    {
        var c = Store.Config();
        if (!c.OnTimer) return null;
        var fromStart = timerSince.AddMinutes(c.CooldownMinutes);
        var next = Store.State().NextAllowed ?? fromStart;
        return next > fromStart ? next : fromStart;
    }

    void TimerTick()
    {
        if (TimerDue() is not DateTime due) return;
        double idle = Win32.IdleSeconds();
        if (idle >= IdleBreakSeconds)
        {
            if ((DateTime.Now - timerSince).TotalSeconds > 1) Log.Debug($"away {(int)idle}s, timer restarts when you're back");
            timerSince = DateTime.Now;
            return;
        }
        if (DateTime.Now < due || current != null || welcomeShowing || delayTask != null) return;
        if (idle >= IdleSkipSeconds) return;
        if (BlockedReason() is string reason) { Log.Debug($"timer not showing: {reason}"); return; }
        Show(TimerSubtitle);
    }

    /// Why an automatic card can't show right now, or null if it can.
    public string? BlockedReason(DateTime? at = null)
    {
        var now = at ?? DateTime.Now;
        var c = Store.Config();
        var s = Store.State();
        if (s.PausedUntil is DateTime p && p > now) return "paused";
        if (c.QuietHours.Contains(now)) return "quiet hours";
        if (s.NextAllowed is DateTime n && n > now) return "cooldown";
        if (c.HoldDuringCalls && Calls.InProgress()) return "on a call";
        if (Store.EnabledExercises(c).Count == 0) return "no exercises enabled";
        return null;
    }

    // MARK: Card lifecycle

    void Show(string subtitle)
    {
        welcomeShowing = false;
        var c = Store.Config();
        var list = Store.EnabledExercises(c);
        if (list.Count == 0) { Log.Debug("no exercises enabled"); return; }
        var s = Store.State();
        int i = s.RotationIndex % list.Count;
        var ex = list[i];
        var now = DateTime.Now;
        s.RotationIndex = (i + 1) % list.Count;
        s.LastShown = now;
        s.NextAllowed = now.AddMinutes(c.CooldownMinutes);
        Store.Save(s);
        Present(ex, i + 1, list.Count, subtitle);
    }

    void Present(Exercise ex, int index, int total, string subtitle, FilmOptions? film = null)
    {
        var c = Store.Config();
        current = ex;
        filming = film != null;
        setSeconds = film?.Timer ?? ex.DurationSeconds;
        stopped = false;
        started = false;
        hovering = false;
        var payload = new Dictionary<string, object?>
        {
            ["id"] = ex.Id, ["name"] = ex.Name, ["instruction"] = ex.Instruction, ["spec"] = ex.Spec,
            ["durationSeconds"] = setSeconds, ["figure"] = ex.Figure, ["subtitle"] = subtitle,
            ["index"] = index, ["total"] = total, ["doneToday"] = Store.DoneToday(),
            ["steps"] = ex.Steps ?? [], ["tip"] = ex.Tip ?? "", ["expand"] = c.ExpandSteps,
            ["snoozeMinutes"] = c.SnoozeMinutes, ["announce"] = c.Announce || (film?.Announce ?? false),
        };
        if (film?.PeekAfter is double n) payload["nudgeAfter"] = n;
        if (FigureSVG(ex.Figure, c.Character) is string svg) payload["figureSVG"] = svg;
        if (FigureSVG("figures/wave.svg", c.Character) is string wave) payload["waveSVG"] = wave;
        if (FigureSVG("figures/peek.svg", c.Character) is string peek) payload["peekSVG"] = peek;
        Log.Debug($"show {ex.Id} ({index} of {total}){(filming ? ", filming" : "")}");
        Card.Show(payload, c.Position, c.Inset, dropSVG: DropSVG(c));
        ArmDismiss(c.ExpandSteps ? ReadingSeconds : WaitSeconds);
    }

    /// Character variants live in figures/<character>/; the base files are the sprout.
    string? FigureSVG(string path, string character)
    {
        var b = Path.Combine(Store.Resources, path.Replace('/', Path.DirectorySeparatorChar));
        var variant = Path.Combine(Path.GetDirectoryName(b)!, character, Path.GetFileName(b));
        try { return File.ReadAllText(File.Exists(variant) ? variant : b); }
        catch { return null; }
    }

    string? DropSVG(Config c) => c.Entrance == "noodle" ? FigureSVG("figures/hang.svg", c.Character) : null;

    // MARK: Intro

    /// The one-time "hey, I'm Toni" card (or whichever noodle you picked). Shown on first launch and from the menu.
    public void ShowWelcome()
    {
        if (current != null) return;
        var s = Store.State();
        s.Onboarded = true;
        Store.Save(s);
        var c = Store.Config();
        var cast = CastInfo.From(c.Character);
        var payload = new Dictionary<string, object?>
        {
            ["character"] = c.Character, ["focus"] = c.Focus, ["name"] = cast.Name(), ["fullName"] = cast.FullName(),
        };
        if (FigureSVG("figures/wave.svg", c.Character) is string svg) payload["figureSVG"] = svg;
        Log.Debug("show intro");
        welcomeShowing = true;
        hovering = false;
        Card.Show(payload, c.Position, c.Inset, "showWelcome", DropSVG(c));
        ArmDismiss(ReadingSeconds, "intro");
    }

    void CloseWelcome()
    {
        if (!welcomeShowing) return;
        welcomeShowing = false;
        dismissTask?.Cancel();
        dismissTask = null;
        dismissDeadline = null;
        Card.Hide("closed");
    }

    // MARK: Auto dismiss

    /// Untouched cards bow out on their own. The pointer resting on the card holds them, like a
    /// notification banner, and the clock picks back up (with a few seconds' grace) when it leaves.
    void ArmDismiss(double seconds, string outcome = "ignored")
    {
        dismissDeadline = DateTime.Now.AddSeconds(seconds);
        dismissOutcome = outcome;
        Log.Debug($"auto dismiss in {(int)seconds}s");
        ScheduleDismiss();
    }

    async void ScheduleDismiss()
    {
        dismissTask?.Cancel();
        dismissTask = null;
        if (dismissDeadline is not DateTime deadline) return;
        if (hovering && dismissOutcome != "done") { Log.Debug("auto dismiss held, pointer on the card"); return; }
        var cts = dismissTask = new CancellationTokenSource();
        if (!await Sleep((deadline - DateTime.Now).TotalSeconds, cts)) return;
        Log.Debug("auto dismiss fired");
        dismissDeadline = null;
        if (dismissOutcome == "intro") CloseWelcome(); else Finish(dismissOutcome);
    }

    void Hover(bool on)
    {
        if (hovering == on) return;
        hovering = on;
        if (!on && dismissDeadline is DateTime d && (d - DateTime.Now).TotalSeconds < 6)
            dismissDeadline = DateTime.Now.AddSeconds(6);
        ScheduleDismiss();
    }

    /// Start pressed: the page runs the timer and reports done itself. This only catches a page
    /// that never does.
    void StartSet()
    {
        if (current == null || started) return;
        started = true;
        Log.Debug($"set started, {(int)setSeconds}s");
        ArmDismiss(setSeconds + SetGraceSeconds, "done");
    }

    /// Opening "How to" means someone is reading, so give them a fresh window before auto dismiss.
    void ExtendForReading()
    {
        if (current == null || started) return;
        Log.Debug("how to opened");
        ArmDismiss(ReadingSeconds);
    }

    void Finish(string outcome)
    {
        if (current is not Exercise ex) return;
        current = null;
        started = false;
        hovering = false;
        dismissTask?.Cancel();
        dismissTask = null;
        dismissDeadline = null;
        if (filming)
        {
            filming = false;
            Log.Debug($"film card {ex.Id} {outcome}, not logged");
            Card.Hide(outcome);
            return;
        }
        Store.LogOutcome(ex.Id, outcome);
        if (outcome == "snoozed")
        {
            var s = Store.State();
            var from = s.NextAllowed is DateTime n && n > DateTime.Now ? n : DateTime.Now;
            s.NextAllowed = from.AddMinutes(Store.Config().SnoozeMinutes);
            Store.Save(s);
        }
        Card.Hide(outcome);
        OnChange?.Invoke();
    }

    // MARK: Filming

    public sealed class FilmOptions
    {
        public double? PeekAfter;   // seconds before Toni peeks, instead of 20
        public double? Timer;       // seconds Start runs, instead of the exercise's own
        public bool Announce;       // open with "It's time to stretch!" even when the setting is off
    }

    /// Shows a specific stretch for the camera. Leaves the rotation, cooldown, and log alone.
    public void Film(string id, FilmOptions options)
    {
        var ex = Store.Exercises.FirstOrDefault(e => e.Id == id);
        if (ex == null) return;
        int index = Store.Exercises.ToList().IndexOf(ex) + 1;
        AfterClearing(() => Present(ex, index, Store.Exercises.Count, WorkingSubtitle, options));
    }

    public void FilmIntro() => AfterClearing(ShowWelcome);

    /// Takes down whatever's up without logging it, then gives the exit a moment before `next`.
    public async void AfterClearing(Action next)
    {
        CancelDelay();
        bool busy = current != null || welcomeShowing;
        if (welcomeShowing) CloseWelcome();
        if (current != null) { filming = true; Finish("closed"); }
        if (busy) await Task.Delay(700);
        next();
    }

    // MARK: Pause

    public void Pause(DateTime? until)
    {
        var s = Store.State();
        s.PausedUntil = until;
        Store.Save(s);
        if (until != null) CancelDelay();
        OnChange?.Invoke();
    }
}

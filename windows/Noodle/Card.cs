using System.Text.Json;

namespace Noodle;

/// Borderless, see-through, always-on-top window that never takes focus, so a card can come up while
/// you're typing in a terminal without stealing a keystroke. Kept out of Alt-Tab and the taskbar.
class OverlayForm : Form
{
    public readonly WebPage Page;
    Rectangle? desired;   // where we put it; re-applied after a DPI change moves it to another monitor

    public OverlayForm(string resources, string page)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;   // alpha 0 under DWM blur-behind, i.e. transparent
        TopMost = true;
        Page = new WebPage(resources, page, transparent: true);
        // Otherwise showing the form selects the WebView, which focuses a button and draws its focus ring.
        Page.View.TabStop = false;
        Controls.Add(Page.View);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Win32.EnableAlpha(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_MOUSEACTIVATE) { m.Result = Win32.MA_NOACTIVATE; return; }
        base.WndProc(ref m);
        if (m.Msg == 0x02E0 /* WM_DPICHANGED */ && desired is { } r) Bounds = r;
    }

    /// Moves it to `r` (device pixels) and shows it on top without activating.
    public void Place(Rectangle r, bool show)
    {
        desired = r;
        Bounds = r;
        if (!show) return;
        // Through WinForms, so WebView2 learns it's visible and paints; ShowWithoutActivation keeps your focus.
        if (!Visible) Show();
        Win32.ShowNoActivate(Handle, r);
    }

    public void OrderOut()
    {
        desired = null;
        Hide();
    }
}

/// Hosts card.html. C# -> JS via window.showCard / showWelcome / enter / leave / setSubtitle,
/// JS -> C# via webkit.messageHandlers.posture.postMessage({action}). See card.html for the contract.
///
/// The window just sits where the card rests, sized to the card plus a transparent margin, and the
/// page does all the moving: slide in, height changes, celebrations, exits. It tells us when it's
/// done leaving ("gone") so the window can go.
sealed class CardController
{
    public event Action<string>? OnAction;
    public bool IsVisible { get; private set; }   // a card is up, from show until hide

    record struct Margins(double T, double R, double B, double L);

    readonly OverlayForm panel;
    readonly WebPage web;
    readonly Dropper dropper;
    bool ready;
    Action? queued;
    SizeF baseCard = new(340, 132);   // overridden by window.CARD in card.html
    double cardHeight = 132;          // current height, grows when "How to" opens
    double pad = 24;                  // transparent margin for the CSS shadow
    Margins margins = new(24, 24, 24, 24);
    bool anchorTop = true;            // which edge stays put when the card resizes
    double scale = 1;                 // device pixels per CSS px on the card's monitor
    int generation;
    const double DropSeconds = 0.95;

    public CardController(string resources)
    {
        dropper = new Dropper(resources);
        panel = new OverlayForm(resources, "card.html");
        web = panel.Page;
        web.Loaded += ReadCardSize;
        web.Message += (_, body) => Receive(body);
        _ = panel.Handle;
        _ = web.InitAsync();
    }

    /// `function` is the card.html entry point: showCard for exercises, showWelcome for the intro.
    /// The card slides in from the screen edge. With `dropSVG` and a top corner, the character rides a
    /// noodle down from the top of the screen first and the card grows out of its tile where it lands.
    /// If a card is already up (the intro), the page swaps the new one in place.
    public void Show(Dictionary<string, object?> payload, string position, double inset, string function = "showCard",
                     string? dropSVG = null)
    {
        bool swapping = IsVisible && panel.Visible;
        bool top = !position.StartsWith("bottom");
        bool reduceMotion = Win32.ReduceMotion();
        string? drop = (top && !reduceMotion && !swapping) ? dropSVG : null;
        var l = Layout(position, inset);
        dropper.Cancel();
        IsVisible = true;
        generation++;

        payload["layout"] = new Dictionary<string, object>
        {
            ["t"] = l.margins.T, ["r"] = l.margins.R, ["b"] = l.margins.B, ["l"] = l.margins.L,
            ["edge"] = position.EndsWith("left") ? "left" : "right", ["anchor"] = top ? "top" : "bottom",
        };
        payload["entrance"] = reduceMotion ? "fade" : (drop != null ? "hold" : "slide");
        if (dropSVG != null && top) payload["noodle"] = inset;   // it hangs from the noodle it came down on
        string js = $"window.{function}({JsonSerializer.Serialize(payload)})";
        double tileFromTop = function == "showWelcome" ? 80 : 66;   // figure tile center, from card top

        void Run()
        {
            if (!IsVisible) return;
            if (!swapping)
            {
                margins = l.margins;
                anchorTop = top;
                scale = l.scale;
                cardHeight = baseCard.Height;
                panel.Place(l.frame, show: drop == null);   // the page is blank until it slides the card in
            }
            web.Run(js);
            if (drop != null) DropIn(drop, l.frame, l.workArea, inset, tileFromTop);
        }
        if (ready) Run(); else queued = Run;
    }

    public void SetSubtitle(string text) => web.Run($"window.setSubtitle({JsonSerializer.Serialize(text)})");

    /// Plays the exit for `reason` (done, snoozed, stopped, ignored, closed). The page posts "gone"
    /// when it's finished, and a timer covers it in case it never does.
    public async void Hide(string reason = "closed")
    {
        dropper.Cancel();
        if (!IsVisible) return;
        IsVisible = false;
        queued = null;
        int g = ++generation;
        if (!panel.Visible) return;   // dismissed mid-drop, the card never came out
        web.Run($"window.leave({JsonSerializer.Serialize(reason)})");
        await Task.Delay(3000);
        if (generation == g && !IsVisible) panel.OrderOut();
    }

    /// Where the card goes: on the screen with the mouse, `inset` from the corner of the work area (so
    /// clear of the taskbar). The window adds a margin for the shadow and reaches past the work area
    /// edge so the card can slide in from it. Returns device pixels.
    (Rectangle frame, Rectangle workArea, Margins margins, double scale) Layout(string position, double inset)
    {
        var mouse = Cursor.Position;
        var wa = Screen.FromPoint(mouse).WorkingArea;
        double s = Win32.ScaleAt(mouse);
        bool top = !position.StartsWith("bottom"), left = position.EndsWith("left");
        double edge = Math.Max(pad, inset + 8);
        var m = new Margins(top ? edge : pad, left ? pad : edge, pad, left ? edge : pad);
        // Card position in CSS px, relative to the work area's top-left.
        double w = wa.Width / s, h = wa.Height / s;
        double cardX = left ? inset : w - inset - baseCard.Width;
        double cardY = top ? inset : h - inset - baseCard.Height;
        double x = cardX - m.L, y = cardY - m.T;
        var frame = new Rectangle(wa.X + (int)Math.Round(x * s), wa.Y + (int)Math.Round(y * s),
            (int)Math.Round((baseCard.Width + m.L + m.R) * s), (int)Math.Round((baseCard.Height + m.T + m.B) * s));
        return (frame, wa, m, s);
    }

    /// A strip from the top of the work area down past the card's figure tile, centered on the tile. The
    /// character lands on the tile, then the card grows out from under it and the strip goes.
    void DropIn(string svg, Rectangle frame, Rectangle wa, double inset, double tileFromTop)
    {
        double tileCenterX = frame.X / scale + margins.L + 14 + 52;
        double landY = inset + tileFromTop;   // tile center, measured down from the top of the work area
        double height = landY + 70;
        var strip = new Rectangle((int)Math.Round((tileCenterX - 70) * scale), wa.Y,
                                  (int)Math.Round(140 * scale), (int)Math.Round(height * scale));
        int g = generation;
        dropper.Drop(svg, strip, landY, DropSeconds, () =>
        {
            if (!IsVisible || generation != g) return;
            panel.Place(frame, show: true);
            web.Run("window.enter('bloom')");
            dropper.Finish();
        });
    }

    /// The card asked for a new height. Keeps the edge nearest the screen corner fixed so the card
    /// grows away from it, then tells the page there's room.
    void Resize(double height, int id)
    {
        if (IsVisible && height > 0 && Math.Abs(height - cardHeight) > 0.5)
        {
            var old = panel.Bounds;
            cardHeight = height;
            int newH = (int)Math.Round((height + margins.T + margins.B) * scale);
            int y = anchorTop ? old.Y : old.Bottom - newH;
            panel.Place(new Rectangle(old.X, y, old.Width, newH), show: panel.Visible);
        }
        web.Run($"window.resized && window.resized({id})");
    }

    async void ReadCardSize()
    {
        try
        {
            // ExecuteScript hands back JSON, here a JSON string holding JSON.
            var outer = await web.Eval("JSON.stringify(window.CARD || null)");
            if (JsonSerializer.Deserialize<string>(outer) is string inner &&
                JsonSerializer.Deserialize<Dictionary<string, double>>(inner) is { } m)
            {
                if (m.TryGetValue("width", out var w) && m.TryGetValue("height", out var h)) baseCard = new((float)w, (float)h);
                if (m.TryGetValue("pad", out var p)) pad = p;
            }
        }
        catch (Exception e) { Log.Debug($"window.CARD unreadable: {e.Message}"); }
        Log.Debug($"card ready, {baseCard.Width}x{baseCard.Height} pad {pad}");
        ready = true;
        queued?.Invoke();
        queued = null;
    }

    void Receive(JsonElement body)
    {
        string? action = body.ValueKind switch
        {
            JsonValueKind.Object when body.TryGetProperty("action", out var a) => a.GetString(),
            JsonValueKind.String => body.GetString(),
            _ => null,
        };
        switch (action)
        {
            case "resize":
                double h = body.TryGetProperty("height", out var hv) && hv.TryGetDouble(out var hd) ? hd : 0;
                int id = body.TryGetProperty("id", out var iv) && iv.TryGetInt32(out var idv) ? idv : 0;
                Resize(h, id);
                break;
            case "gone":
                Log.Debug("card gone");
                if (!IsVisible) panel.OrderOut();
                break;
            case "hover":
                bool on = body.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.True;
                OnAction?.Invoke(on ? "hover:on" : "hover:off");
                break;
            case "focus":
                if (body.TryGetProperty("value", out var f) && f.GetString() is string fv) OnAction?.Invoke($"focus:{fv}");
                break;
            case string other:
                Log.Debug($"card message {other}");
                OnAction?.Invoke(other);
                break;
        }
    }
}

/// The noodle drop: a transparent strip hanging from the top of the screen. The character rides a
/// spaghetti strand down to the card's spot, then the card takes over.
sealed class Dropper
{
    readonly OverlayForm panel;
    readonly WebPage web;
    Action? queued;
    Action? onArrive;
    int generation;

    public Dropper(string resources)
    {
        panel = new OverlayForm(resources, "drop.html");
        web = panel.Page;
        web.Loaded += () => { queued?.Invoke(); queued = null; };
        web.Message += (_, body) => { if (body.ValueKind == JsonValueKind.String && body.GetString() == "arrived") Arrive(); };
        _ = panel.Handle;
        _ = web.InitAsync();
    }

    /// Drops the character inside `strip` (device pixels, top edge at the top of the work area) so its
    /// 96px box lands centered `landY` CSS px below the top. Calls `arrived` once, even if the page never answers.
    public async void Drop(string svg, Rectangle strip, double landY, double duration, Action arrived)
    {
        int g = ++generation;
        onArrive = arrived;
        var args = JsonSerializer.Serialize(new Dictionary<string, object> { ["svg"] = svg, ["landY"] = landY, ["duration"] = duration });
        void Run()
        {
            if (generation != g) return;
            panel.Place(strip, show: true);
            web.Run($"window.drop({args})");
        }
        if (web.Ready) Run(); else queued = Run;
        await Task.Delay(TimeSpan.FromSeconds(duration + 1.5));   // safety net if the page never posts "arrived"
        if (generation == g) Arrive();
    }

    /// Takes the rig down once the card is out in its place.
    public async void Finish()
    {
        int g = generation;
        await Task.Delay(200);
        if (generation == g) panel.OrderOut();
    }

    /// Stops a drop in progress without calling back (the card was dismissed mid-drop).
    public void Cancel()
    {
        generation++;
        onArrive = null;
        queued = null;
        if (panel.Visible) panel.OrderOut();
    }

    void Arrive()
    {
        var cb = onArrive;
        onArrive = null;
        cb?.Invoke();
    }
}

using System.Text.Json;

namespace Noodle;

/// The Noodle window: a welcome screen with the whole cast idling, where you pick your noodle and
/// try a stretch. Opens from the tray, or when Noodle is opened again. See home.html.
sealed class HomeWindow : Form
{
    /// (action, value): "character" with an id, "stretch", "intro", "connect" or "disconnect" (open that sheet),
    /// "apply" (confirm the sheet, value "login" to also open at login), "sheet-dismissed", or "closed".
    public event Action<string, string?>? OnAction;
    /// Stays above other windows, for filming.
    public bool FloatsOverFullScreen { get => TopMost; set => TopMost = value; }

    readonly WebPage web;
    bool initialized;

    // Matches home.html's background, so opening never flashes white.
    static readonly Color Dark = Color.FromArgb(0x1D, 0x19, 0x17), Light = Color.FromArgb(0xFF, 0xFC, 0xF9);

    public HomeWindow(string resources)
    {
        Text = "Noodle";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(680, 470);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "noodle.ico")))
            Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "noodle.ico"));
        web = new WebPage(resources, "home.html", transparent: false);
        web.Message += (_, body) => Receive(body);
        Controls.Add(web.View);
        ApplyTheme();
    }

    public bool IsOpen => Visible;

    bool quietly;
    protected override bool ShowWithoutActivation => quietly;

    /// `activate` false is for when Noodle opens the window on its own (the Connect step at launch): it comes
    /// up without taking the keyboard, so a keystroke meant for your terminal can't press its focused Connect button.
    public void ShowWith(Dictionary<string, object?> state, bool activate = true)
    {
        EnsureInit();
        bool reopening = !Visible;
        Push(state, animate: reopening);
        if (reopening)
        {
            ApplyTheme();
            CenterToScreen();
            quietly = !activate;
            Show();
            quietly = false;
        }
        if (!activate) return;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    /// Keeps an open window current (a new pick from the menu, a stretch done) without replaying the welcome.
    public void UpdateState(Dictionary<string, object?> state)
    {
        if (Visible) Push(state, animate: false);
    }

    void Push(Dictionary<string, object?> state, bool animate) =>
        web.Run($"window.home({JsonSerializer.Serialize(state)}, {(animate ? "true" : "false")})");

    /// The Connect / Disconnect sheet over the window: null closes it. See window.sheet in home.html.
    public void Sheet(Dictionary<string, object?>? s) => web.Run($"window.sheet({JsonSerializer.Serialize(s)})");

    void EnsureInit()
    {
        if (initialized) return;
        initialized = true;
        _ = Handle;
        _ = web.InitAsync();
    }

    void ApplyTheme()
    {
        bool dark = Win32.DarkMode();
        BackColor = dark ? Dark : Light;
        web.View.DefaultBackgroundColor = BackColor;
        if (IsHandleCreated) Win32.StyleTitleBar(Handle, dark, BackColor);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Win32.StyleTitleBar(Handle, Win32.DarkMode(), BackColor);
    }

    // Closing just hides it. Blank the page as it goes, so reopening plays the welcome instead of flashing the old frame.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            OnAction?.Invoke("closed", null);
            web.Run("document.body.classList.remove('shown'); window.sheet(null)");
            return;
        }
        base.OnFormClosing(e);
    }

    void Receive(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("action", out var a) || a.GetString() is not string action) return;
        if (action == "close") { Close(); return; }
        string? value = body.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        OnAction?.Invoke(action, value);
    }
}

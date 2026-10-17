using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Noodle;

/// One of the shared pages (card.html, home.html, drop.html) in a WebView2. The pages talk to the Mac app
/// through webkit.messageHandlers.<name>.postMessage, so a shim injected before they load provides exactly
/// that on top of chrome.webview, and the HTML stays identical on both platforms.
sealed class WebPage
{
    const string Host = "noodle.example";
    static Task<CoreWebView2Environment>? environment;

    public readonly WebView2 View = new() { Dock = DockStyle.Fill };
    public bool Ready { get; private set; }
    /// (handler name, message body) for every postMessage from the page.
    public event Action<string, JsonElement>? Message;
    /// The page finished loading, the first time and after every reload.
    public event Action? Loaded;

    readonly string resources;
    readonly string page;
    readonly List<string> queued = [];

    public WebPage(string resources, string page, bool transparent)
    {
        this.resources = resources;
        this.page = page;
        if (transparent) View.DefaultBackgroundColor = Color.Transparent;
    }

    /// Call once the hosting form has a handle.
    public async Task InitAsync()
    {
        try { await Init(); }
        catch (Exception e) { Log.Debug($"{page} WebView2 init failed: {e}"); }
    }

    async Task Init()
    {
        _ = View.Handle;   // the control waits for its own handle, which a hidden window never creates
        Log.Debug($"{page} starting WebView2");
        environment ??= CoreWebView2Environment.CreateAsync(null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Noodle", "WebView2"),
            // Chromium pauses pages it thinks are covered up; these see-through topmost windows can read
            // as covered, so cards came up blank on the Mac until the same check was turned off there.
            new CoreWebView2EnvironmentOptions("--disable-features=CalculateNativeWinOcclusion"));
        await View.EnsureCoreWebView2Async(await environment);
        var core = View.CoreWebView2;
        var s = core.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsStatusBarEnabled = false;
        s.AreDevToolsEnabled = Environment.GetEnvironmentVariable("NOODLE_DEVTOOLS") == "1";
        core.SetVirtualHostNameToFolderMapping(Host, resources, CoreWebView2HostResourceAccessKind.Allow);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Shim);
        core.WebMessageReceived += (_, e) => Receive(e.WebMessageAsJson);
        core.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess) { Log.Debug($"{page} failed to load: {e.WebErrorStatus}"); return; }
            Ready = true;
            Log.Debug($"{page} loaded");
            foreach (var js in queued) _ = core.ExecuteScriptAsync(js);
            queued.Clear();
            Loaded?.Invoke();
        };
        // The Windows twin of webViewWebContentProcessDidTerminate: load it again so the next card has somewhere to go.
        core.ProcessFailed += (_, e) =>
        {
            Log.Debug($"{page} process failed ({e.ProcessFailedKind}), reloading");
            Ready = false;
            Load();
        };
        Load();
    }

    void Load() => View.CoreWebView2?.Navigate($"https://{Host}/{page}");

    /// Runs `js` now, or once the page has loaded.
    public void Run(string js)
    {
        if (Ready && View.CoreWebView2 is { } core) _ = core.ExecuteScriptAsync(js);
        else queued.Add(js);
    }

    /// Runs `js` and returns its result as JSON ("null" before the page is up).
    public async Task<string> Eval(string js) =>
        View.CoreWebView2 is { } core ? await core.ExecuteScriptAsync(js) : "null";

    void Receive(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Message?.Invoke(root.GetProperty("handler").GetString() ?? "", root.GetProperty("body").Clone());
        }
        catch (Exception e) { Log.Debug($"{page} sent an unreadable message: {e.Message}"); }
    }

    /// webkit.messageHandlers for the three pages, plus Ctrl+W where the Mac pages listen for Cmd+W.
    const string Shim = """
        (() => {
          const post = (handler) => ({ postMessage: (body) => chrome.webview.postMessage({ handler, body }) });
          window.webkit = { messageHandlers: { posture: post("posture"), home: post("home"), dropper: post("dropper") } };
          document.documentElement.dataset.platform = "windows";
          if (location.pathname.endsWith("/home.html")) addEventListener("keydown", (e) => {
            if (e.ctrlKey && (e.key === "w" || e.key === "W")) chrome.webview.postMessage({ handler: "home", body: { action: "close" } });
          });
        })();
        """;
}

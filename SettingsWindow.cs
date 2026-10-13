using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TrackpadClicks;

// The settings window: a WebView2 page (ui/settings.html, embedded in the exe) that
// draws the trackpad, its gesture zones and the fingers on it live. Runs on the UI
// thread. Messages, as JSON:
//   page -> app: {type:"ready"}, {type:"config", config}, {type:"enabled", value}, {type:"log"},
//                {type:"test", value} (the report bytes in "live" while on)
//   app -> page: {type:"state", config, enabled, actions}, {type:"live", ...Live}
sealed class SettingsWindow : Form
{
    readonly Gestures _gestures;
    readonly Action<bool> _setEnabled;
    readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Transparent };
    readonly System.Windows.Forms.Timer _live = new() { Interval = 33 };
    string _lastLive = "";

    public SettingsWindow(Gestures gestures, Action<bool> setEnabled)
    {
        _gestures = gestures;
        _setEnabled = setEnabled;
        Text = "TrackpadClicks";
        Icon = Tray.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 620);
        Size = new Size(1140, 820);
        BackColor = Dark ? Color.FromArgb(28, 28, 30) : Color.FromArgb(245, 245, 247);
        Controls.Add(_web);
        _live.Tick += (_, _) => SendLive();
        HandleCreated += (_, _) =>
        {
            int on = Dark ? 1 : 0;
            DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        };
        Load += async (_, _) => await Start();
        FormClosed += (_, _) =>
        {
            _live.Stop();
            RawInput.Capture = false;
        };
    }

    async Task Start()
    {
        try
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackpadClicks", "WebView2");
            await _web.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, data));
        }
        catch (Exception e)
        {
            Log.WriteNow($"settings window: WebView2 failed: {e.Message}");
            MessageBox.Show("Окну настроек нужен Microsoft Edge WebView2 Runtime:\nhttps://go.microsoft.com/fwlink/p/?LinkId=2124703",
                "TrackpadClicks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
            return;
        }
        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = File.Exists(@"C:\TrackpadClicks\debug");
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.WebMessageReceived += (_, e) => OnMessage(e.WebMessageAsJson);
        using var html = typeof(SettingsWindow).Assembly.GetManifestResourceStream("settings.html")!;
        core.NavigateToString(new StreamReader(html).ReadToEnd());
    }

    void OnMessage(string json)
    {
        var message = JsonNode.Parse(json);
        switch ((string?)message?["type"])
        {
            case "ready":
                SendState();
                _live.Start();
                break;
            case "config":
                if (message!["config"].Deserialize<Config>(Settings.Json) is Config config)
                {
                    Settings.Apply(config);
                    Log.Write("settings changed");
                }
                break;
            case "enabled":
                _setEnabled((bool)message!["value"]!);
                break;
            case "test":
                RawInput.Capture = (bool)message!["value"]!;
                break;
            case "log":
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.Path) { UseShellExecute = true });
                break;
        }
    }

    // The tray can switch gestures on or off while the window is open.
    public void SendState()
    {
        if (_web.CoreWebView2 == null) return;
        var state = new JsonObject
        {
            ["type"] = "state",
            ["config"] = JsonSerializer.SerializeToNode(Settings.Current, Settings.Json),
            ["enabled"] = Gestures.Enabled,
        };
        _web.CoreWebView2.PostWebMessageAsJson(state.ToJsonString());
    }

    void SendLive()
    {
        if (_web.CoreWebView2 == null || WindowState == FormWindowState.Minimized) return;
        Live? live;
        lock (_gestures.Sync) live = _gestures.Snapshot();
        if (live == null) return;
        var node = JsonSerializer.SerializeToNode(live, Settings.Json)!.AsObject();
        node["type"] = "live";
        string json = node.ToJsonString();
        if (json == _lastLive) return;
        _lastLive = json;
        _web.CoreWebView2.PostWebMessageAsJson(json);
    }

    static bool Dark =>
        Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is int light && light == 0;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

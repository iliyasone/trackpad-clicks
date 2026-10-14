using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace TrackpadClicks;

// Updates are never applied by themselves. One gets into C:\TrackpadClicks\update in
// two ways: Fetch downloads the latest GitHub release when it is newer (at start-up and
// every 6 hours), or scripts/stage-update.sh puts one there. It holds update.json, the
// new TrackpadClicks.exe, and optionally a driver/ folder with a Magic Trackpad driver
// package (and an install.ps1 to run instead of plain pnputil). It is applied only when the user
// asks: the tray menu, the settings window, or a trackpadclicks://update link, which
// Windows opens with this exe (registered at start-up, per user).
//
// Applying: the driver through an elevated pnputil (Windows asks first), then the new exe
// swapped in by renaming the running one (a locked exe can still be renamed), the old
// copies stopped and the new one started.
static class Updater
{
    public const string InstallDir = @"C:\TrackpadClicks";
    public static readonly string Dir = Path.Combine(InstallDir, "update");
    static string Manifest => Path.Combine(Dir, "update.json");
    public const string Scheme = "trackpadclicks";

    public static string Current =>
        typeof(Updater).Assembly.GetName().Version is Version v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    // RestartCiao: the update changes what Ciao should listen to; restart it to reread its settings.
    public sealed record Pending(string Version, string Notes, string? Driver, bool RestartCiao = false);

    // The staged update, if there is one that is not applied yet.
    public static Pending? Check()
    {
        try
        {
            if (!File.Exists(Manifest)) return null;
            var p = JsonSerializer.Deserialize<Pending>(File.ReadAllText(Manifest), Settings.Json);
            if (p == null) return null;
            bool newer = System.Version.TryParse(p.Version, out var v) && System.Version.TryParse(Current, out var c) && v > c;
            return newer || p.Driver != null ? p : null;
        }
        catch { return null; }
    }

    // Opens trackpadclicks://… links with this exe. Per user, no admin needed.
    public static void Register()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
            key.SetValue("", "URL:TrackpadClicks");
            key.SetValue("URL Protocol", "");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{Path.Combine(InstallDir, "TrackpadClicks.exe")}\" --url \"%1\"");
        }
        catch (Exception e) { Log.WriteNow($"url scheme not registered: {e.Message}"); }
    }

    // Applies the staged update and starts the new version; returns an error to show, or
    // never returns. From any copy of the exe: the running one, or one opened by a link.
    public static string? Apply()
    {
        var pending = Check();
        if (pending == null) return "Обновления нет.";
        Log.WriteNow($"update {Current} -> {pending.Version}{(pending.Driver != null ? $", driver {pending.Driver}" : "")}");

        string driverDir = Path.Combine(Dir, "driver");
        var inf = Directory.Exists(driverDir) ? Directory.GetFiles(driverDir, "*.inf").FirstOrDefault() : null;
        string script = Path.Combine(driverDir, "install.ps1");
        bool reboot = false;
        if (pending.Driver != null && inf != null)
        {
            try
            {
                var start = File.Exists(script)
                    ? new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
                    : new ProcessStartInfo("pnputil.exe", $"/add-driver \"{inf}\" /install");
                using var pnputil = Process.Start(new ProcessStartInfo(start.FileName, start.Arguments)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                })!;
                pnputil.WaitForExit();
                // 3010: installed, a restart finishes it.
                if (pnputil.ExitCode is not (0 or 3010))
                {
                    Log.WriteNow($"update: pnputil exit {pnputil.ExitCode}");
                    return $"Драйвер не установился (pnputil, код {pnputil.ExitCode}).";
                }
                Log.WriteNow($"update: driver installed, exit {pnputil.ExitCode}");
                reboot = pnputil.ExitCode == 3010;
                Directory.Delete(Path.Combine(Dir, "driver"), true);
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                return "Установка драйвера отменена.";
            }
        }

        string installed = Path.Combine(InstallDir, "TrackpadClicks.exe");
        string staged = Path.Combine(Dir, "TrackpadClicks.exe");
        if (File.Exists(staged))
        {
            foreach (var old in Directory.GetFiles(InstallDir, "TrackpadClicks.old-*.exe"))
                try { File.Delete(old); } catch { }
            if (File.Exists(installed)) File.Move(installed, Path.Combine(InstallDir, $"TrackpadClicks.old-{DateTime.Now:yyyyMMddHHmmss}.exe"));
            File.Copy(staged, installed);
        }
        File.Move(Manifest, Path.Combine(Dir, $"applied-{pending.Version}.json"), true);

        if (reboot)
            MessageBox.Show("Драйвер установлен. Перезагрузи компьютер, когда будет удобно: он заработает после перезагрузки.",
                "TrackpadClicks", MessageBoxButtons.OK, MessageBoxIcon.Information);

        int me = Environment.ProcessId;
        foreach (var other in Process.GetProcessesByName("TrackpadClicks"))
            try { if (other.Id != me) other.Kill(); } catch { }
        if (pending.RestartCiao)
        {
            string ciao = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ciao", "Ciao.exe");
            foreach (var p in Process.GetProcessesByName("Ciao"))
                try { p.Kill(true); p.WaitForExit(3000); } catch { }
            if (File.Exists(ciao)) Process.Start(new ProcessStartInfo(ciao) { UseShellExecute = true });
        }
        Process.Start(installed, $"--replace {me}");
        Environment.Exit(0);
        return null;
    }

    // ---- GitHub releases.

    const string Releases = "https://api.github.com/repos/iliyasone/trackpad-clicks/releases/latest";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    // Downloads the latest release when it is newer than this exe and than what is
    // staged; false when GitHub couldn't be asked. Never on the effects worker: it waits
    // on the network. One at a time: the tray's check may meet the timer's.
    static readonly SemaphoreSlim Fetching = new(1, 1);

    public static async Task<bool> Fetch()
    {
        if (!await Fetching.WaitAsync(0)) return true;
        try
        {
            // A driver staged by hand is installed with its exe; a release must not replace it.
            if (Check() is { Driver: not null }) return true;
            using var request = new HttpRequestMessage(HttpMethod.Get, Releases);
            request.Headers.UserAgent.ParseAdd($"TrackpadClicks/{Current}");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Log.WriteNow($"update check failed: GitHub answered {(int)response.StatusCode}");
                return false;
            }
            using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            string version = root.GetProperty("tag_name").GetString()!.TrimStart('v');
            if (!System.Version.TryParse(version, out var v) || !System.Version.TryParse(Current, out var c) || v <= c) return true;
            if (Check() is Pending staged && System.Version.TryParse(staged.Version, out var s) && s >= v) return true;
            var asset = root.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => a.GetProperty("name").GetString() == "TrackpadClicks.exe");
            if (asset.ValueKind != System.Text.Json.JsonValueKind.Object) return true;
            Directory.CreateDirectory(Dir);
            string exe = Path.Combine(Dir, "TrackpadClicks.exe");
            // The whole download, not only its start, within 5 minutes.
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
            using (var download = await Http.GetAsync(asset.GetProperty("browser_download_url").GetString(), HttpCompletionOption.ResponseHeadersRead, timeout.Token))
            using (var file = File.Create(exe + ".part"))
            {
                download.EnsureSuccessStatusCode();
                await download.Content.CopyToAsync(file, timeout.Token);
            }
            // GitHub's own SHA-256 of the asset: a cut-off or corrupt download is never staged.
            string? digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var file = File.OpenRead(exe + ".part"))
            {
                string got = "sha256:" + Convert.ToHexString(sha.ComputeHash(file)).ToLowerInvariant();
                bool sizeOk = file.Length == asset.GetProperty("size").GetInt64();
                if (!sizeOk || digest != null && !string.Equals(digest, got, StringComparison.OrdinalIgnoreCase))
                {
                    file.Close();
                    File.Delete(exe + ".part");
                    Log.WriteNow($"update {version}: the download doesn't match GitHub's size or digest; dropped");
                    return false;
                }
            }
            File.Move(exe + ".part", exe, true);
            // Release notes: the first lines after the signature block, as plain text.
            string notes = string.Join(" ", (root.GetProperty("body").GetString() ?? "").Split('\n')
                .Select(l => l.Trim().TrimStart('-', '*', ' ').Replace("**", "")).Where(l => l.Length > 0 && !l.StartsWith(">")).Take(3));
            File.WriteAllText(Manifest, JsonSerializer.Serialize(new Pending(version, notes, null), Settings.Json));
            Log.WriteNow($"update {version} downloaded from GitHub");
            return true;
        }
        catch (Exception e)
        {
            Log.WriteNow($"update check failed: {e.Message}");
            return false;
        }
        finally { Fetching.Release(); }
    }

    // At start-up: the staged exe is no longer needed once it runs from the install folder.
    public static void Clean()
    {
        try
        {
            if (Check() == null && File.Exists(Path.Combine(Dir, "TrackpadClicks.exe")))
                File.Delete(Path.Combine(Dir, "TrackpadClicks.exe"));
        }
        catch { }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace TrackpadClicks;

// Updates are never applied by themselves. scripts/stage-update.sh puts one into
// C:\TrackpadClicks\update: update.json, the new TrackpadClicks.exe, and optionally a
// driver/ folder with a Magic Trackpad driver package. It is applied only when the user
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

    public sealed record Pending(string Version, string Notes, string? Driver);

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

        var inf = Directory.Exists(Path.Combine(Dir, "driver"))
            ? Directory.GetFiles(Path.Combine(Dir, "driver"), "*.inf").FirstOrDefault() : null;
        if (pending.Driver != null && inf != null)
        {
            try
            {
                using var pnputil = Process.Start(new ProcessStartInfo("pnputil.exe", $"/add-driver \"{inf}\" /install")
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
                Log.WriteNow($"update: driver installed, pnputil exit {pnputil.ExitCode}");
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

        int me = Environment.ProcessId;
        foreach (var other in Process.GetProcessesByName("TrackpadClicks"))
            try { if (other.Id != me) other.Kill(); } catch { }
        Process.Start(installed, $"--replace {me}");
        Environment.Exit(0);
        return null;
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

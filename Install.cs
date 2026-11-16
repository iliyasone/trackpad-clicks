using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace TrackpadClicks;

// The release's TrackpadClicks.exe is its own installer. Started from anywhere but
// C:\TrackpadClicks (a download), it asks, restarts itself elevated with --install, and:
//   - installs the Magic Trackpad driver it carries (driver/, upstream v2.3) unless the
//     driver store already has that version or newer (a fork build, say); Windows 11
//     gets the Microsoft-signed package, Windows 10 the one whose catalog is signed by
//     the driver's own certificate, imported first into Trusted Root and Trusted Publishers;
//   - installs the WebView2 Runtime for the settings window when Windows lacks it;
//   - copies itself to C:\TrackpadClicks and starts it at the user's logon with highest
//     privileges (README, "Run"), and starts it now.
static class Install
{
    static string Installed => Path.Combine(Updater.InstallDir, "TrackpadClicks.exe");

    public static bool Needed =>
        !string.Equals(Environment.ProcessPath, Installed, StringComparison.OrdinalIgnoreCase);

    // Not elevated: ask, then hand over to an elevated copy for the signed-in user.
    public static void Ask()
    {
        if (MessageBox.Show(
                "Установить TrackpadClicks?\n\nОн встанет в C:\\TrackpadClicks и будет запускаться при входе в Windows. " +
                "Если нужно, установится драйвер Magic Trackpad и WebView2 для окна настроек. Windows попросит права администратора.",
                "TrackpadClicks", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                ArgumentList = { "--install", System.Security.Principal.WindowsIdentity.GetCurrent().Name },
            });
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { } // the user said no
    }

    // Elevated, for `user`: everything, then the result in a message.
    public static void Run(string user)
    {
        var done = new List<string>();
        bool reboot = false;
        try
        {
            Log.WriteNow($"install {Updater.Current} for {user}");
            reboot = Driver(done);
            WebView2(done);
            Copy();
            LogonTask(user);
            done.Add("TrackpadClicks запущен: значок трекпада в трее, щелчок по нему открывает настройки.");
        }
        catch (Exception e)
        {
            Log.WriteNow($"install failed: {e}");
            MessageBox.Show($"Установка не удалась: {e.Message}\n\nПодробности: {Log.Path}",
                "TrackpadClicks", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (reboot) done.Add("Перезагрузи компьютер: драйвер заработает после перезагрузки.");
        done.Add("В параметрах сенсорной панели Windows выключи касания: TrackpadClicks работает со щелчками.");
        MessageBox.Show(string.Join("\n\n", done), "TrackpadClicks", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---- The driver. Returns whether a restart finishes it.

    static readonly Version Bundled = new(2026, 3983, 1, 1000);

    static bool Driver(List<string> done)
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            done.Add("Драйвер Magic Trackpad для этого процессора не входит в TrackpadClicks: возьми его на github.com/vitoplantamura/MagicTrackpad2ForWindows/releases.");
            return false;
        }
        var have = StoreVersion();
        if (have >= Bundled)
        {
            Log.WriteNow($"install: driver {have} already installed");
            return false;
        }
        // The Microsoft-signed catalog covers Windows 11 22H2 (22621) and later only.
        bool win11 = Environment.OSVersion.Version.Build >= 22621;
        string package = win11 ? "win11" : "win10";
        string dir = Path.Combine(Updater.InstallDir, "driver");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var assembly = typeof(Install).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith($"driver/{package}/")))
        {
            using var resource = assembly.GetManifestResourceStream(name)!;
            using var file = File.Create(Path.Combine(dir, name[(name.LastIndexOf('/') + 1)..]));
            resource.CopyTo(file);
        }
        // Windows 10: the catalog is signed by the driver's own certificate; trusted, the
        // package installs without a prompt. The driver files themselves are Microsoft-signed.
        foreach (var cer in Directory.GetFiles(dir, "*.cer"))
            foreach (var store in new[] { StoreName.Root, StoreName.TrustedPublisher })
            {
                using var s = new X509Store(store, StoreLocation.LocalMachine);
                s.Open(OpenFlags.ReadWrite);
                using var certificate = new X509Certificate2(cer);
                s.Add(certificate);
            }
        // Lets TrackpadClicks open the driver's haptic device without admin rights too.
        using (var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\AmtPtpHidFilter\Parameters"))
            key.SetValue("AllowNonAdminAccess", 1);
        using var pnputil = Process.Start(new ProcessStartInfo("pnputil.exe", $"/add-driver \"{Path.Combine(dir, "AmtPtpDevice.inf")}\" /install")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        string output = pnputil.StandardOutput.ReadToEnd();
        pnputil.WaitForExit();
        Log.WriteNow($"install: pnputil ({package}) exit {pnputil.ExitCode}: {output.ReplaceLineEndings(" ")}");
        // 3010: installed, a restart finishes it. 259: in the store, no trackpad connected yet.
        if (pnputil.ExitCode is not (0 or 3010 or 259))
            throw new Exception($"драйвер не установился (pnputil, код {pnputil.ExitCode})");
        Directory.Delete(dir, true);
        done.Add($"Драйвер Magic Trackpad {(win11 ? "для Windows 11" : "для Windows 10")} установлен.");
        return pnputil.ExitCode == 3010;
    }

    // The newest Magic Trackpad driver in the driver store, by its INF's DriverVer.
    static Version? StoreVersion()
    {
        Version? best = null;
        string repository = Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository");
        foreach (var inf in Directory.GetDirectories(repository, "amtptpdevice.inf_*").Select(d => Path.Combine(d, "amtptpdevice.inf")).Where(File.Exists))
            foreach (var line in File.ReadLines(inf))
                if (line.TrimStart().StartsWith("DriverVer", StringComparison.OrdinalIgnoreCase)
                    && Version.TryParse(line.Split(',').Last().Trim(), out var v) && (best == null || v > best))
                    best = v;
        return best;
    }

    // ---- WebView2: Windows 11 has it; Windows 10 usually got it with Edge, not always.

    static void WebView2(List<string> done)
    {
        const string client = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        bool present = new[]
        {
            Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SOFTWARE\WOW6432Node\{client}"),
            Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"Software\{client}"),
        }.Any(k => k?.GetValue("pv") is string pv && pv != "" && pv != "0.0.0.0");
        if (present) return;
        try
        {
            string setup = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                File.WriteAllBytes(setup, http.GetByteArrayAsync("https://go.microsoft.com/fwlink/p/?LinkId=2124703").Result);
            using var run = Process.Start(setup, "/silent /install");
            run.WaitForExit();
            File.Delete(setup);
            Log.WriteNow($"install: WebView2 setup exit {run.ExitCode}");
            if (run.ExitCode == 0) done.Add("Установлен WebView2 для окна настроек.");
            else done.Add($"WebView2 для окна настроек не установился (код {run.ExitCode}); жесты работают и без него.");
        }
        catch (Exception e)
        {
            Log.WriteNow($"install: WebView2 failed: {e.Message}");
            done.Add("WebView2 для окна настроек не скачался; жесты работают и без него.");
        }
    }

    // ---- The exe: stop running copies, put this one in place.

    static void Copy()
    {
        int me = Environment.ProcessId;
        foreach (var other in Process.GetProcessesByName("TrackpadClicks"))
            try { if (other.Id != me) { other.Kill(); other.WaitForExit(3000); } } catch { }
        Directory.CreateDirectory(Updater.InstallDir);
        foreach (var old in Directory.GetFiles(Updater.InstallDir, "TrackpadClicks.old-*.exe"))
            try { File.Delete(old); } catch { }
        // A copy wedged in the kernel keeps its exe locked; a locked exe can still be renamed.
        if (File.Exists(Installed))
            File.Move(Installed, Path.Combine(Updater.InstallDir, $"TrackpadClicks.old-{DateTime.Now:yyyyMMddHHmmss}.exe"));
        File.Copy(Environment.ProcessPath!, Installed);
    }

    // The logon task, as scripts/deploy.sh makes it, and started now in the user's session.
    static void LogonTask(string user)
    {
        string u = user.Replace("'", "''");
        string script = $"""
            $ErrorActionPreference = 'Stop'
            $a = New-ScheduledTaskAction -Execute '{Installed}'
            $t = New-ScheduledTaskTrigger -AtLogOn -User '{u}'
            $p = New-ScheduledTaskPrincipal -UserId '{u}' -LogonType Interactive -RunLevel Highest
            $s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
            Register-ScheduledTask -TaskName 'TrackpadClicks' -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null
            Start-ScheduledTask -TaskName 'TrackpadClicks'
            """;
        using var ps = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -EncodedCommand {Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        })!;
        string error = ps.StandardError.ReadToEnd();
        ps.WaitForExit();
        if (ps.ExitCode != 0) throw new Exception($"задача автозапуска не создана: {error.Trim()}");
    }
}

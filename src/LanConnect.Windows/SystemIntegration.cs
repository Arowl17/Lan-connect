using System.Diagnostics;
using System.Security.Cryptography;
using LanConnect.Core;
using LanConnect.Core.Net;
using Microsoft.Win32;

namespace LanConnect.Windows;

public static class AppPaths
{
    /// <summary>%APPDATA%\LanConnect: settings and identity (roams with the user profile).</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanConnect");

    /// <summary>%LOCALAPPDATA%\LanConnect\logs.</summary>
    public static string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanConnect", "logs");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string IdentityFile => Path.Combine(DataDirectory, "identity.bin");

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LanConnect.exe");
}

/// <summary>Keeps this computer's certificate on disk, encrypted for the current Windows user (DPAPI).</summary>
public static class IdentityStore
{
    public static DeviceIdentity LoadOrCreate()
    {
        var path = AppPaths.IdentityFile;
        if (File.Exists(path))
        {
            try
            {
                var pkcs12 = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                return DeviceIdentity.FromPkcs12(pkcs12);
            }
            catch (Exception e) when (e is CryptographicException or IOException)
            {
                // A new identity means paired computers need to pair again, but the app keeps working.
                Log.Error("Stored identity unreadable; creating a new one", e);
                File.Copy(path, path + ".bad", overwrite: true);
            }
        }

        var identity = DeviceIdentity.Create();
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var protectedBytes = ProtectedData.Protect(identity.ExportPkcs12(), null, DataProtectionScope.CurrentUser);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        File.Move(temp, path, overwrite: true);
        Log.Info($"Created device identity {DeviceIdentity.ShortFingerprint(identity.Fingerprint)}");
        return identity;
    }
}

/// <summary>Start with Windows, via the per-user Run key (no admin rights needed).</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LanConnect";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Contains(AppPaths.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                key.SetValue(ValueName, $"\"{AppPaths.ExecutablePath}\" --background");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Error("Changing start-with-Windows failed", e);
        }
    }
}

/// <summary>
/// Windows Firewall rule for Lan Connect. The installer creates it; this lets the app check and
/// repair it (one admin prompt) when it was run without the installer.
/// </summary>
public static class FirewallHelper
{
    public const string RuleName = "Lan Connect";

    /// <summary>True when an inbound rule with our name exists (does not need admin rights).</summary>
    public static bool RuleExists()
    {
        try
        {
            // netsh output is localized, so rely on the exit code (non-zero when no rule matches).
            var (exitCode, output) = RunNetsh($"advfirewall firewall show rule name=\"{RuleName}\" dir=in");
            return exitCode == 0 && output.Contains(RuleName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e)
        {
            Log.Warn($"Could not query firewall rules: {e.Message}");
            return false;
        }
    }

    /// <summary>Adds (or replaces) the rule; shows one UAC prompt. Returns false if the user declined.</summary>
    public static bool AddRule()
    {
        var exe = AppPaths.ExecutablePath;
        var script =
            $"advfirewall firewall delete rule name=\"{RuleName}\" & " +
            $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{exe}\" enable=yes profile=any";
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c netsh {script}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            process?.WaitForExit(30_000);
            return process is { ExitCode: 0 };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC prompt declined
        }
    }

    private static (int ExitCode, string Output) RunNetsh(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "netsh.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(10_000);
        return (process.ExitCode, output);
    }
}

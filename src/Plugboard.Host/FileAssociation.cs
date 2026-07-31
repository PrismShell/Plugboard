using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Plugboard.Host;

// Registers the .pbapp file type as a per-user (HKCU, no admin) association whose default
// program is Plugboard: double-clicking a .pbapp runs `Plugboard.Host.exe --open "%1"`,
// which hands the file to the running host and serves it at localhost (same-origin, no key).
//
// This is part of Plugboard's setup: the host calls EnsureRegistered() on every startup so
// the association always points at the exe that is actually running (build vs dist), and
// self-heals if it drifts. Idempotent - only writes when the command differs. Toggle with
// the RegisterFileAssociation setting; remove with `Plugboard.Host.exe --unregister`.
internal static class FileAssociation
{
    private const string Ext    = ".pbapp";
    private const string ProgId = "Plugboard.App";

    public static void EnsureRegistered(string exePath, ILogger? log = null)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var wantCmd = $"\"{exePath}\" --open \"%1\"";
            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            using (var ext = classes!.CreateSubKey(Ext)) ext!.SetValue(null, ProgId);

            using var prog = classes.CreateSubKey(ProgId);
            prog!.SetValue(null, "Plugboard App");
            // Prefer the dedicated .pbapp document icon shipped next to the exe (pbapp.ico);
            // fall back to the exe's own icon. Keeps the shell verb and Register-PbappIcon.cmd
            // in agreement instead of fighting over DefaultIcon on each launch.
            var docIcon = Path.Combine(AppContext.BaseDirectory, "pbapp.ico");
            var iconValue = File.Exists(docIcon) ? docIcon : exePath + ",0";
            using (var di = prog.CreateSubKey("DefaultIcon")) di!.SetValue(null, iconValue);

            using var cmd = prog.CreateSubKey(@"shell\open\command");
            if (cmd!.GetValue(null) as string is var current &&
                !string.Equals(current, wantCmd, StringComparison.OrdinalIgnoreCase))
            {
                cmd.SetValue(null, wantCmd);
                log?.LogInformation(".pbapp association registered -> {Cmd}", wantCmd);
            }
        }
        catch (Exception ex)
        {
            log?.LogWarning("Could not register the .pbapp association: {Message}", ex.Message);
        }
    }

    public static void Unregister()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
            classes?.DeleteSubKeyTree(Ext, throwOnMissingSubKey: false);
            classes?.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
        }
        catch { /* best effort */ }
    }
}

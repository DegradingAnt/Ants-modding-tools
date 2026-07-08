using System.Diagnostics;

namespace Amt.Core;

/// Boots the game (AMT-14). A full vanilla-style launch (resolve the version manifest, build the library
/// classpath, download assets) is a large subsystem; v1 instead runs a USER-CONFIGURED launch command in the
/// instance folder and injects the signed-in session into it. That covers the real cases — a CurseForge/Prism
/// instance already has a working launch, and the pack ships its own `.uvrun/launch-world.args` java arg-file —
/// without AMT reimplementing a launcher. Placeholders in the command are substituted from the account:
///   {accessToken} {username} {uuid} {instance}
/// so an offline arg-file becomes an authenticated boot. A native manifest-based launch is a later stage.
public static class GameLauncher
{
    /// Whether we have enough to boot: a command, and an instance dir to run it in.
    public static bool CanLaunch(AppSettings s) => s.LaunchCommand.Trim().Length > 0 && s.InstancePath.Length > 0;

    /// Start the game. Returns (ok, message). Never throws — a bad command becomes a readable message, not a crash.
    public static (bool Ok, string Message) Launch(AppSettings s, Account? account)
    {
        var cmd = s.LaunchCommand.Trim();
        if (cmd.Length == 0) return (false, "No launch command set — add one in Settings → Launch.");
        if (s.InstancePath.Length == 0) return (false, "No instance folder set.");

        cmd = cmd
            .Replace("{accessToken}", account?.AccessToken ?? "")
            .Replace("{username}", account?.Username ?? "Player")
            .Replace("{uuid}", account?.Uuid ?? "")
            .Replace("{instance}", s.InstancePath);

        // split the command into exe + args on the first space, honouring a quoted exe path
        var (exe, args) = SplitCommand(cmd);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = s.InstancePath,
                UseShellExecute = true,   // let the OS resolve a .bat/.exe/associated launcher
            };
            Process.Start(psi);
            return (true, $"Launched — {account?.Username ?? "offline"}");
        }
        catch (Exception ex)
        {
            return (false, $"Couldn't launch: {ex.Message}");
        }
    }

    // "C:\path with spaces\java.exe" @args  ->  (exe, "@args"). An unquoted first token splits on the first space.
    private static (string Exe, string Args) SplitCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            var close = cmd.IndexOf('"', 1);
            if (close > 0) return (cmd[1..close], cmd[(close + 1)..].Trim());
        }
        var sp = cmd.IndexOf(' ');
        return sp < 0 ? (cmd, "") : (cmd[..sp], cmd[(sp + 1)..].Trim());
    }
}

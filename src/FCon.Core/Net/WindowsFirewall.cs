using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace FCon.Core.Net;

/// <summary>What Windows Defender Firewall says about the programs we care about.</summary>
public sealed record FirewallFinding(
    bool Enabled,
    /// <summary>The active profile blocks outbound traffic unless a rule allows it.</summary>
    bool DefaultOutboundBlocked,
    IReadOnlyList<string> BlockRules,
    IReadOnlyList<string> AllowRules,
    string Profiles)
{
    /// <summary>True when the firewall as configured would stop an outbound connection.</summary>
    public bool WouldBlockOutbound => Enabled && (BlockRules.Count > 0 || (DefaultOutboundBlocked && AllowRules.Count == 0));
}

/// <summary>
/// Reads the firewall through its COM policy object and adds allow rules through an
/// elevated <c>netsh</c>. Read-only unless the user agrees to the UAC prompt.
/// </summary>
public static class WindowsFirewall
{
    private const int ActionBlock = 0;
    private const int DirectionOut = 2;

    /// <summary>
    /// Inspect the active profiles for rules touching <paramref name="programs"/>.
    /// Null when the firewall service cannot be queried (disabled service, no COM).
    /// </summary>
    public static FirewallFinding? Inspect(IReadOnlyList<string> programs)
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null) return null;
            var policy = Activator.CreateInstance(type);
            if (policy is null) return null;

            var current = Convert.ToInt32(Get(policy, "CurrentProfileTypes"));
            var enabled = false;
            var defaultBlocked = false;
            var names = new List<string>();
            foreach (var (flag, name) in new[] { (1, "domain"), (2, "private"), (4, "public") })
            {
                if ((current & flag) == 0) continue;
                names.Add(name);
                if (Convert.ToBoolean(Get(policy, "FirewallEnabled", flag))) enabled = true;
                if (Convert.ToInt32(Get(policy, "DefaultOutboundAction", flag)) == ActionBlock) defaultBlocked = true;
            }

            var wanted = programs.Select(Normalise).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var block = new List<string>();
            var allow = new List<string>();

            if (Get(policy, "Rules") is IEnumerable rules)
            {
                foreach (var rule in rules)
                {
                    if (rule is null) continue;
                    if (!Convert.ToBoolean(Get(rule, "Enabled"))) continue;
                    if (Convert.ToInt32(Get(rule, "Direction")) != DirectionOut) continue;
                    if (Get(rule, "ApplicationName") is not string app || app.Length == 0) continue;
                    if (!wanted.Contains(Normalise(app))) continue;

                    var ruleProfiles = Convert.ToInt32(Get(rule, "Profiles"));
                    if ((ruleProfiles & current) == 0) continue;

                    var label = $"{Get(rule, "Name")} ({Path.GetFileName(app)})";
                    if (Convert.ToInt32(Get(rule, "Action")) == ActionBlock) block.Add(label);
                    else allow.Add(label);
                }
            }

            return new FirewallFinding(enabled, defaultBlocked, block, allow, string.Join("+", names));
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or InvalidCastException
                                       or MissingMemberException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Add outbound (and optionally inbound) allow rules for the programs, through one
    /// elevated netsh run. False when the user declined the prompt or netsh failed.
    /// </summary>
    public static bool TryAllow(IReadOnlyList<(string Name, string Program)> programs, bool inbound)
    {
        var commands = new List<string>();
        foreach (var (name, program) in programs)
        {
            commands.Add($"netsh advfirewall firewall add rule name=\"{name}\" dir=out action=allow program=\"{program}\" enable=yes profile=any");
            if (inbound)
                commands.Add($"netsh advfirewall firewall add rule name=\"{name}\" dir=in action=allow program=\"{program}\" enable=yes profile=any");
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + string.Join(" && ", commands))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null) return false;
            process.WaitForExit(30000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // ERROR_CANCELLED: the user said no at the UAC prompt.
            return false;
        }
    }

    private static object? Get(object target, string property, params object[] index) =>
        target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, index.Length == 0 ? null : index);

    private static string Normalise(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}

using System.Diagnostics;
using FCon.Abstractions.Plugins;

namespace FCon.Core.Engine;

public sealed record EngineInfo(EngineKind Kind, string ExecutablePath, string? Version)
{
    public string FileName => Path.GetFileName(ExecutablePath);
}

/// <summary>
/// Finds the engine binaries. Looks beside the app first so a portable install works,
/// then falls back to PATH for developers who already have the cores installed.
/// </summary>
public static class EngineLocator
{
    public static string ExecutableName(EngineKind kind) => kind switch
    {
        EngineKind.Xray => "xray.exe",
        _ => "sing-box.exe",
    };

    public static string DirectoryName(EngineKind kind) => kind switch
    {
        EngineKind.Xray => "xray",
        _ => "sing-box",
    };

    /// <summary>
    /// Overrides where cores are looked up. Useful for a portable install that keeps its
    /// binaries elsewhere, and for tools that do not live beside the app executable.
    /// </summary>
    public const string DirectoryOverrideVariable = "FCON_ENGINES_DIR";

    public static string? Locate(EngineKind kind)
    {
        var exe = ExecutableName(kind);
        var candidates = new List<string>();

        if (Environment.GetEnvironmentVariable(DirectoryOverrideVariable) is { Length: > 0 } overrideDir)
        {
            candidates.Add(Path.Combine(overrideDir, DirectoryName(kind), exe));
            candidates.Add(Path.Combine(overrideDir, exe));
        }

        candidates.Add(Path.Combine(AppPaths.EnginesDirectory, DirectoryName(kind), exe));
        candidates.Add(Path.Combine(AppPaths.EnginesDirectory, exe));
        candidates.Add(Path.Combine(AppPaths.BaseDirectory, exe));

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return FindOnPath(exe);
    }

    public static EngineInfo? Describe(EngineKind kind)
    {
        var path = Locate(kind);
        return path is null ? null : new EngineInfo(kind, path, ReadVersion(path));
    }

    /// <summary>Both cores print their version on <c>version</c>; the first line is enough.</summary>
    public static string? ReadVersion(string executablePath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executablePath, "version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(4000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            var line = output.ReplaceLineEndings("\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            return line?.Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // PATH entries are user-editable and occasionally contain illegal characters.
            }
        }
        return null;
    }
}

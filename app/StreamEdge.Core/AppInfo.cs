// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;

namespace StreamEdge;

/// <summary>What the About window says about this build.</summary>
public static class AppInfo
{
    public const string RepositoryUrl = "https://github.com/Ixitxachitl/stream-edge";
    public const string License = "GPL-3.0-or-later";

    private static readonly Assembly Assembly = typeof(AppInfo).Assembly;

    /// <summary>"0.1.3+b9e2e69": the release and, when built from git, the
    /// commit (see Directory.Build.props).</summary>
    public static string FullVersion { get; } =
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetName().Version?.ToString(3)
        ?? "unknown";

    /// <summary>"0.1.3": the release alone.</summary>
    public static string Version => FullVersion.Split('+')[0];

    /// <summary>The commit the build came from, or null.</summary>
    public static string? Commit => FullVersion.Split('+') is [_, var hash, ..] && hash.Length > 0 ? hash : null;

    public static string Copyright { get; } =
        Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";

    /// <summary>".NET 8.0.11 · Windows 10.0.26200 · x64".</summary>
    public static string Runtime =>
        $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
}

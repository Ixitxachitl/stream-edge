// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace StreamEdge.Tests;

public class AppInfoTests
{
    [Fact]
    public void VersionIsTheOneDeclaredForTheBuild()
    {
        // Walk up from the test binaries to the repo's Directory.Build.props.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var declared = XDocument.Load(Path.Combine(dir.FullName, "Directory.Build.props"))
            .Descendants("VersionPrefix").First().Value;

        Assert.Equal(declared, AppInfo.Version);
        Assert.StartsWith(declared, AppInfo.FullVersion);
    }

    [Fact]
    public void CommitIsAShortHashWhenBuiltFromGit()
    {
        if (AppInfo.Commit is { } commit) Assert.Matches(new Regex("^[0-9a-f]{7,40}$"), commit);
    }

    [Fact]
    public void RuntimeNamesDotNetAndTheArchitecture()
    {
        Assert.Contains(".NET", AppInfo.Runtime);
        Assert.Contains(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), AppInfo.Runtime);
    }
}

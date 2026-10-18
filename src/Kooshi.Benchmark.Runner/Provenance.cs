using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kooshi.Benchmark.Runner;

public record SourceProvenance(string? Commit, string? Branch, bool? Dirty, string Source, string? Warning);

public static class Provenance
{
    private static string? Git(string root, params string[] arguments)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo("git") { WorkingDirectory = root,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
            process.StartInfo.ArgumentList.Add("-C");
            process.StartInfo.ArgumentList.Add(root);
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(5000)) { process.Kill(); return null; }
            return process.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }

    public static SourceProvenance Capture(string root, string? buildRevision = null, string? buildBranch = null, string? buildDirty = null)
    {
        var commit = Git(root, "rev-parse", "--verify", "HEAD");
        if (ValidSha(commit))
        {
            var branch = Git(root, "symbolic-ref", "--quiet", "--short", "HEAD");
            var status = Git(root, "status", "--porcelain", "--untracked-files=all");
            return new(commit, string.IsNullOrWhiteSpace(branch) ? null : branch, status is null ? null : status.Length > 0,
                "git", status is null ? "Git working-tree status unavailable." : null);
        }
        buildRevision ??= Environment.GetEnvironmentVariable("KOOSHI_BUILD_REVISION");
        buildBranch ??= Environment.GetEnvironmentVariable("KOOSHI_BUILD_BRANCH");
        buildDirty ??= Environment.GetEnvironmentVariable("KOOSHI_BUILD_DIRTY");
        if (ValidSha(buildRevision) && bool.TryParse(buildDirty, out var dirty))
            return new(buildRevision, string.IsNullOrWhiteSpace(buildBranch) ? null : buildBranch, dirty,
                "build", "Build revision was supplied externally; verify it against the build context at image creation.");
        return new(null, null, null, "unavailable", "Git metadata and a complete verified build revision were unavailable.");
    }

    public static bool ValidSha(string? value) => value is not null && Regex.IsMatch(value, "^[0-9a-fA-F]{40}$");

    public static string ConfigurationHash(object options, object? pilot, object apiSettings, object order)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { options, pilot, apiSettings, order }, Benchmark.Json);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class LocalCommandTools
{
    private const int MaximumCommands = 20;
    private const int MaximumCommandNameLength = 64;
    private const int MaximumOutputCharacters = 8 * 1024;
    private static readonly TimeSpan PackageQueryTimeout = TimeSpan.FromSeconds(4);
    private static readonly Regex ValidCommandName = new(@"^[a-zA-Z0-9_][a-zA-Z0-9_.+-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex PackageOwnerLine = new(@"^(.+):\s+", RegexOptions.CultureInvariant);

    [McpServerTool(Name = "check_local_commands", ReadOnly = true, Destructive = false, OpenWorld = false), Description("Checks whether named commands are executable on this Linux MCP server's PATH. It does not run the commands. Where possible, it identifies the owning installed Debian package and its installed version using the local dpkg database. This does not report packages merely available from APT repositories.")]
    public static async Task<string> CheckLocalCommands(
        [Description("Command names to check, without paths or arguments. For example: [\"ss\", \"netstat\", \"lsof\", \"nmap\"]. Provide 1-20 names.")] string[] commands)
    {
        if (!OperatingSystem.IsLinux())
        {
            return "This availability check is supported only when the MCP server is running on Linux. It checks the server host, not the MCP client machine.";
        }

        if (commands is null || commands.Length is 0 or > MaximumCommands)
        {
            return $"Invalid command list. Provide 1-{MaximumCommands} command names.";
        }

        var results = new List<string>(commands.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? command in commands)
        {
            if (string.IsNullOrWhiteSpace(command) || command.Length > MaximumCommandNameLength || !ValidCommandName.IsMatch(command))
            {
                results.Add($"Invalid command name: '{LimitForDisplay(command)}'. Supply a bare command name only; paths, arguments, and shell syntax are not accepted.");
                continue;
            }

            if (!seen.Add(command))
            {
                continue;
            }

            string? path = FindExecutableOnPath(command);
            if (path is null)
            {
                results.Add($"{command}: not found as an executable on the MCP server's current PATH. This does not prove that a similarly named package is unavailable to install.");
                continue;
            }

            string packageInfo = await GetPackageOwnershipAsync(path);
            results.Add($"{command}: executable found at {path}. {packageInfo}");
        }

        return "Local command availability (checked on the MCP server host; commands were not executed):\n" + string.Join("\n", results);
    }

    [SupportedOSPlatform("linux")]
    private static string? FindExecutableOnPath(string command)
    {
        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathValue))
        {
            return null;
        }

        foreach (string directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Path.IsPathFullyQualified(directory))
            {
                // Ignore relative PATH entries, including empty/current-directory entries.
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.Combine(directory, command);
                if (!File.Exists(candidate))
                {
                    continue;
                }

                UnixFileMode mode = File.GetUnixFileMode(candidate);
                if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                {
                    continue;
                }
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (ArgumentException)
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    [SupportedOSPlatform("linux")]
    private static async Task<string> GetPackageOwnershipAsync(string executablePath)
    {
        string? dpkgQuery = FindExecutableOnPath("dpkg-query");
        if (dpkgQuery is null)
        {
            return "Package owner/version: unknown because dpkg-query is not available on PATH.";
        }

        ProcessResult ownerResult = await RunDpkgQueryAsync(dpkgQuery, "-S", executablePath);
        if (ownerResult.TimedOut)
        {
            return "Package owner/version: unknown because the local dpkg ownership query timed out.";
        }

        if (ownerResult.ExitCode != 0)
        {
            return "Package owner/version: not identified by the local dpkg database (the executable may be manually installed, supplied outside dpkg, or owned by a package with a different path).";
        }

        var packages = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in ownerResult.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Match match = PackageOwnerLine.Match(line);
            if (!match.Success)
            {
                continue;
            }

            // dpkg-query -S may print multiple owners separated by commas.
            foreach (string package in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Regex.IsMatch(package, @"^[a-zA-Z0-9][a-zA-Z0-9+.-]*(?::[a-zA-Z0-9_-]+)?$", RegexOptions.CultureInvariant))
                {
                    packages.Add(package);
                }
            }
        }

        if (packages.Count == 0)
        {
            return "Package owner/version: not identified by the local dpkg database.";
        }

        var installedPackages = new List<string>();
        foreach (string package in packages.Take(5))
        {
            ProcessResult versionResult = await RunDpkgQueryAsync(
                dpkgQuery,
                "-W",
                "-f=${db:Status-Abbrev}\t${binary:Package}\t${Version}",
                package);

            if (versionResult.TimedOut)
            {
                installedPackages.Add($"{package}: version query timed out");
                continue;
            }

            if (versionResult.ExitCode != 0)
            {
                installedPackages.Add($"{package}: package version unavailable from dpkg");
                continue;
            }

            string[] fields = versionResult.StandardOutput.Trim().Split('\t');
            if (fields.Length >= 3 && fields[0].StartsWith("ii", StringComparison.Ordinal))
            {
                installedPackages.Add($"{fields[1]} version {fields[2]} (installed; local dpkg database)");
            }
            else
            {
                installedPackages.Add($"{package}: dpkg does not report it as installed");
            }
        }

        if (packages.Count > 5)
        {
            installedPackages.Add("Additional package owners omitted.");
        }

        return "Package owner/version: " + string.Join("; ", installedPackages) + ".";
    }

    private static async Task<ProcessResult> RunDpkgQueryAsync(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty, true);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new ProcessResult(-1, string.Empty, false);
        }

        Task<string> stdoutTask = ReadBoundedAsync(process.StandardOutput, MaximumOutputCharacters, () => TryKill(process));
        Task<string> stderrTask = ReadBoundedAsync(process.StandardError, 2 * 1024, () => TryKill(process));

        using var timeout = new CancellationTokenSource(PackageQueryTimeout);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            TryKill(process);
            await process.WaitForExitAsync();
        }

        string standardOutput = await stdoutTask;
        _ = await stderrTask;
        return new ProcessResult(timedOut ? -1 : process.ExitCode, standardOutput, timedOut);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters, Action onLimitReached)
    {
        var output = new StringBuilder(Math.Min(maximumCharacters, 1024));
        var buffer = new char[1024];
        bool limitReached = false;

        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory());
            if (read == 0)
            {
                break;
            }

            int remaining = maximumCharacters - output.Length;
            if (remaining > 0)
            {
                output.Append(buffer, 0, Math.Min(remaining, read));
            }

            if (read > remaining && !limitReached)
            {
                limitReached = true;
                onLimitReached();
            }
        }

        return output.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill request.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The child is already exiting or cannot be signalled.
        }
    }

    private static string LimitForDisplay(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(empty)";
        }

        return value.Length <= 64 ? value : value[..64] + "…";
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, bool TimedOut);
}

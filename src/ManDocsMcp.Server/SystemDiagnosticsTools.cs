using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Runtime.Versioning;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class SystemDiagnosticsTools
{
    private const int MaximumOutputCharacters = 24 * 1024;
    private const int MaximumErrorCharacters = 2 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [McpServerTool(Name = "get_system_diagnostics", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("On request, runs the fixed inxi diagnostic report on the Linux MCP server host. It does not install software or use sudo. The report may reveal detailed hardware and configuration information; -z enables inxi privacy filtering, but review the returned data before sharing it.")]
    public static async Task<string> GetSystemDiagnostics()
    {
        if (!OperatingSystem.IsLinux())
        {
            return "System diagnostics are supported only when the MCP server is running on Linux. This checks the server host, not the MCP client machine.";
        }

        string? inxi = FindExecutableOnPath("inxi");
        if (inxi is null)
        {
            return "The inxi executable was not found on the MCP server's current PATH. No software was installed. Use check_local_commands to verify availability, or install inxi yourself if you choose.";
        }

        ProcessResult result = await RunInxiAsync(inxi);
        if (result.TimedOut)
        {
            return "The inxi diagnostic timed out after 15 seconds. No sudo or installation was attempted.";
        }

        if (result.OutputLimitReached)
        {
            return "The inxi diagnostic exceeded the output limit and was stopped. The partial report is omitted to avoid presenting incomplete diagnostics as complete.";
        }

        if (result.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? string.Empty
                : $" Diagnostic message: {LimitForDisplay(result.StandardError.Trim(), MaximumErrorCharacters)}";
            return $"inxi exited with status {result.ExitCode}; no diagnostic report was returned.{detail}";
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return "inxi completed successfully but returned no report.";
        }

        return "Optional system diagnostics from the Linux MCP server host (inxi -ezxx; expanded report, privacy filter enabled). This describes the server host/session, not necessarily the MCP client or the user's usual desktop. The -z filter reduces some identifying details but is not a guarantee that all sensitive information is removed; review before sharing.\n\n" + result.StandardOutput.Trim();
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
                continue;
            }

            try
            {
                string candidate = Path.Combine(directory, command);
                if (!File.Exists(candidate))
                {
                    continue;
                }

                UnixFileMode mode = File.GetUnixFileMode(candidate);
                if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                {
                    return candidate;
                }
            }
            catch (IOException)
            {
                // Continue searching the remaining PATH entries.
            }
            catch (UnauthorizedAccessException)
            {
                // Continue searching the remaining PATH entries.
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static async Task<ProcessResult> RunInxiAsync(string executable)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Fixed diagnostic options: expanded hardware/software context, inxi's
        // privacy filter, extra detail, plain output, and non-IRC/TTY mode.
        foreach (string argument in new[] { "-ezxx", "--color", "0", "--tty" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty, string.Empty, false, false);
            }
        }
        catch (Win32Exception)
        {
            return new ProcessResult(-1, string.Empty, string.Empty, false, false);
        }

        bool outputLimitReached = false;
        Task<string> stdoutTask = ReadBoundedAsync(
            process.StandardOutput,
            MaximumOutputCharacters,
            () =>
            {
                outputLimitReached = true;
                TryKill(process);
            });
        Task<string> stderrTask = ReadBoundedAsync(
            process.StandardError,
            MaximumErrorCharacters,
            () => TryKill(process));

        bool timedOut = false;
        using var timeout = new CancellationTokenSource(Timeout);
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

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        int exitCode = process.HasExited ? process.ExitCode : -1;
        return new ProcessResult(exitCode, stdout, stderr, timedOut, outputLimitReached);
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
        catch (Win32Exception)
        {
            // The child is already exiting or cannot be signalled.
        }
    }

    private static string LimitForDisplay(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : value[..maximumCharacters] + "…";

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut, bool OutputLimitReached);
}

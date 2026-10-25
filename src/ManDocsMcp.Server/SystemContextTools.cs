using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class SystemContextTools
{
    private static readonly string[] OsReleaseFields =
    [
        "PRETTY_NAME",
        "ID",
        "VERSION_ID",
        "VERSION_CODENAME",
        "ID_LIKE"
    ];

    [McpServerTool(Name = "get_system_context", ReadOnly = true, Destructive = false, OpenWorld = false),
     Description("Returns a small, read-only summary of the local operating system, Ubuntu release fields, Linux kernel, and desktop session when available. Desktop information is inferred from the server process environment and may be unavailable.")]
    public static async Task<string> GetSystemContext()
    {
        var result = new StringBuilder("Local system context\n");

        if (OperatingSystem.IsLinux())
        {
            Dictionary<string, string> osRelease = ReadOsRelease();
            AppendField(result, "OS", osRelease, "PRETTY_NAME");
            AppendField(result, "Distribution ID", osRelease, "ID");
            AppendField(result, "Version", osRelease, "VERSION_ID");
            AppendField(result, "Codename", osRelease, "VERSION_CODENAME");
            AppendField(result, "ID-like distributions", osRelease, "ID_LIKE");

            string desktop = GetDesktopSession();
            result.Append("Desktop session: ").AppendLine(desktop);

            string kernel = await GetKernelVersionAsync();
            result.Append("Kernel: ").AppendLine(kernel);
        }
        else
        {
            result.Append("Operating system: ").AppendLine(Environment.OSVersion.ToString());
            result.AppendLine("Distribution release, desktop session, and Linux kernel details are only detected on Linux.");
        }

        result.AppendLine("Note: the desktop session is inferred from environment variables inherited by the MCP server; it identifies the session when available, not necessarily the installed Ubuntu flavor.");
        return result.ToString().TrimEnd();
    }

    private static Dictionary<string, string> ReadOsRelease()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            foreach (string line in File.ReadLines("/etc/os-release"))
            {
                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                string key = line[..separator];
                if (OsReleaseFields.Contains(key, StringComparer.Ordinal))
                {
                    values[key] = ParseOsReleaseValue(line[(separator + 1)..].Trim());
                }
            }
        }
        catch (IOException)
        {
            // Return the system fields that can still be collected.
        }
        catch (UnauthorizedAccessException)
        {
            // Return the system fields that can still be collected.
        }

        return values;
    }

    private static string ParseOsReleaseValue(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1];
        }

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            var decoded = new StringBuilder(value.Length - 2);
            for (int i = 1; i < value.Length - 1; i++)
            {
                char current = value[i];
                if (current == '\\' && i + 1 < value.Length - 1 && value[i + 1] is '\\' or '"' or '$' or '`')
                {
                    current = value[++i];
                }

                decoded.Append(current);
            }

            return decoded.ToString();
        }

        return value;
    }

    private static string GetDesktopSession()
    {
        string? currentDesktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        if (!string.IsNullOrWhiteSpace(currentDesktop))
        {
            string[] desktops = currentDesktop
                .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeDesktopName)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (desktops.Length > 0)
            {
                return string.Join(", ", desktops);
            }
        }

        string? sessionDesktop = Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP");
        if (!string.IsNullOrWhiteSpace(sessionDesktop))
        {
            return NormalizeDesktopName(sessionDesktop.Trim());
        }

        if (string.Equals(Environment.GetEnvironmentVariable("KDE_FULL_SESSION"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return "KDE Plasma (detected from KDE_FULL_SESSION)";
        }

        return "Unavailable (no desktop-session environment detected)";
    }

    private static string NormalizeDesktopName(string name) =>
        string.Equals(name, "KDE", StringComparison.OrdinalIgnoreCase) ? "KDE Plasma" : name;

    private static void AppendField(StringBuilder output, string label, IReadOnlyDictionary<string, string> values, string key)
    {
        output.Append(label).Append(": ")
            .AppendLine(values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : "Unavailable");
    }

    private static async Task<string> GetKernelVersionAsync()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "uname",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-sr");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return "Unavailable (could not start uname)";
            }
        }
        catch (Win32Exception)
        {
            return "Unavailable (uname was not found)";
        }

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the timeout and kill request.
            }

            return "Unavailable (uname timed out)";
        }

        string output = (await outputTask).Trim();
        return process.ExitCode == 0 && output.Length > 0
            ? output
            : "Unavailable (uname did not return a kernel version)";
    }
}

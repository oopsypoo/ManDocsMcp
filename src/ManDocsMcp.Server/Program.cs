using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

var transport = "stdio";
var transportOptionSeen = false;
var serverArgs = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    var argument = args[i];

    if (argument.Equals("--transport", StringComparison.OrdinalIgnoreCase))
    {
        if (transportOptionSeen || i + 1 >= args.Length)
        {
            throw new ArgumentException("Specify the transport once as '--transport stdio' or '--transport http'.");
        }

        transport = args[++i].ToLowerInvariant();
        transportOptionSeen = true;
        continue;
    }

    if (argument.StartsWith("--transport=", StringComparison.OrdinalIgnoreCase))
    {
        if (transportOptionSeen)
        {
            throw new ArgumentException("Specify the transport only once.");
        }

        transport = argument["--transport=".Length..].ToLowerInvariant();
        transportOptionSeen = true;
        continue;
    }

    serverArgs.Add(argument);
}

switch (transport)
{
    case "stdio":
        await RunStdioServerAsync(serverArgs.ToArray());
        break;
    case "http":
        await RunHttpServerAsync(serverArgs.ToArray());
        break;
    default:
        throw new ArgumentException($"Unknown transport '{transport}'. Use 'stdio' or 'http'.");
}

static async Task RunStdioServerAsync(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    // MCP stdio uses stdout for protocol messages. Keep all logs on stderr.
    builder.Logging.AddConsole(options =>
    {
        options.LogToStandardErrorThreshold = LogLevel.Trace;
    });

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    await builder.Build().RunAsync();
}

static async Task RunHttpServerAsync(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);
    const string defaultUrl = "http://127.0.0.1:8080";

    // Safe default: HTTP is loopback-only unless the operator explicitly supplies
    // a specific LAN address with --urls. Wildcard binds are rejected below.
    var configuredUrls = builder.Configuration["urls"];
    if (string.IsNullOrWhiteSpace(configuredUrls))
    {
        configuredUrls = defaultUrl;
        builder.WebHost.UseUrls(defaultUrl);
    }

    var allowedHosts = GetExplicitListenHosts(configuredUrls);

    builder.Services
        .AddMcpServer()
        .WithHttpTransport(options =>
        {
            // These tools are request/response only and need no per-client session state.
            options.SessionMode = HttpServerSessionMode.Stateless;
        })
        .WithToolsFromAssembly();

    var app = builder.Build();

    // Allow only Host values matching the explicitly configured listen address.
    // If a browser sends Origin, accept only same-origin requests; native MCP
    // clients normally omit Origin. CORS is intentionally not enabled.
    app.Use(async (context, next) =>
    {
        var requestHost = context.Request.Host.Host;
        if (!allowedHosts.Contains(requestHost, StringComparer.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Host is not allowed.");
            return;
        }

        if (context.Request.Headers.TryGetValue("Origin", out var originHeader))
        {
            var originText = originHeader.ToString();
            var expectedPort = context.Request.Host.Port ?? (context.Request.IsHttps ? 443 : 80);
            var validOrigin = Uri.TryCreate(originText, UriKind.Absolute, out var origin)
                && (origin.Scheme == Uri.UriSchemeHttp || origin.Scheme == Uri.UriSchemeHttps)
                && origin.Scheme.Equals(context.Request.Scheme, StringComparison.OrdinalIgnoreCase)
                && origin.Host.Equals(requestHost, StringComparison.OrdinalIgnoreCase)
                && origin.Port == expectedPort;

            if (!validOrigin)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Origin is not allowed.");
                return;
            }
        }

        await next(context);
    });

    app.MapMcp("/mcp");
    await app.RunAsync();
}

static string[] GetExplicitListenHosts(string urls)
{
    var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var address in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"Invalid HTTP listen address '{address}'. Use an explicit address such as http://127.0.0.1:8080 or http://192.168.1.131:8080.");
        }

        var host = uri.Host;
        if (host is "*" or "+" or "0.0.0.0" or "::" or "[::]")
        {
            throw new ArgumentException("Wildcard HTTP binds are disabled. Bind to loopback or to a specific LAN IP address.");
        }

        hosts.Add(host);
    }

    if (hosts.Count == 0)
    {
        throw new ArgumentException("Configure at least one explicit HTTP listen address.");
    }

    return hosts.ToArray();
}

[McpServerToolType]
public static class DiagnosticTools
{
    [McpServerTool, Description("Basic smoke test confirming that the ManDocsMcp server is reachable.")]
    public static string Ping() => "ManDocsMcp server is running.";
}

# ManDocsMcp

A small, cross-platform MCP server for local and online Ubuntu manual pages. It supports local **stdio** and optional **Streamable HTTP** transports, and targets **.NET 10**. It is intended to be built on Windows and run on a Kubuntu/Linux machine.

## Solution layout

```text
ManDocsMcp/
├── ManDocsMcp.slnx
├── README.md
└── src/
    └── ManDocsMcp.Server/
        ├── ManDocsMcp.Server.csproj
        └── Program.cs
```

## Current state

- MCP server host using the official C# SDK. `stdio` is the default transport; opt into Streamable HTTP with `--transport http`.
- HTTP mode serves the MCP endpoint at `/mcp`, defaults to loopback on port `8080`, and requires an explicit, non-wildcard listen address for LAN use.
- HTTP mode validates the request `Host` against the configured listen address and rejects cross-origin browser requests. It does not provide authentication; restrict LAN access with your firewall.
- Read-only local tools: `search_man`, `get_man_page`, and `get_system_context`.
- Read-only online manpage tools: `search_ubuntu_manpages` and `get_ubuntu_manpage`, using the official Ubuntu Manpage Repository.
- Read-only general documentation tools: `search_ubuntu_docs` and `get_ubuntu_doc`, using official Ubuntu Desktop, Server, Core, WSL, and Ubuntu Project documentation.
- A harmless `ping` tool for connection testing.
- No arbitrary shell-command tool.

## Build in Visual Studio on Windows

Open `ManDocsMcp.slnx` in Visual Studio, restore NuGet packages, and build the solution. Or use a terminal from the solution directory:

```powershell
dotnet restore .\ManDocsMcp.slnx
dotnet build .\ManDocsMcp.slnx --configuration Release
```

## Publish for the Linux machine

Publish a framework-dependent output from Windows:

```powershell
dotnet publish .\src\ManDocsMcp.Server\ManDocsMcp.Server.csproj `
  --configuration Release `
  --output .\artifacts\publish
```

Copy the **contents** of `artifacts/publish` to a directory on the Kubuntu machine. This portable output is launched with `dotnet` rather than a Windows `.exe`:

```bash
cd /path/to/ManDocsMcp
dotnet ManDocsMcp.Server.dll
```

The Linux machine needs the **.NET 10 ASP.NET Core runtime** installed (check with `dotnet --list-runtimes` for `Microsoft.AspNetCore.App 10.x`). The server writes MCP protocol messages to standard output and logs to standard error. Normally, configure your MCP client to launch it with command `dotnet` and the full path to `ManDocsMcp.Server.dll` as its argument; use the client-specific configuration format.

> The server will wait for MCP messages on standard input when run directly. Use an MCP client (or an MCP inspector) to test it; it is not a terminal user interface.

## Run as a standalone LAN HTTP server

HTTP mode uses Streamable HTTP at `/mcp`. By default, it listens only on `127.0.0.1:8080`, so it is not LAN-accessible until you explicitly bind it to a specific address assigned to the Kubuntu machine:

```bash
dotnet ManDocsMcp.Server.dll --transport http --urls http://192.168.1.131:8080
```

Replace `192.168.1.131` with the machine's current LAN IP. In your MCP client, configure the HTTP endpoint as `http://192.168.1.131:8080/mcp`. Keep the process running while clients use it; stop it with `Ctrl+C`.

> **Security:** Do not expose this unauthenticated HTTP server to the public internet or configure router port forwarding. Allow port `8080` through the Kubuntu firewall only for trusted LAN devices. The server deliberately rejects wildcard binds such as `0.0.0.0`; bind to a specific LAN address instead. Plain HTTP is unencrypted, so use it only on a trusted network.

The existing `stdio` setup remains unchanged: omit `--transport http` (or use `--transport stdio`) when your client should launch the server as a child process.

## Online Ubuntu documentation

The online tools use HTTPS and return results from official Ubuntu sources. Manpages are release-specific. General product guides are searched from the published Sphinx indexes for Ubuntu Desktop, Server, Core, WSL, and Ubuntu Project; these guides are often rolling/latest documentation and are not automatically matched to the local OS release.

- `search_ubuntu_manpages(query, release?, maxResults?)` searches a release and returns the repository's JSON result data and source link.
- `get_ubuntu_manpage(pageName, section, release?)` retrieves a plain-text page, with a 64 KiB response limit.
- `search_ubuntu_docs(query, product?, maxResults?)` searches general guides. `product` can be `desktop`, `server`, `core`, `wsl`, `project`, or `all`.
- `get_ubuntu_doc(product, documentPath)` fetches a page using a product key and relative path returned by `search_ubuntu_docs`; arbitrary URLs are not accepted. Page text is limited to 96 KiB.

The general-documentation tools search only the official product documentation sets listed above. Other Ubuntu and Canonical product documentation can be added to the allowlist in a later update.

## SDK

- MCP C# SDK: <https://github.com/modelcontextprotocol/csharp-sdk>
- Getting started: <https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/getting-started.md>

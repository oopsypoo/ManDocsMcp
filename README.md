# ManDocsMcp

A small, cross-platform MCP server for local and online Ubuntu manual pages. It currently uses **stdio only** and targets **.NET 10**. It is intended to be built on Windows, copied to a Kubuntu/Linux machine, and launched by an MCP client there.

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

- MCP server host using the official C# SDK, with local `stdio` transport.
- Read-only local tools: `search_man`, `get_man_page`, and `get_system_context`.
- Read-only online tools: `search_ubuntu_manpages` and `get_ubuntu_manpage`, using the official Ubuntu Manpage Repository.
- A harmless `ping` tool for connection testing.
- No arbitrary shell-command tool and no LAN/HTTP listener yet.

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

The Linux machine needs the **.NET 10 runtime** installed. The server writes MCP protocol messages to standard output and logs to standard error. Normally, configure your MCP client to launch it with command `dotnet` and the full path to `ManDocsMcp.Server.dll` as its argument; use the client-specific configuration format.

> The server will wait for MCP messages on standard input when run directly. Use an MCP client (or an MCP inspector) to test it; it is not a terminal user interface.

## Online Ubuntu manpages

The online tools query the official Ubuntu Manpage Repository over HTTPS. If `release` is omitted, the server reads `UBUNTU_CODENAME` or `VERSION_CODENAME` from `/etc/os-release`; otherwise, supply a release codename explicitly. Online results are separate from the locally installed man pages and can reflect a different package version.

- `search_ubuntu_manpages(query, release?, maxResults?)` searches a release and returns the repository's JSON result data and source link.
- `get_ubuntu_manpage(pageName, section, release?)` retrieves a plain-text page, with a 64 KiB response limit.

General Ubuntu product documentation and LAN/HTTP transport are not implemented yet; they can be added as separate features after these online manpage tools are tested.

## SDK

- MCP C# SDK: <https://github.com/modelcontextprotocol/csharp-sdk>
- Getting started: <https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/getting-started.md>

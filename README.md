# ManDocsMcp

A small, cross-platform MCP server for locally installed Linux manual pages. The first skeleton uses **stdio only** and targets **.NET 10**. It is intended to be built on Windows, copied to a Kubuntu/Linux machine, and launched by an MCP client there.

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

- MCP server host using the official C# SDK.
- Local `stdio` transport only.
- A harmless `ping` tool for testing that the client can launch and communicate with the server.
- No shell-command tool, no LAN listener, and no man-page commands wired in yet.

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

## Next implementation step

Add the local man-page tools (`search_man` and `get_man_page`) using fixed executables and validated arguments. Keep arbitrary shell execution out of the tool surface. LAN/HTTP transport can be added as a separately configured transport after local stdio is working.

## SDK

- MCP C# SDK: <https://github.com/modelcontextprotocol/csharp-sdk>
- Getting started: <https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/getting-started.md>

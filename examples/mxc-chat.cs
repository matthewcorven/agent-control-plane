#!/usr/bin/env dotnet
#:package Microsoft.Mxc.Sdk@1.1.0
using System.Net;
using System.Net.Sockets;
using Microsoft.Mxc.Sdk;
using Microsoft.Mxc.Sdk.V1;

if (!OperatingSystem.IsMacOS() || args.Length != 1)
{
    Console.Error.WriteLine("Usage on macOS: dotnet examples/mxc-chat.cs <published-chat-executable>");
    return 2;
}
var binary = Path.GetFullPath(args[0]);
if (!File.Exists(binary) || binary.Contains('"') || binary.Contains('\\') || binary.Contains('\n') || binary.Contains('\r'))
{
    Console.Error.WriteLine("The executable must exist and its path must not contain quotes, backslashes, or newlines.");
    return 2;
}
var endpoint = new Uri("http://127.0.0.1:8001/v1");
if (endpoint.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(endpoint.Host, out var ip) || !IPAddress.IsLoopback(ip) || ip.AddressFamily != AddressFamily.InterNetwork)
{
    Console.Error.WriteLine("The experimental MXC launcher requires a numeric IPv4 HTTP loopback endpoint.");
    return 2;
}
var request = new ContainerRequest($"\"{binary}\"")
{
    Containment = new Containment.Seatbelt { NestedPty = false },
    Filesystem = new FilesystemPolicy { ReadonlyPaths = [binary] },
    Network = new NetworkPolicy
    {
        Egress = new NetworkEgressPolicy { Default = NetworkAction.Deny },
        Ingress = new NetworkIngressPolicy { Default = NetworkAction.Deny, HostLoopback = NetworkAction.Deny },
        RuntimeConfig = new NetworkRuntimeConfig { NetworkProxy = $"http://127.0.0.1:{endpoint.Port}" }
    },
    TimeoutMs = 120000
};
try
{
    using var process = MxcContainer.SpawnWithExtendedOptions(request, new SpawnExtendedOptions { Stdio = SpawnStdio.Inherit });
    foreach (var warning in process.Warnings) Console.Error.WriteLine($"MXC warning: {warning}");
    var result = await process.WaitAsync();
    if (result.TimedOut) { Console.Error.WriteLine("MXC workload timed out."); return 1; }
    return result.ExitCode;
}
catch (Exception ex) when (ex is MxcException or IOException)
{
    Console.Error.WriteLine($"MXC launch failed: {ex.Message}");
    return 1;
}

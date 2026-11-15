#!/usr/bin/env dotnet
#:package Microsoft.Mxc.Sdk@1.1.0
using System.Net;
using System.Net.Sockets;
using Microsoft.Mxc.Sdk;
using Microsoft.Mxc.Sdk.V1;

if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("This probe is validated only on macOS."); return 2; }
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
var request = new ContainerRequest($"/bin/sh -c 'if /usr/bin/curl -s --connect-timeout 2 http://127.0.0.1:{port}/ >/dev/null 2>&1; then exit 10; else exit 0; fi'")
{
    Containment = new Containment.Process(),
    Network = new NetworkPolicy { Egress = new NetworkEgressPolicy { Default = NetworkAction.Deny }, Ingress = new NetworkIngressPolicy { Default = NetworkAction.Deny, HostLoopback = NetworkAction.Deny } },
    TimeoutMs = 10000
};
var result = await MxcContainer.RunAsync(request);
Console.Write(result.Stdout);
Console.Error.Write(result.Stderr);
foreach (var warning in result.Warnings) Console.Error.WriteLine($"warning: {warning}");
if (result.TimedOut || result.ExitCode != 0 || listener.Pending()) { Console.Error.WriteLine("MXC network probe failed or timed out."); return 1; }
Console.WriteLine("MXC probe: loopback connection blocked (examples do not use MXC yet).");
return 0;

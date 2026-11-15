#!/usr/bin/env dotnet
#:package Microsoft.Mxc.Sdk@1.1.0
using System.Net;
using System.Net.Sockets;
using Microsoft.Mxc.Sdk;
using Microsoft.Mxc.Sdk.V1;

if (!OperatingSystem.IsMacOS() || args.Length < 1)
{
    Console.Error.WriteLine("Usage on macOS: dotnet examples/mxc-run.cs <published-example-executable> [example arguments]");
    return 2;
}
var executable = Path.GetFullPath(args[0]);
var name = Path.GetFileName(executable);
var parameters = args.Skip(1).ToArray();
var read = new List<string> { executable };
var write = new List<string>();
int expected;
switch (name)
{
    case "01-hello-world-chat-stdio": expected = 0; break;
    case "02-file-read": expected = 1; if (parameters.Length == expected) { parameters[0] = Path.GetFullPath(parameters[0]); read.Add(parameters[0]); } break;
    case "03-file-write":
    case "03-file-append":
    case "03-file-tts-by-prompt": expected = 2; if (parameters.Length == expected) { parameters[0] = Path.GetFullPath(parameters[0]); write.Add(parameters[0]); } break;
    case "03-file-tts-by-stdin": expected = 1; if (parameters.Length == expected) { parameters[0] = Path.GetFullPath(parameters[0]); write.Add(parameters[0]); } break;
    case "03-file-audio-and-tts": expected = 2; if (parameters.Length == expected) { parameters[0] = Path.GetFullPath(parameters[0]); parameters[1] = Path.GetFullPath(parameters[1]); read.Add(parameters[0]); write.Add(parameters[1]); } break;
    default: Console.Error.WriteLine("Only the seven published example names are supported."); return 2;
}
if (parameters.Length != expected || !File.Exists(executable) || read.Skip(1).Any(p => !File.Exists(p)))
{
    Console.Error.WriteLine("Incorrect arguments, or the executable or input file does not exist.");
    return 2;
}
if (write.Any(p => Directory.Exists(p)) || read.Intersect(write, StringComparer.Ordinal).Any())
{
    Console.Error.WriteLine("Output paths must be files distinct from input paths.");
    return 2;
}
var commandArguments = new[] { executable }.Concat(parameters).ToArray();
if (commandArguments.Any(a => a.Any(c => char.IsControl(c) || c == '"' || c == '\\')))
{
    Console.Error.WriteLine("Arguments cannot contain quotes, backslashes, or control characters.");
    return 2;
}
var endpoint = new Uri("http://127.0.0.1:8001/v1");
if (endpoint.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(endpoint.Host, out var ip) || !IPAddress.IsLoopback(ip) || ip.AddressFamily != AddressFamily.InterNetwork)
{
    Console.Error.WriteLine("The experimental MXC launcher requires a numeric IPv4 HTTP loopback endpoint.");
    return 2;
}
var command = string.Join(" ", commandArguments.Select(a => $"\"{a}\""));
var request = new ContainerRequest(command)
{
    Containment = new Containment.Seatbelt { NestedPty = false },
    Filesystem = new FilesystemPolicy { ReadonlyPaths = read, ReadwritePaths = write },
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

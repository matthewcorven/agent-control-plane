#!/usr/bin/env dotnet
#:include ../examples/common.cs

foreach (var value in new[] { "http://192.0.2.1:8001/v1", "https://example.com/v1", "ftp://127.0.0.1/v1" })
{
    try { LocalEndpoint.Create(value); throw new Exception($"Accepted non-local endpoint {value}"); }
    catch (InvalidOperationException) { }
}
var endpoint = LocalEndpoint.Create("http://127.0.0.1:8001/v1");
using var http = LocalHttp.Create(endpoint);
if (http.BaseAddress != endpoint) throw new Exception("Wrong local endpoint");
Console.WriteLine("Loopback endpoint validation passed.");

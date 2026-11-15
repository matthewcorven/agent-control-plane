#!/usr/bin/env dotnet
using System.Net;
using System.Net.Sockets;

var protectedPath = (await Console.In.ReadLineAsync()) ?? throw new InvalidOperationException("Missing test path");
try
{
    await File.ReadAllTextAsync(protectedPath);
    Console.Error.WriteLine("Unrelated file was readable");
    return 1;
}
catch (UnauthorizedAccessException) { Console.WriteLine("Unrelated file denied"); }

using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
try
{
    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 8002));
    Console.Error.WriteLine("Unrelated port was reachable");
    return 1;
}
catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
{
    Console.WriteLine("Unrelated port denied");
}
return 0;

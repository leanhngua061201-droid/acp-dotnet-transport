using System.IO.Pipes;
using System.Text.Json;
using Acp.Transport;

// Two anonymous pipes form a local duplex connection. This is a simulated peer,
// not an actual ACP agent, authentication flow, or protocol-conformance test.
using var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
using var clientInput = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
using var toPeer = new AnonymousPipeServerStream(PipeDirection.Out);
using var peerInput = new AnonymousPipeClientStream(PipeDirection.In, toPeer.ClientSafePipeHandle);
var updateReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

await using var client = new AcpTransport(clientInput, toPeer,
    requestHandler: (request, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { accepted = true })),
    notificationHandler: (method, _) =>
    {
        Console.WriteLine($"Client received notification: {method}");
        updateReceived.TrySetResult();
    });
await using var peer = new AcpTransport(peerInput, toClient,
    requestHandler: (request, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { protocolVersion = 1 })));
peer.Start();

using var response = await client.RequestAsync("initialize", new { protocolVersion = 1 });
Console.WriteLine($"Initialize result from simulated peer: {response.RootElement}");
await peer.NotifyAsync("session/update", new { sessionId = "demo" });
await updateReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
using var reverseResponse = await peer.RequestAsync("demo/confirm", new { operation = "example" });
Console.WriteLine($"Reverse-request result: {reverseResponse.RootElement}");

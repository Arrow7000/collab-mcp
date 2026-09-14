module Collab.Tests.Transport

open System
open System.IO
open System.Diagnostics
open System.Net
open System.Net.Sockets
open System.Text.Json.Nodes
open Xunit
open Collab.Adapters.OpenCode
open Collab.Daemon
open Collab.Shim

let private withScript body test =
    let path = Path.Combine(Path.GetTempPath(), "collab-script-" + Guid.NewGuid().ToString "N")
    File.WriteAllText(path, "#!/bin/sh\n" + body)
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    try test path finally File.Delete path

[<Fact>]
let ``discovery kills a hung subprocess at its deadline`` () =
    withScript "sleep 30\n" (fun executable ->
        let elapsed = Stopwatch.StartNew()
        match Discovery.baseUrlFor executable (TimeSpan.FromMilliseconds 100.) with
        | Error(Unreachable detail) -> Assert.Contains("deadline", detail)
        | result -> failwithf "unexpected discovery result %A" result
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds 3.))

[<Fact>]
let ``discovery drains stderr concurrently instead of deadlocking`` () =
    withScript "dd if=/dev/zero bs=1024 count=256 1>&2 2>/dev/null\necho http://127.0.0.1:1234\n" (fun executable ->
        match Discovery.baseUrlFor executable (TimeSpan.FromSeconds 3.) with
        | Ok uri -> Assert.Equal(1234, uri.Port)
        | result -> failwithf "unexpected discovery result %A" result)

let private withSockets test =
    use listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
    listener.Bind(IPEndPoint(IPAddress.Loopback, 0))
    listener.Listen 1
    use client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
    client.Connect listener.LocalEndPoint
    use server = listener.Accept()
    test client server
let private request = { Verb = "roster"; Input = JsonObject(); Metadata = None }

[<Fact>]
let ``a stalled daemon answer times out with an unknown outcome`` () = withSockets (fun client _ ->
    let elapsed = Stopwatch.StartNew()
    let answer = Client.exchange client request (TimeSpan.FromMilliseconds 100.) |> Async.RunSynchronously
    Assert.False answer.Ok
    Assert.Contains("outcome is unknown", answer.Text)
    Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds 3.))

[<Fact>]
let ``an answered daemon call returns its exact answer`` () = withSockets (fun client server ->
    use stream = new NetworkStream(server, false)
    use writer = new StreamWriter(stream, AutoFlush = true)
    writer.WriteLine(Protocol.encodeAnswer { Ok = true; Text = "ready" })
    let answer = Client.exchange client request (TimeSpan.FromSeconds 1.) |> Async.RunSynchronously
    Assert.True answer.Ok
    Assert.Equal("ready", answer.Text))

let private withHttp responder test =
    use listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
    listener.Bind(IPEndPoint(IPAddress.Loopback, 0))
    listener.Listen 1
    let port = (listener.LocalEndPoint :?> IPEndPoint).Port
    let serving =
        async {
            use! accepted = listener.AcceptAsync() |> Async.AwaitTask
            use stream = new NetworkStream(accepted, false)
            use reader = new StreamReader(stream)
            let mutable reading = true
            while reading do
                let! line = reader.ReadLineAsync() |> Async.AwaitTask
                reading <- not (isNull line || line = "")
            do! responder stream
        } |> Async.StartAsTask
    use client = new OpenCodeClient({ BaseUrl = Uri $"http://127.0.0.1:{port}"; Password = "test" }, TimeSpan.FromMilliseconds 300.)
    try test client
    finally
        listener.Dispose()
        try serving.GetAwaiter().GetResult() with :? ObjectDisposedException -> ()
let private reply raw (stream: Stream) = async {
    let bytes = Text.Encoding.UTF8.GetBytes(raw: string)
    do! stream.WriteAsync(bytes).AsTask() |> Async.AwaitTask
    do! stream.FlushAsync() |> Async.AwaitTask
}
let private synthetic = { Id = Some "msg_test"; Text = "hello"; Description = Some "peer"; Delivery = Queue }

[<Fact>]
let ``successful headers establish admission even when the body is truncated`` () =
    withHttp (reply "HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\n{") (fun client ->
        match client.PostSynthetic("session", synthetic) |> Async.RunSynchronously with
        | Ok None -> ()
        | result -> failwithf "unexpected admission %A" result)

[<Theory>]
[<InlineData(500)>]
[<InlineData(408)>]
let ``server errors leave admission uncertain`` status =
    withHttp (reply $"HTTP/1.1 {status} Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n") (fun client ->
        match client.PostSynthetic("session", synthetic) |> Async.RunSynchronously with
        | Error(ApiError.AdmissionUnknown _) -> ()
        | result -> failwithf "unexpected admission %A" result)

[<Fact>]
let ``a missing synthetic destination identifies the dead session`` () =
    withHttp (reply "HTTP/1.1 404 Missing\r\nContent-Length: 0\r\nConnection: close\r\n\r\n") (fun client ->
        match client.PostSynthetic("session", synthetic) |> Async.RunSynchronously with
        | Error(SessionNotFound "session") -> ()
        | result -> failwithf "unexpected admission %A" result)

[<Fact>]
let ``event handshake has a deadline without caller cancellation`` () =
    withHttp (fun _ -> async { do! Async.Sleep 500 }) (fun client ->
        match client.ReadEvents(ignore, Threading.CancellationToken.None) |> Async.RunSynchronously with
        | Error(Unreachable detail) -> Assert.Contains("deadline", detail)
        | result -> failwithf "unexpected connection %A" result)

[<Fact>]
let ``connected event stream survives beyond the handshake deadline`` () =
    withHttp (fun stream -> async {
        do! reply "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n" stream
        do! Async.Sleep 500
        do! reply "event: sample\ndata: {\"type\":\"sample\",\"data\":{}}\n\n" stream
    }) (fun client ->
        let mutable connected, frames = false, 0
        let result = client.ReadEvents((fun _ -> frames <- frames + 1), Threading.CancellationToken.None,
                                       onConnected = (fun () -> connected <- true)) |> Async.RunSynchronously
        Assert.True connected
        Assert.Equal(1, frames)
        Assert.Equal<Result<unit, ApiError>>(Ok(), result))

[<Fact>]
let ``matching synthetic inbox evidence confirms admission`` () =
    let body = "{\"data\":[{\"id\":\"msg_test\",\"type\":\"synthetic\",\"sessionID\":\"session\",\"delivery\":\"queue\",\"payload\":{\"text\":\"hello\",\"description\":\"peer\"}}]}"
    let raw = $"HTTP/1.1 200 OK\r\nContent-Length: {Text.Encoding.UTF8.GetByteCount body}\r\nConnection: close\r\n\r\n{body}"
    withHttp (reply raw) (fun client ->
        let result = client.FindSynthetic("session", synthetic) |> Async.RunSynchronously
        Assert.Equal<Result<bool, ApiError>>(Ok true, result))

[<Fact>]
let ``malformed inbox evidence cannot confirm admission`` () =
    withHttp (reply "HTTP/1.1 200 OK\r\nContent-Length: 11\r\nConnection: close\r\n\r\n{\"data\":{}}") (fun client ->
        match client.FindSynthetic("session", synthetic) |> Async.RunSynchronously with
        | Error _ -> ()
        | result -> failwithf "unexpected evidence %A" result)

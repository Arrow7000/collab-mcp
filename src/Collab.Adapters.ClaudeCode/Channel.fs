namespace Collab.Adapters.ClaudeCode

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Sockets
open System.Text.Json.Nodes
open System.Threading
open Collab.Domain

/// A per-session stdio shim owns the channel pipe. Leases are transport availability,
/// never definitive session deletion: Claude can resume the same conversation.
type ChannelConnection =
    { Endpoint: Endpoint
      Token: string
      Socket: string
      Projects: string
      Seen: DateTimeOffset }

module Channel =
    let messageId (envelope: Envelope) =
        let (MessageId id) = envelope.Id
        "msg_" + id.ToString("N")

    let parameters (envelope: Envelope) : JsonNode =
        let meta = JsonObject()
        meta["message_id"] <- JsonValue.Create(messageId envelope)
        meta["sender_name"] <- JsonValue.Create(AgentName.value envelope.From)
        meta["sender_id"] <- JsonValue.Create(envelope.FromAddress |> Option.defaultValue "")
        let payload = JsonObject()
        payload["content"] <- JsonValue.Create(envelope.Body + "\n\nReply using collab send to the sender_id above. End your turn when awaiting a reply; do not poll.")
        payload["meta"] <- meta
        payload

    let transcriptContent envelope =
        let payload = parameters envelope
        let meta = payload["meta"] :?> JsonObject
        let attributes = meta |> Seq.map (fun pair -> pair.Key + "=\"" + pair.Value.GetValue<string>() + "\"") |> String.concat " "
        "<channel source=\"collab\" " + attributes + ">\n" + payload["content"].GetValue<string>() + "\n</channel>"

    /// Native durable queue admission or the channel-origin conversation entry.
    /// Assistant output, prompt snapshots, and mere ID matches are not receipts.
    let receipt (session: SessionId) (envelope: Envelope) (line: string) =
        try
            let node = JsonNode.Parse line
            let (SessionId sid) = session
            let expected = transcriptContent envelope
            let queue =
                node["type"].GetValue<string>() = "queue-operation"
                && node["operation"].GetValue<string>() = "enqueue"
                && node["content"].GetValue<string>() = expected
            let conversation =
                if node["type"].GetValue<string>() <> "user" then false
                else
                    (node["origin"].["kind"]).GetValue<string>() = "channel"
                    && (node["origin"].["server"]).GetValue<string>() = "collab"
                    && (node["message"].["content"]).GetValue<string>() = expected
            node["sessionId"].GetValue<string>() = sid && (queue || conversation)
        with _ -> false

type ClaudeCodeAdapter(?receiptFile: string) =
    let connections = ConcurrentDictionary<Endpoint, ChannelConnection>()
    let roots = ConcurrentDictionary<Endpoint, string>()
    let gate = obj()
    let events = Event<HarnessEvent>()

    do
        match receiptFile with
        | Some file when File.Exists file ->
            for node in JsonNode.Parse(File.ReadAllText file).AsArray() do
                let endpoint = { Harness = ClaudeCode; Session = SessionId(node["session"].GetValue<string>()); Scope = Scope(node["directory"].GetValue<string>()) }
                roots[endpoint] <- node["projects"].GetValue<string>()
        | _ -> ()

    let saveRoots () =
        match receiptFile with
        | None -> ()
        | Some file ->
            let values = JsonArray()
            for pair in roots do
                let (SessionId sid) = pair.Key.Session
                let (Scope directory) = pair.Key.Scope
                let node = JsonObject()
                for key, value in [ "session", sid; "directory", directory; "projects", pair.Value ] do node[key] <- JsonValue.Create value
                values.Add node
            let temp = file + ".tmp"
            File.WriteAllText(temp, values.ToJsonString())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            File.Move(temp, file, true)

    member _.Register(connection: ChannelConnection) =
        // Global router capacity is independently enforced; cap transport descriptors too.
        lock gate (fun () ->
            if roots.ContainsKey connection.Endpoint || roots.Count < 256 then
                let changed = match roots.TryGetValue connection.Endpoint with true, previous -> previous <> connection.Projects | _ -> true
                if changed then
                    let previous = match roots.TryGetValue connection.Endpoint with true, value -> Some value | _ -> None
                    roots[connection.Endpoint] <- connection.Projects
                    try saveRoots ()
                    with error ->
                        match previous with
                        | Some value -> roots[connection.Endpoint] <- value
                        | None -> roots.TryRemove connection.Endpoint |> ignore
                        raise error
                connections[connection.Endpoint] <- connection
                true
            else false)

    member _.Resolve(token: string) =
        connections.Values
        |> Seq.tryFind (fun c -> c.Token = token && DateTimeOffset.UtcNow - c.Seen < TimeSpan.FromSeconds 45.)
        |> Option.map _.Endpoint

    member _.IsConnected(endpoint: Endpoint) =
        match connections.TryGetValue endpoint with
        | true, c -> DateTimeOffset.UtcNow - c.Seen < TimeSpan.FromSeconds 45. && File.Exists c.Socket
        | _ -> false

    member _.FindAdmission(endpoint: Endpoint, envelope: Envelope) : Async<Result<bool, string>> = async {
        match roots.TryGetValue endpoint with
        | false, _ -> return Ok false
        | true, projects ->
            try
                let (SessionId sid) = endpoint.Session
                if not (Directory.Exists projects) then return Ok false
                else
                    let files = Directory.EnumerateFiles(projects, sid + ".jsonl", SearchOption.AllDirectories)
                    return Ok(files |> Seq.exists (fun file -> File.ReadLines file |> Seq.exists (Channel.receipt endpoint.Session envelope)))
            with error -> return Error error.Message
    }

    interface HarnessPort with
        member _.Kind = ClaudeCode
        member _.Observe() = events.Publish
        member this.Deliver(endpoint, envelope) = async {
            if envelope.Urgency = Interrupt then
                return Error(HarnessRejected(0, "Claude Code channels do not support interrupt; use at_turn_boundary."))
            else
                match connections.TryGetValue endpoint with
                | false, _ -> return Error(HarnessUnreachable "Claude Code channel is not connected")
                | true, c when DateTimeOffset.UtcNow - c.Seen >= TimeSpan.FromSeconds 45. ->
                    return Error(HarnessUnreachable "Claude Code channel lease has expired")
                | true, connection ->
                    use socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
                    use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 5.)
                    // Connection failure proves no notification was submitted.
                    let! connected = async {
                        try
                            do! socket.ConnectAsync(UnixDomainSocketEndPoint connection.Socket, deadline.Token).AsTask() |> Async.AwaitTask
                            return true
                        with _ -> return false
                    }
                    if not connected then return Error(HarnessUnreachable "Claude Code shim is disconnected")
                    else
                        try
                            use stream = new NetworkStream(socket, true)
                            use writer = new StreamWriter(stream, AutoFlush = true)
                            use reader = new StreamReader(stream)
                            let request = JsonObject()
                            request["token"] <- JsonValue.Create connection.Token
                            request["params"] <- Channel.parameters envelope
                            do! writer.WriteLineAsync(request.ToJsonString().AsMemory(), deadline.Token) |> Async.AwaitTask
                            let! response = reader.ReadLineAsync(deadline.Token).AsTask() |> Async.AwaitTask
                            if response <> "written" then return Error(AdmissionUnknown "channel write was not confirmed")
                            else
                                let rec receipt attempts = async {
                                    let! proof = this.FindAdmission(endpoint, envelope)
                                    match proof with
                                    | Ok true -> return Ok true
                                    | _ when attempts > 0 ->
                                        do! Async.Sleep 50
                                        return! receipt (attempts - 1)
                                    | _ -> return proof
                                }
                                let! proof = receipt 10
                                match proof with
                                | Ok true -> return Ok()
                                | _ -> return Error(AwaitingReceipt "channel written; awaiting native transcript evidence")
                        with error -> return Error(AdmissionUnknown error.Message)
        }

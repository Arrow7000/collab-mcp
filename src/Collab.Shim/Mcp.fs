/// The MCP half of the shim: JSON-RPC 2.0 over stdio, one object per line.
///
/// Deliberately thin. It parses a call, forwards it, and wraps whatever came back as
/// text. The shim bounds concurrent forwarding and serializes response frames; the
/// daemon remains the owner of routing and delivery decisions.
///
/// Hand-rolled rather than taking an SDK, for the same reason the SSE reader is: the
/// subset we need is small, and one fewer dependency across a process boundary we
/// already control is worth more than the generality.
namespace Collab.Shim

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open System.Threading
open Collab.Daemon

module Mcp =

    /// Echoed back to the client when it names one, since the harness knows better than
    /// we do which revision it is speaking. This is only the fallback.
    [<Literal>]
    let private FallbackProtocolVersion = "2025-06-18"

    /// The agent-facing contract.
    ///
    /// The descriptions carry more weight than usual. An agent meets this system only
    /// through them, and the one failure they exist to prevent — waiting in a loop for a
    /// reply instead of ending the turn — is the failure that hangs a session outright
    /// in opencode's sandbox (docs/findings-oc2.md). So they say what not to do as
    /// plainly as what to do.
    let private tools =
        JsonNode.Parse
            """
[
  {
    "name": "hello",
    "description": "Choose or change your peer display name at any time. OC2 sessions with collab loaded are registered automatically with a stable generated name; hello is optional. A name owned by another session is refused; choose a different name. Repeating your name is harmless. Your stable peer ID stays the same and previous names remain aliases for your lifetime, so existing conversations and queued mail keep reaching you. You supply only a name: which session you are, and which project you are working in, are taken from your runtime rather than from you.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "name": { "type": "string", "description": "The name peers will address you by, for example RedStone. Letters, digits, hyphen and underscore." }
      },
      "required": ["name"]
    }
  },
  {
    "name": "roster",
    "description": "List the agents working in this project, so you can address one by name. Takes no arguments.",
    "inputSchema": { "type": "object", "properties": {} }
  },
  {
    "name": "send",
    "description": "Send a message to another agent by name. Delivery is push, not pull: the recipient is woken and handed your message even if it is idle, so there is no inbox anywhere and nothing to check. Messages to you arrive the same way, on their own. If you need a reply, call this once and then END YOUR TURN — you will be woken when the reply arrives. Never sleep, poll, retry or loop waiting for one.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "to": { "type": "string", "description": "The current name, previous name, or stable peer ID of the recipient, as shown by roster. Prefer the sender ID shown in a received message when replying." },
        "body": { "type": "string", "description": "What to say. Your own name is attached for you." },
        "urgency": {
          "type": "string",
          "enum": ["at_turn_boundary", "interrupt"],
          "description": "at_turn_boundary, the default, lets the recipient finish what it is doing first. interrupt cuts into its work immediately; use it only when the recipient must stop, such as when it is about to conflict with you."
        }
      },
      "required": ["to", "body"]
    }
  }
]
"""

    let private emit (output: TextWriter) gate (message: JsonObject) =
        message["jsonrpc"] <- JsonValue.Create "2.0"
        let line = message.ToJsonString()
        lock gate (fun () -> output.WriteLine line; output.Flush())

    let private reply output gate (id: JsonNode) (payload: JsonNode) =
        let message = JsonObject()
        message["id"] <- id.DeepClone()
        message["result"] <- payload
        emit output gate message

    let private refuse output gate (id: JsonNode) (code: int) (detail: string) =
        let problem = JsonObject()
        problem["code"] <- JsonValue.Create code
        problem["message"] <- JsonValue.Create detail
        let message = JsonObject()
        message["id"] <- id.DeepClone()
        message["error"] <- problem
        emit output gate message

    /// A tool result is text plus a flag; the daemon has already written the text.
    let private asToolResult (answer: Answer) : JsonNode =
        let item = JsonObject()
        item["type"] <- JsonValue.Create "text"
        item["text"] <- JsonValue.Create answer.Text
        let content = JsonArray()
        content.Add item
        let payload = JsonObject()
        payload["content"] <- content
        payload["isError"] <- JsonValue.Create(not answer.Ok)
        payload :> JsonNode

    let private initialize (parameters: JsonNode option) : JsonNode =
        let capabilities = JsonObject()
        capabilities["tools"] <- JsonObject()
        let server = JsonObject()
        server["name"] <- JsonValue.Create "collab"
        server["version"] <- JsonValue.Create "0.1.0"
        let payload = JsonObject()

        payload["protocolVersion"] <-
            JsonValue.Create(
                parameters
                |> Option.bind (Field.text "protocolVersion")
                |> Option.defaultValue FallbackProtocolVersion
            )

        payload["capabilities"] <- capabilities
        payload["serverInfo"] <- server
        payload["instructions"] <- JsonValue.Create "OC2 sessions with collab loaded are automatically registered with a stable peer name. You do not need hello before roster or send. Roster identifies your own name and other project peers. Hello is optional to choose or change a display name. Your peer ID survives renaming and reconnection; previous names stay reserved aliases during your lifetime. Address replies using the sender ID included in messages. Peer messages arrive automatically; end your turn when waiting for a reply and never poll."
        payload :> JsonNode

    let private listTools () : JsonNode =
        let payload = JsonObject()
        payload["tools"] <- tools.DeepClone()
        payload :> JsonNode

    let private callTool (parameters: JsonNode option) : Async<JsonNode> =
        async {
            let named =
                parameters
                |> Option.bind (Field.text "name")
                |> Option.filter (fun name ->
                    name = Verbs.Hello || name = Verbs.Roster || name = Verbs.Send)

            match named with
            | None -> return asToolResult { Ok = false; Text = "no such tool" }
            | Some verb ->
                let arguments =
                    match parameters with
                    | Some(:? JsonObject as object') when object'.ContainsKey "arguments" ->
                        match object'["arguments"] with
                        | null -> JsonObject() :> JsonNode
                        | value -> value.DeepClone()
                    | _ -> JsonObject() :> JsonNode

                let metadata =
                    match parameters with
                    | Some(:? JsonObject as object') ->
                        match object'["_meta"] with
                        | null -> None
                        | value -> Some(value.DeepClone())
                    | _ -> None

                let! answer = Client.call verb arguments metadata
                return asToolResult answer
        }

    /// Independent sessions share this pipe. Bound forwarding without blocking reads;
    /// control requests remain responsive and each complete response is written atomically.
    let serveWith (input: TextReader) (output: TextWriter)
                  (forward: JsonNode option -> Async<JsonNode>) (maxConcurrentTools: int) : int =
        if maxConcurrentTools < 1 then invalidArg "maxConcurrentTools" "must be positive"
        let outputGate = obj ()
        let reply = reply output outputGate
        let refuse = refuse output outputGate
        use slots = new SemaphoreSlim(maxConcurrentTools, maxConcurrentTools)
        // The initial count keeps completion open while stdin can add new requests.
        use pending = new CountdownEvent(1)

        let dispatch id parameters =
            if not (slots.Wait 0) then
                reply id (asToolResult { Ok = false; Text = "The collaboration shim is at its concurrent request limit. This request was not forwarded or accepted; report this rather than polling." })
            else
                pending.AddCount()
                async {
                    try
                        try
                            let! payload = forward parameters
                            reply id payload
                        with error ->
                            try refuse id -32603 $"Tool processing failed: {error.Message}. The outcome may be unknown; do not resend automatically."
                            with writeError -> eprintfn $"collab shim: response write failed: {writeError.Message}"
                    finally
                        slots.Release() |> ignore
                        pending.Signal() |> ignore
                } |> fun work -> Async.Start(work, cancellationToken = CancellationToken.None)

        let rec loop () =
            match input.ReadLine() with
            | null -> ()
            | line when String.IsNullOrWhiteSpace line -> loop ()
            | line ->
                (try
                    match JsonNode.Parse line with
                    | null -> ()
                    | request ->
                        let parameters =
                            match request with
                            | :? JsonObject as object' when object'.ContainsKey "params" ->
                                match object'["params"] with
                                | null -> None
                                | value -> Some value
                            | _ -> None

                        let identifier =
                            match request with
                            | :? JsonObject as object' when object'.ContainsKey "id" ->
                                match object'["id"] with
                                | null -> None
                                | value -> Some value
                            | _ -> None

                        match Field.text "method" request, identifier with
                        | _, None -> ()
                        | Some "initialize", Some id -> reply id (initialize parameters)
                        | Some "tools/list", Some id -> reply id (listTools ())
                        | Some "tools/call", Some id -> dispatch id parameters
                        | Some "ping", Some id -> reply id (JsonObject() :> JsonNode)
                        | Some other, Some id -> refuse id -32601 $"unsupported method '{other}'"
                        | None, Some id -> refuse id -32600 "no method"
                 with error -> eprintfn $"collab shim: {error.Message}")
                loop ()

        try loop ()
        finally
            // EOF stops intake but must not drop the responses already being forwarded.
            pending.Signal() |> ignore
            pending.Wait()
        0

    let serve () : int =
        Client.warm ()
        use input = new StreamReader(Console.OpenStandardInput(), UTF8Encoding false)
        use output = new StreamWriter(Console.OpenStandardOutput(), UTF8Encoding false, AutoFlush = true)
        serveWith input output callTool 32

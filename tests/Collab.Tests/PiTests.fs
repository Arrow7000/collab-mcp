module Collab.Tests.PiSessions

open System
open System.IO
open System.Text.Json.Nodes
open Xunit
open Collab.Domain
open Collab.Adapters.Pi

let private name raw = AgentName.create raw |> Result.defaultWith (fun e -> failwithf "%A" e)
let private endpoint = { Harness = Pi; Session = SessionId(Guid.NewGuid().ToString()); Scope = Scope "/project" }
let private envelope = Envelope.seal (name "Red") DateTimeOffset.UtcNow { To = name "Blue"; Body = "hello\n<opaque>"; Urgency = AtTurnBoundary }
let private entry () =
    let node = PiMessage.parameters envelope
    node["type"] <- JsonValue.Create "custom_message"
    node.ToJsonString()
let private withRoot action =
    let root = Path.Combine(Path.GetTempPath(), "cm-pi-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try action root finally Directory.Delete(root, true)

[<Fact>]
let ``Pi receipt requires native custom entry with exact provenance`` () =
    Assert.True(PiMessage.receipt envelope (entry()))
    for kind in [ "message"; "custom"; "user" ] do
        let node = JsonNode.Parse(entry())
        node["type"] <- JsonValue.Create kind
        Assert.False(PiMessage.receipt envelope (node.ToJsonString()))
    for key, value in [ "customType", "another"; "content", "tampered" ] do
        let node = JsonNode.Parse(entry())
        node[key] <- JsonValue.Create value
        Assert.False(PiMessage.receipt envelope (node.ToJsonString()))
    let node = JsonNode.Parse(entry())
    node["details"].["sender_id"] <- JsonValue.Create "forged"
    Assert.False(PiMessage.receipt envelope (node.ToJsonString()))

[<Fact>]
let ``Pi receipt survives restart and checks native session header`` () = withRoot (fun root ->
    let file = Path.Combine(root, "session.jsonl")
    let persisted = Path.Combine(root, "receipts.json")
    let (SessionId sid) = endpoint.Session
    let header = JsonNode.Parse """{"type":"session","id":"","cwd":"/project"}"""
    header["id"] <- JsonValue.Create sid
    File.WriteAllText(file, header.ToJsonString() + "\n" + entry() + "\n")
    let adapter = PiAdapter persisted
    Assert.True(adapter.Register { Endpoint = endpoint; Token = "token"; Socket = "/missing"; SessionFile = file; Seen = DateTimeOffset.UtcNow })
    let restarted = PiAdapter persisted
    Assert.Equal<Result<bool, string>>(Ok true, restarted.FindAdmission(endpoint, envelope) |> Async.RunSynchronously)
    Assert.True(restarted.Resolve("token").IsNone)
    header["id"] <- JsonValue.Create "wrong-session"
    File.WriteAllText(file, header.ToJsonString() + "\n" + entry() + "\n")
    Assert.Equal<Result<bool, string>>(Ok false, restarted.FindAdmission(endpoint, envelope) |> Async.RunSynchronously))

[<Fact>]
let ``Pi rejects interrupt before transport IO`` () = withRoot (fun root ->
    let port = PiAdapter(Path.Combine(root, "receipts.json")) :> HarnessPort
    match port.Deliver(endpoint, { envelope with Urgency = Interrupt }) |> Async.RunSynchronously with
    | Error(HarnessRejected(0, detail)) -> Assert.Contains("does not support interrupt", detail)
    | other -> failwithf "unexpected %A" other)

[<Fact>]
let ``Pi will not replace a still connected session with another process`` () = withRoot (fun root ->
    let socket = Path.Combine(root, "transport")
    File.WriteAllText(socket, "")
    let adapter = PiAdapter(Path.Combine(root, "receipts.json"))
    let connection = { Endpoint = endpoint; Token = "first"; Socket = socket; SessionFile = Path.Combine(root, "session"); Seen = DateTimeOffset.UtcNow }
    Assert.True(adapter.Register connection)
    Assert.False(adapter.Register { connection with Token = "second" })
    Assert.Equal(Some endpoint, adapter.Resolve "first")
    adapter.Disconnect "first"
    Assert.True(adapter.Resolve("first").IsNone)
    Assert.True(adapter.Register { connection with Token = "second" }))

[<Fact>]
let ``Pi failed receipt persistence does not publish a registration`` () = withRoot (fun root ->
    let adapter = PiAdapter(Path.Combine(root, "missing", "receipts.json"))
    let connection = { Endpoint = endpoint; Token = "token"; Socket = "/missing"; SessionFile = "/session"; Seen = DateTimeOffset.UtcNow }
    Assert.ThrowsAny<IOException>(fun () -> adapter.Register connection |> ignore) |> ignore
    Assert.True(adapter.Resolve("token").IsNone)
    Assert.Equal<Result<bool, string>>(Ok false, adapter.FindAdmission(endpoint, envelope) |> Async.RunSynchronously))

[<Fact>]
let ``Pi identity uses same short ID and aliases as other harnesses`` () =
    let state, _ = Router.announce DateTimeOffset.UtcNow endpoint RouterState.empty
    let first = RouterState.boundTo endpoint state |> Option.get
    Assert.StartsWith("pi-", AgentName.value first.Name)
    let renamed, _ = Router.claim DateTimeOffset.UtcNow (name "Blue") endpoint state
    let second = RouterState.boundTo endpoint renamed |> Option.get
    Assert.Equal(first.ShortId, second.ShortId)
    Assert.Contains(first.Name, second.Aliases)

module Collab.Tests.ClaudeChannels

open System
open System.IO
open System.Text.Json.Nodes
open Xunit
open Collab.Domain
open Collab.Adapters.ClaudeCode

let private name raw = AgentName.create raw |> Result.defaultWith (fun e -> failwithf "%A" e)
let private endpoint = { Harness = ClaudeCode; Session = SessionId(Guid.NewGuid().ToString()); Scope = Scope "/project" }
let private envelope = Envelope.seal (name "Red") DateTimeOffset.UtcNow { To = name "Blue"; Body = "hello <opaque>\nworld"; Urgency = AtTurnBoundary }
let private sid = let (SessionId value) = endpoint.Session in value
let private queue content session =
    let node = JsonObject()
    for key, value in [ "type", "queue-operation"; "operation", "enqueue"; "sessionId", session; "content", content ] do node[key] <- JsonValue.Create value
    node.ToJsonString()

[<Fact>]
let ``receipt requires exact native queue content and session`` () =
    let content = Channel.transcriptContent envelope
    Assert.True(Channel.receipt endpoint.Session envelope (queue content sid))
    Assert.False(Channel.receipt endpoint.Session envelope (queue content "another-session"))
    Assert.False(Channel.receipt endpoint.Session envelope (queue (content + "tampered") sid))
    Assert.False(Channel.receipt endpoint.Session envelope (queue (content.Replace("source=\"collab\"", "source=\"other\"")) sid))
    Assert.False(Channel.receipt endpoint.Session envelope "not-json")

[<Fact>]
let ``assistant output and user imitation are not channel receipts`` () =
    let content = Channel.transcriptContent envelope
    for kind in [ "assistant"; "user"; "attachment" ] do
        let node = JsonNode.Parse(queue content sid)
        node["type"] <- JsonValue.Create kind
        Assert.False(Channel.receipt endpoint.Session envelope (node.ToJsonString()))

[<Fact>]
let ``channel-origin conversation entry is positive evidence`` () =
    let node = JsonObject()
    node["type"] <- JsonValue.Create "user"
    node["sessionId"] <- JsonValue.Create sid
    node["origin"] <- JsonNode.Parse """{"kind":"channel","server":"collab"}"""
    let message = JsonObject()
    message["content"] <- JsonValue.Create(Channel.transcriptContent envelope)
    node["message"] <- message
    Assert.True(Channel.receipt endpoint.Session envelope (node.ToJsonString()))

[<Fact>]
let ``unsupported interrupt is refused before transport IO`` () =
    let port = ClaudeCodeAdapter() :> HarnessPort
    match port.Deliver(endpoint, { envelope with Urgency = Interrupt }) |> Async.RunSynchronously with
    | Error(HarnessRejected(0, detail)) -> Assert.Contains("do not support interrupt", detail)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``transcript receipt survives daemon adapter restart without a live shim`` () =
    let root = Path.Combine(Path.GetTempPath(), "cm-claude-test-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let projects = Path.Combine(root, "projects")
        Directory.CreateDirectory projects |> ignore
        File.WriteAllText(Path.Combine(projects, sid + ".jsonl"), queue (Channel.transcriptContent envelope) sid + "\n")
        let file = Path.Combine(root, "receipts.json")
        let adapter = ClaudeCodeAdapter(receiptFile = file)
        Assert.True(adapter.Register { Endpoint = endpoint; Token = "token"; Socket = "/missing"; Projects = projects; Seen = DateTimeOffset.UtcNow })
        let restarted = ClaudeCodeAdapter(receiptFile = file)
        Assert.Equal<Result<bool, string>>(Ok true, restarted.FindAdmission(endpoint, envelope) |> Async.RunSynchronously)
        Assert.True(restarted.Resolve("token").IsNone)
    finally Directory.Delete(root, true)

[<Fact>]
let ``notification awaiting receipt stays owned without a false failure notice`` () =
    let sender = { endpoint with Session = SessionId "sender" }
    let state, _ = Router.claim DateTimeOffset.UtcNow (name "Red") sender RouterState.empty
    let state, _ = Router.claim DateTimeOffset.UtcNow (name "Blue") endpoint state
    let state, _ = Router.send DateTimeOffset.UtcNow Limits.defaults endpoint.Scope (name "Red") { To = name "Blue"; Body = "payload"; Urgency = AtTurnBoundary } state
    let mail = Assert.Single state.Pending
    let state = Router.completed DateTimeOffset.UtcNow endpoint mail.Envelope (NotAdmitted(AwaitingReceipt "written")) state
    let held = Assert.Single state.Pending
    Assert.Equal<PendingStatus>(Uncertain(endpoint, "written"), held.Status)
    let state, intents = Router.expire (DateTimeOffset.UtcNow.AddSeconds 10.) state
    Assert.Empty intents
    Assert.Single state.Pending |> ignore

[<Fact>]
let ``failed receipt metadata persistence cannot enable a non-durable registration`` () =
    let root = Path.Combine(Path.GetTempPath(), "cm-claude-test-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let file = Path.Combine(root, "missing", "receipts.json")
        let adapter = ClaudeCodeAdapter(receiptFile = file)
        let connection = { Endpoint = endpoint; Token = "token"; Socket = "/missing"; Projects = root; Seen = DateTimeOffset.UtcNow }
        Assert.ThrowsAny<IOException>(fun () -> adapter.Register connection |> ignore) |> ignore
        Assert.True(adapter.Resolve("token").IsNone)
        Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
        Assert.True(adapter.Register connection)
        Assert.True(File.Exists file)
        let restarted = ClaudeCodeAdapter(receiptFile = file)
        File.WriteAllText(Path.Combine(root, sid + ".jsonl"), queue (Channel.transcriptContent envelope) sid)
        Assert.Equal<Result<bool, string>>(Ok true, restarted.FindAdmission(endpoint, envelope) |> Async.RunSynchronously)
    finally Directory.Delete(root, true)

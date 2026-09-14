module Collab.Tests.InvocationAttribution

open System
open System.Text.Json.Nodes
open Xunit
open Collab.Domain
open Collab.Daemon
open Collab.Adapters.OpenCode

let private now = DateTimeOffset.Parse "2026-09-14T12:00:00Z"
let private clock = { new Clock with member _.Now() = now }
let private endpoint session =
    { Harness = OpenCode; Session = SessionId session; Scope = Scope "/project" }
let private a, b = endpoint "a", endpoint "b"
let private invocation = { Tool = "send"; Input = "{\"to\":\"Red\",\"body\":\"same\"}" }
let private equal (expected: 'T) (actual: 'T) = Assert.Equal<'T>(expected, actual)
let private table () = Attribution(16, TimeSpan.FromMinutes 1., clock)

[<Fact>]
let ``session metadata disambiguates identical held sends`` () =
    let attribution = table ()
    attribution.Record(a, invocation)
    attribution.Record(b, invocation)
    equal (Some b)
        (attribution.Resolve(invocation, TimeSpan.Zero, session = b.Session) |> Async.RunSynchronously)
    equal (Some a)
        (attribution.Resolve(invocation, TimeSpan.Zero, session = a.Session) |> Async.RunSynchronously)

[<Fact>]
let ``a fact from another session cannot satisfy a waiting call`` () =
    let attribution = table ()
    let waiting =
        attribution.Resolve(invocation, TimeSpan.FromSeconds 2., session = b.Session)
        |> Async.StartImmediateAsTask
    attribution.Record(a, invocation)
    Assert.False waiting.IsCompleted
    attribution.Record(b, invocation)
    equal (Some b) (waiting.GetAwaiter().GetResult())
    equal (Some a)
        (attribution.Resolve(invocation, TimeSpan.Zero, session = a.Session) |> Async.RunSynchronously)

[<Fact>]
let ``a session hint never falls back to another session's fact`` () =
    let attribution = table ()
    attribution.Record(a, invocation)
    equal None
        (attribution.Resolve(invocation, TimeSpan.Zero, session = b.Session) |> Async.RunSynchronously)
    equal (Some a)
        (attribution.Resolve(invocation, TimeSpan.Zero, session = a.Session) |> Async.RunSynchronously)

[<Fact>]
let ``session scoped matching supports canonical argument order`` () =
    let attribution = table ()
    attribution.Record(a, invocation)
    let reordered = { invocation with Input = "{\"body\":\"same\",\"to\":\"Red\"}" }
    equal (Some a) (attribution.Resolve(reordered, TimeSpan.Zero, session = a.Session) |> Async.RunSynchronously)

[<Fact>]
let ``identical calls resolve only to the required session`` () =
    let attribution = table ()
    attribution.Record(a, invocation)
    attribution.Record(b, invocation)
    equal (Some b) (attribution.Resolve(invocation, TimeSpan.Zero, session = b.Session) |> Async.RunSynchronously)

[<Theory>]
[<InlineData("{\"ai.opencode/sessionID\":\"b\"}")>]
[<InlineData("{\"sessionID\":\"b\"}")>]
[<InlineData("{\"ai.opencode/sessionID\":\"b\",\"sessionID\":\"b\"}")>]
let ``both supported metadata keys recover the session`` (json: string) =
    equal (Ok b.Session) (Mapping.sessionMetadata (Some(JsonNode.Parse json)))

[<Theory>]
[<InlineData("{\"ai.opencode/sessionID\":\"a\",\"sessionID\":\"b\"}")>]
[<InlineData("{\"sessionID\":null}")>]
[<InlineData("{\"sessionID\":17}")>]
[<InlineData("{\"sessionID\":\" \"}")>]
[<InlineData("[]")>]
let ``invalid context is an error rather than legacy matching`` (json: string) =
    match Mapping.sessionMetadata (Some(JsonNode.Parse json)) with
    | Error _ -> ()
    | result -> failwithf "Expected invalid context, got %A" result

[<Fact>]
let ``missing session metadata is refused`` () =
    for metadata in [ None; Some(JsonNode.Parse "{\"progressToken\":1}") ] do
        match Mapping.sessionMetadata metadata with
        | Error detail -> Assert.Contains("required", detail)
        | result -> failwithf "unsafe metadata result: %A" result

[<Fact>]
let ``daemon protocol preserves metadata independently of arguments`` () =
    let input = JsonNode.Parse invocation.Input
    let metadata = JsonNode.Parse "{\"ai.opencode/sessionID\":\"b\",\"progressToken\":42}"
    let call = { Verb = invocation.Tool; Input = input; Metadata = Some metadata }
    let decoded = call |> Protocol.encodeCall |> Protocol.decodeCall |> Option.get
    equal (input.ToJsonString()) (decoded.Input.ToJsonString())
    equal (metadata.ToJsonString()) (decoded.Metadata.Value.ToJsonString())
    equal (Ok b.Session) (Mapping.sessionMetadata decoded.Metadata)
    Assert.False((decoded.Input :?> JsonObject).ContainsKey "ai.opencode/sessionID")

[<Fact>]
let ``old daemon requests decode without metadata`` () =
    let call = Protocol.decodeCall "{\"verb\":\"roster\",\"input\":{}}" |> Option.get
    equal None call.Metadata

let private progress (session: string) (directory: string) (tool: string) =
    let data = JsonObject()
    data["sessionID"] <- JsonValue.Create session
    data["id"] <- JsonValue.Create "tool_0"
    data["assistantMessageID"] <- JsonValue.Create "step"
    let item = JsonObject()
    item["status"] <- JsonValue.Create "running"
    item["tool"] <- JsonValue.Create tool
    let calls = JsonArray()
    calls.Add item
    let metadata = JsonObject()
    metadata["toolCalls"] <- calls
    data["metadata"] <- metadata
    { Type = EventTypes.ToolProgress; Data = data; Directory = Some directory }

[<Theory>]
[<InlineData("other.roster")>]
[<InlineData("roster")>]
[<InlineData("other.collab.roster")>]
[<InlineData("collaboration.roster")>]
let ``another server's tools cannot supply attribution facts`` tool =
    Assert.Empty(Mapping.observe (progress "a" "/project" tool))

let private observation frame =
    match Mapping.observe frame with
    | [ McpToolCalled(endpoint, identity, invocation) ] -> endpoint, identity, invocation
    | other -> failwithf "Expected one collab call, got %A" other

[<Fact>]
let ``collab roster with absent arguments is observed as an empty object`` () =
    let observed, _, invocation = progress "a" "/project" "collab.roster" |> observation
    equal a observed
    equal "roster" invocation.Tool
    equal "{}" invocation.Input

[<Fact>]
let ``dedupe identity includes session and project as well as provider call identity`` () =
    let _, first, _ = progress "a" "/project" "collab.roster" |> observation
    let _, repeated, _ = progress "a" "/project" "collab.roster" |> observation
    let _, otherSession, _ = progress "b" "/project" "collab.roster" |> observation
    let _, otherProject, _ = progress "a" "/other-project" "collab.roster" |> observation
    equal first repeated
    Assert.NotEqual<string>(first, otherSession)
    Assert.NotEqual<string>(first, otherProject)

[<Fact>]
let ``unscoped events cannot supply identity`` () =
    let frame = progress "a" "/project" "collab.roster"
    Assert.Empty(Mapping.observe { frame with Directory = None })

let private directFrame kind session directory =
    let node = JsonNode.Parse "{\"sessionID\":\"a\",\"assistantMessageID\":\"step\",\"id\":\"call\",\"name\":\"collab_hello\",\"input\":{\"name\":\"Red\"}}"
    node["sessionID"] <- JsonValue.Create(session: string)
    { Type = kind; Data = node; Directory = Some directory }

[<Fact>]
let ``direct tool name and arguments share a scoped call identity`` () =
    let started = directFrame EventTypes.ToolInputStarted "a" "/project"
    let identity, tool = Mapping.directStarted started |> Option.get
    let called = { started with Type = EventTypes.ToolCalled }
    match Mapping.directCalled tool called with
    | [ McpToolCalled(endpoint, observedIdentity, call) ] ->
        equal a endpoint
        equal identity observedIdentity
        equal "hello" call.Tool
        equal "{\"name\":\"Red\"}" call.Input
    | result -> failwithf "unexpected direct call %A" result
    Assert.NotEqual(Mapping.directIdentity called, Mapping.directIdentity(directFrame EventTypes.ToolCalled "b" "/project"))
    Assert.NotEqual(Mapping.directIdentity called, Mapping.directIdentity(directFrame EventTypes.ToolCalled "a" "/other"))

[<Theory>]
[<InlineData("other_hello")>]
[<InlineData("collab_unknown")>]
[<InlineData("collaboration_hello")>]
let ``direct names from other servers cannot supply context`` qualified =
    let frame = directFrame EventTypes.ToolInputStarted "a" "/project"
    frame.Data["name"] <- JsonValue.Create(qualified: string)
    equal None (Mapping.directStarted frame)

[<Theory>]
[<InlineData("session.created")>]
[<InlineData("session.viewed")>]
[<InlineData("session.execution.started")>]
let ``session lifecycle observations require real project context`` kind =
    let frame = directFrame kind "a" "/project"
    equal (Some a) (Mapping.sessionAvailable frame)
    equal None (Mapping.sessionAvailable { frame with Directory = None })
    equal None (Mapping.sessionAvailable { frame with Directory = Some "relative" })

[<Fact>]
let ``authenticated deletion without directory identifies a session but never a caller`` () =
    let frame = { directFrame EventTypes.SessionDeleted "a" "/project" with Directory = None }
    equal [ SessionDeleted(SessionId "a") ] (Mapping.observe frame)
    equal None (Mapping.sessionAvailable frame)
    frame.Data["sessionID"] <- JsonValue.Create " "
    equal [] (Mapping.observe frame)

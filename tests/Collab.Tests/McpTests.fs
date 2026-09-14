module Collab.Tests.McpConcurrency

open System
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open System.Text.Json.Nodes
open Xunit
open Collab.Shim

// Force individual writes to yield so an unlocked writer would interleave frames.
type private ObservedWriter() =
    inherit StringWriter()
    let lines = Channel.CreateUnbounded<string>()
    let mutable writing = 0
    let mutable overlap = false
    member _.Lines = lines.Reader
    member _.Overlap = overlap
    override this.WriteLine(value: string) =
        if Interlocked.Increment(&writing) > 1 then overlap <- true
        try
            for character in value do
                base.Write character
                Thread.Yield() |> ignore
            base.Write '\n'
            lines.Writer.TryWrite value |> ignore
        finally Interlocked.Decrement(&writing) |> ignore

let private frame id methodName parameter =
    let request = JsonObject()
    request.["jsonrpc"] <- JsonValue.Create "2.0"
    request.["id"] <- JsonValue.Create(id: int)
    request.["method"] <- JsonValue.Create(methodName: string)
    let parameters = JsonObject()
    parameters.["number"] <- JsonValue.Create(parameter: int)
    request.["params"] <- parameters
    request.ToJsonString()

let private next (writer: ObservedWriter) =
    writer.Lines.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds 3.).GetAwaiter().GetResult() |> JsonNode.Parse
let private id (node: JsonNode) = node.["id"].GetValue<int>()
let private payload () = JsonObject() :> JsonNode
let private wait (task: Task<int>) = task.WaitAsync(TimeSpan.FromSeconds 3.).GetAwaiter().GetResult()

[<Fact>]
let ``other sessions and ping respond while a tool waits and EOF drains it`` () =
    use writer = new ObservedWriter()
    use reader = new StringReader(String.concat "\n" [ frame 1 "tools/call" 1; frame 2 "tools/call" 2; frame 3 "ping" 0 ])
    let held = TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously)
    let entered = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    let forward (parameters: JsonNode option) = async {
        if parameters.Value.["number"].GetValue<int>() = 1 then
            entered.SetResult()
            return! held.Task |> Async.AwaitTask
        else return payload ()
    }
    let server = Task.Run(fun () -> Mcp.serveWith reader writer forward 2)
    try
        entered.Task.WaitAsync(TimeSpan.FromSeconds 3.).GetAwaiter().GetResult()
        let answers = [ id (next writer); id (next writer) ] |> Set.ofList
        Assert.Equal<Set<int>>(Set.ofList [ 2; 3 ], answers)
        Assert.False server.IsCompleted
    finally held.TrySetResult(payload ()) |> ignore
    Assert.Equal(0, wait server)
    Assert.Equal(1, id (next writer))
    Assert.False writer.Overlap

[<Fact>]
let ``overload is rejected without forwarding while control requests remain responsive`` () =
    use writer = new ObservedWriter()
    use reader = new StringReader(String.concat "\n" [ frame 1 "tools/call" 1; frame 2 "tools/call" 2; frame 3 "ping" 0 ])
    let held = TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable forwarded = 0
    let forward _ = async {
        Interlocked.Increment(&forwarded) |> ignore
        return! held.Task |> Async.AwaitTask
    }
    let server = Task.Run(fun () -> Mcp.serveWith reader writer forward 1)
    try
        let first, second = next writer, next writer
        let refused = if id first = 2 then first else second
        Assert.Equal(2, id refused)
        Assert.True(refused.["result"].["isError"].GetValue<bool>())
        Assert.Contains("not forwarded or accepted", refused.ToJsonString())
        Assert.Equal<Set<int>>(Set.ofList [ 2; 3 ], Set.ofList [ id first; id second ])
    finally held.TrySetResult(payload ()) |> ignore
    Assert.Equal(0, wait server)
    Assert.Equal(1, forwarded)

[<Fact>]
let ``concurrent results retain IDs and intact escaped JSON frames`` () =
    use writer = new ObservedWriter()
    use reader = new StringReader([ 1 .. 32 ] |> List.map (fun number -> frame number "tools/call" number) |> String.concat "\n")
    let forward (parameters: JsonNode option) = async {
        let result = JsonObject()
        result.["text"] <- JsonValue.Create "quotes \" and newline\n🔥"
        result.["number"] <- JsonValue.Create(parameters.Value.["number"].GetValue<int>())
        return result :> JsonNode
    }
    Assert.Equal(0, Mcp.serveWith reader writer forward 32)
    let responses = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.map JsonNode.Parse
    Assert.Equal(32, responses.Length)
    Assert.Equal<Set<int>>(Set.ofList [ 1 .. 32 ], responses |> Array.map id |> Set.ofArray)
    for response in responses do
        Assert.Equal(id response, response.["result"].["number"].GetValue<int>())
        Assert.Equal("quotes \" and newline\n🔥", response.["result"].["text"].GetValue<string>())
    Assert.False writer.Overlap

[<Fact>]
let ``a failed tool gets an error response and does not lose other answers or hang EOF`` () =
    use writer = new ObservedWriter()
    use reader = new StringReader(String.concat "\n" [ frame 1 "tools/call" 1; frame 2 "tools/call" 2; frame 3 "ping" 0 ])
    let forward (parameters: JsonNode option) = async {
        if parameters.Value.["number"].GetValue<int>() = 1 then failwith "probe failure"
        return payload ()
    }
    Assert.Equal(0, Mcp.serveWith reader writer forward 2)
    let responses = [ next writer; next writer; next writer ]
    Assert.Equal<Set<int>>(Set.ofList [ 1; 2; 3 ], responses |> List.map id |> Set.ofList)
    let failed = responses |> List.find (fun response -> id response = 1)
    Assert.Equal(-32603, failed.["error"].["code"].GetValue<int>())
    Assert.Contains("outcome may be unknown", failed.["error"].["message"].GetValue<string>())

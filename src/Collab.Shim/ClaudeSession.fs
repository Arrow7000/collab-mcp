namespace Collab.Shim

open System
open System.IO
open System.Net.Sockets
open System.Text.Json.Nodes
open System.Threading
open Collab.Daemon

/// Claude spawns an MCP process per session and exports native identity to it.
/// No tool argument or incoming _meta may override this context.
type ClaudeSession(session: string, directory: string, projects: string) =
    let token = Guid.NewGuid().ToString("N")
    let path = Path.Combine(Paths.directory(), "cc-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".sock")
    let listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
    let stop = new CancellationTokenSource()
    let mutable started = false

    member _.Metadata =
        let meta = JsonObject()
        meta["collab.claude/token"] <- JsonValue.Create token
        Some(meta :> JsonNode)

    member _.Register() = async {
        let input = JsonObject()
        for key, value in [ "session", session; "directory", directory; "projects", projects; "token", token; "socket", path ] do
            input[key] <- JsonValue.Create value
        return! Client.call "_claude_register" input None
    }

    member this.Start(notify: JsonObject -> unit) =
        if not started then
            listener.Bind(UnixDomainSocketEndPoint path)
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            listener.Listen 16
            started <- true
            let handle (client: Socket) = async {
                use client = client
                use deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token)
                deadline.CancelAfter(TimeSpan.FromSeconds 5.)
                try
                    use stream = new NetworkStream(client, false)
                    use reader = new StreamReader(stream)
                    use writer = new StreamWriter(stream, AutoFlush = true)
                    let! line = reader.ReadLineAsync(deadline.Token).AsTask() |> Async.AwaitTask
                    let request = JsonNode.Parse line
                    if Field.text "token" request = Some token then
                        let notification = JsonObject()
                        notification["method"] <- JsonValue.Create "notifications/claude/channel"
                        notification["params"] <- request["params"].DeepClone()
                        notify notification
                        do! writer.WriteLineAsync("written".AsMemory(), deadline.Token) |> Async.AwaitTask
                with error -> eprintfn $"collab Claude channel: {error.Message}"
            }
            let rec accepting () = async {
                let! client = listener.AcceptAsync(stop.Token).AsTask() |> Async.AwaitTask
                Async.Start(handle client, cancellationToken = stop.Token)
                return! accepting ()
            }
            let rec registering () = async {
                let! result = this.Register()
                if not result.Ok then eprintfn $"collab Claude registration: {result.Text}"
                do! Async.Sleep 15000
                return! registering ()
            }
            Async.Start(accepting (), cancellationToken = stop.Token)
            Async.Start(registering (), cancellationToken = stop.Token)

    interface IDisposable with
        member _.Dispose() =
            stop.Cancel()
            listener.Dispose()
            if File.Exists path then File.Delete path
            stop.Dispose()

    static member FromEnvironment() =
        let session = Environment.GetEnvironmentVariable "CLAUDE_CODE_SESSION_ID"
        let directory = Environment.GetEnvironmentVariable "CLAUDE_PROJECT_DIR"
        if not (isNull session) && (Guid.TryParse session |> fst)
           && not (String.IsNullOrWhiteSpace directory) && Path.IsPathFullyQualified directory then
            let config =
                match Environment.GetEnvironmentVariable "CLAUDE_CONFIG_DIR" with
                | null | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".claude")
                | value -> Path.GetFullPath value
            Some(new ClaudeSession(session, Path.GetFullPath directory, Path.Combine(config, "projects")))
        else None

/// Reaching the daemon, and starting it if nobody has.
///
/// The user must never have to launch anything: one line in their harness config and it
/// works. MCP has no runtime registration protocol, but a `local` server is spawned
/// lazily on first tool call (DESIGN.md §4, F8), so first use of a verb is the hook we
/// get, and this is what hangs off it.
namespace Collab.Shim

open System
open System.Diagnostics
open System.IO
open System.Net.Sockets
open System.Text.Json.Nodes
open System.Threading
open Collab.Daemon

module Client =

    /// Generous, because the first call of a session pays for a cold .NET start plus
    /// the daemon's discovery of the harness. Later calls connect immediately.
    let private startupTimeout = TimeSpan.FromSeconds 20.0

    /// Shorter, because this one is paid before the harness has finished starting the
    /// session. If the daemon is slower than this the first call still waits for it;
    /// giving up here only means not holding the session open any longer.
    let private warmTimeout = TimeSpan.FromSeconds 5.0

    let private retryDelayMs = 200

    let private tryConnect () : Socket option =
        let socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
        try
            socket.Connect(UnixDomainSocketEndPoint(Paths.socket ()))
            Some socket
        with _ ->
            socket.Dispose()
            None

    let private quote (text: string) = "'" + text.Replace("'", "'\\''") + "'"

    /// How to start ourselves again as the daemon.
    ///
    /// The shim and the daemon are one binary, which is not tidiness but the only way to
    /// make the promise above true: a path we resolve at runtime cannot be stale, cannot
    /// be missing a runtime config, and does not have to be written into anyone's
    /// harness config beside the shim's own.
    let private daemonCommand () =
        match Environment.ProcessPath with
        | null -> None
        | host when Path.GetFileNameWithoutExtension host = "dotnet" ->
            match Reflection.Assembly.GetEntryAssembly() with
            | null -> None
            | assembly -> Some $"{quote host} {quote assembly.Location} --daemon"
        | host -> Some $"{quote host} --daemon"

    /// Start the daemon detached, at most one of us at a time.
    ///
    /// `nohup … &` rather than a bare child process: the daemon must outlive the shim
    /// that started it, and every other shim in every other session goes on using it.
    /// Its output goes to a log because it has no terminal and nothing else would ever
    /// see why it failed.
    let private spawn () =
        try
            // Exclusive: a second shim racing us fails to take this and falls through to
            // retrying its connection, which is exactly what it should do.
            use _lock =
                new FileStream(Paths.spawnLock (), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)

            match tryConnect () with
            | Some socket ->
                // Someone finished while we were taking the lock.
                socket.Dispose()
            | None ->
                match daemonCommand () with
                | None -> ()
                | Some command ->
                    let start = ProcessStartInfo(FileName = "/bin/sh", UseShellExecute = false)
                    start.ArgumentList.Add "-c"
                    start.ArgumentList.Add $"nohup {command} >> {quote (Paths.log ())} 2>&1 &"
                    Process.Start start |> ignore
        with _ ->
            ()

    let private obtain (budget: TimeSpan) : Async<Socket option> =
        async {
            match tryConnect () with
            | Some socket -> return Some socket
            | None ->
                spawn ()
                let deadline = DateTimeOffset.UtcNow + budget

                let rec waiting () =
                    async {
                        do! Async.Sleep retryDelayMs

                        match tryConnect () with
                        | Some socket -> return Some socket
                        | None when DateTimeOffset.UtcNow < deadline -> return! waiting ()
                        | None -> return None
                    }

                return! waiting ()
        }

    /// Start the daemon early. Socket readiness does not guarantee SSE readiness;
    /// installed harnesses without session metadata can still lose the first fact.
    let warm () : unit =
        obtain warmTimeout
        |> Async.RunSynchronously
        |> Option.iter (fun socket -> socket.Dispose())

    /// A lost answer cannot establish whether the router accepted the request.
    let exchange (socket: Socket) (request: Call) (timeout: TimeSpan) : Async<Answer> =
        async {
            use deadline = new CancellationTokenSource(timeout)
            try
                use stream = new NetworkStream(socket, true)
                use writer = new StreamWriter(stream, AutoFlush = true)
                use reader = new StreamReader(stream)
                do! writer.WriteLineAsync((Protocol.encodeCall request).AsMemory(), cancellationToken = deadline.Token)
                    |> Async.AwaitTask
                let! line = reader.ReadLineAsync(deadline.Token).AsTask() |> Async.AwaitTask
                match (if isNull line then None else Protocol.decodeAnswer line) with
                | Some answer -> return answer
                | None -> return { Ok = false; Text = "The daemon closed the connection without an answer. The outcome is unknown; do not resend automatically." }
            with error ->
                socket.Dispose()
                return { Ok = false; Text = $"The daemon response was lost: {error.Message}. The outcome is unknown; do not resend automatically." }
        }

    /// Forward one call and return what the daemon said, verbatim.
    let call (verb: string) (input: JsonNode) (metadata: JsonNode option) : Async<Answer> =
        async {
            match! obtain startupTimeout with
            | None ->
                return { Ok = false; Text = $"the collaboration daemon could not be started; see {Paths.log ()}" }
            | Some socket ->
                return! exchange socket { Verb = verb; Input = input; Metadata = metadata } (TimeSpan.FromSeconds 60.)
        }

    /// Inspect an existing daemon without starting one or entering attribution.
    let status () : Async<Answer> = async {
        match tryConnect () with
        | None -> return { Ok = false; Text = "No collaboration daemon is reachable." }
        | Some socket ->
            return! exchange socket { Verb = "_status"; Input = JsonObject(); Metadata = None } (TimeSpan.FromSeconds 5.)
    }

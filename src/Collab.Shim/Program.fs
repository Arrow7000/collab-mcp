/// One binary, two roles.
///
/// The harness spawns this as a stdio MCP server; given `--daemon` it is instead the
/// long-lived router. They are the same executable so that the shim can start the daemon
/// without being told where it lives (see `Client.daemonCommand`), which is what makes
/// the whole thing one line of configuration.
namespace Collab.Shim

open Collab.Daemon

module Program =

    /// Native extensions use the same daemon protocol without pretending to be MCP.
    /// Sequential frames keep this bridge deliberately small; reverse delivery has
    /// its own socket and remains responsive during an outgoing tool call.
    let private bridge () =
        Client.warm ()
        let mutable reading = true
        while reading do
            match System.Console.ReadLine() with
            | null -> reading <- false
            | line ->
                let answer =
                    match Protocol.decodeCall line with
                    | Some call -> Client.call call.Verb call.Input call.Metadata |> Async.RunSynchronously
                    | None -> { Ok = false; Text = "Invalid native bridge frame; nothing was forwarded." }
                System.Console.WriteLine(Protocol.encodeAnswer answer)
                System.Console.Out.Flush()
        0

    [<EntryPoint>]
    let main argv =
        if Array.contains "--daemon" argv then
            Daemon.run ()
        elif Array.contains "--status" argv then
            let answer = Client.status () |> Async.RunSynchronously
            System.Console.WriteLine answer.Text
            if answer.Ok then 0 else 1
        elif Array.contains "--native-bridge" argv then
            bridge ()
        else
            Mcp.serve ()

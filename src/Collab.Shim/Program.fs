/// One binary, two roles.
///
/// The harness spawns this as a stdio MCP server; given `--daemon` it is instead the
/// long-lived router. They are the same executable so that the shim can start the daemon
/// without being told where it lives (see `Client.daemonCommand`), which is what makes
/// the whole thing one line of configuration.
namespace Collab.Shim

open Collab.Daemon

module Program =

    [<EntryPoint>]
    let main argv =
        if Array.contains "--daemon" argv then
            Daemon.run ()
        elif Array.contains "--status" argv then
            let answer = Client.status () |> Async.RunSynchronously
            System.Console.WriteLine answer.Text
            if answer.Ok then 0 else 1
        else
            Mcp.serve ()

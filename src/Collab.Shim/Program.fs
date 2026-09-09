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
        else
            Mcp.serve ()

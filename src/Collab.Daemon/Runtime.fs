/// Where the daemon lives on disk, and the two ambient services it needs.
namespace Collab.Daemon

open System
open System.IO
open Collab.Domain

/// The daemon's files, all under one directory owned by the user.
///
/// Not `$XDG_RUNTIME_DIR`: macOS has no such thing, and a path under the home directory
/// is the one place both halves of a lazily-spawned pair can agree on without being
/// told. Note that a unix socket path is limited to about a hundred bytes by the kernel,
/// so this stays short deliberately.
module Paths =

    let private home () =
        Environment.GetFolderPath Environment.SpecialFolder.UserProfile

    /// `~/.collab-mcp`, created private on first use. The daemon holds harness
    /// credentials and can steer any session (DESIGN.md §8), so nothing here is
    /// readable by anyone else.
    let directory () : string =
        let path =
            match Environment.GetEnvironmentVariable "COLLAB_MCP_HOME" with
            | null | "" -> Path.Combine(home (), ".collab-mcp")
            | configured -> Path.GetFullPath configured

        if not (Directory.Exists path) then
            Directory.CreateDirectory path |> ignore

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )

        path

    let socket () : string = Path.Combine(directory (), "daemon.sock")

    /// Held for the daemon's whole life, so a second daemon exits instead of racing for
    /// the socket.
    let daemonLock () : string = Path.Combine(directory (), "daemon.lock")

    /// Held only across a spawn, so two shims starting at once produce one daemon.
    let spawnLock () : string = Path.Combine(directory (), "spawn.lock")

    let log () : string = Path.Combine(directory (), "daemon.log")
    let database () : string = Path.Combine(directory (), "state.sqlite")

/// The daemon is started detached and has no terminal, so this is the only account of
/// what it did. Written to stdout, which the shim redirects to `Paths.log`.
module Log =

    let private gate = obj ()

    let write (message: string) =
        lock gate (fun () ->
            let stamp = DateTimeOffset.UtcNow.ToString "yyyy-MM-ddTHH:mm:ss.fffZ"
            Console.Out.WriteLine $"{stamp} {message}"
            Console.Out.Flush())

type SystemClock() =
    interface Clock with
        member _.Now() = DateTimeOffset.UtcNow

/// `IObserver` from a function, so subscribing to a port reads as one line.
///
/// Errors and completion are logged rather than handled: a harness stream that ends is
/// the adapter's business to reconnect, and there is nothing useful the router can do
/// about it that the adapter is not already doing.
module Observer =

    let onNext (handle: 'T -> unit) : IObserver<'T> =
        { new IObserver<'T> with
            member _.OnNext value = handle value
            member _.OnError error = Log.write $"event stream error: {error.Message}"
            member _.OnCompleted() = Log.write "event stream completed" }

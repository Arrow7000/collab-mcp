/// Join required harness-supplied session metadata to observed project context.
/// Both arrival orders are supported; facts from another session never match.
namespace Collab.Daemon

open System
open System.Collections.Generic
open System.Text.Json.Nodes
open System.Threading.Tasks
open Collab.Domain

/// Comparing JSON that two different programs serialised.
module Canonical =

    let rec private rewrite (node: JsonNode) : JsonNode =
        match node with
        | :? JsonObject as object' ->
            let sorted = JsonObject()

            for entry in object' |> Seq.sortWith (fun a b -> String.CompareOrdinal(a.Key, b.Key)) do
                sorted[entry.Key] <-
                    match entry.Value with
                    | null -> null
                    | value -> rewrite (value.DeepClone())

            sorted :> JsonNode
        | :? JsonArray as array' ->
            let ordered = JsonArray()

            for item in array' do
                ordered.Add(
                    match item with
                    | null -> null
                    | value -> rewrite (value.DeepClone())
                )

            ordered :> JsonNode
        | value -> value.DeepClone()

    /// A form in which two serialisations of the same arguments compare equal.
    ///
    /// The two sides of the join are produced by different programs — the harness
    /// serialises the arguments onto its event bus, the agent's runtime sends them over
    /// MCP — so key order is not something either side promises. Sorting removes the
    /// only difference we can remove; anything beyond that is a genuine mismatch.
    ///
    /// Absent arguments become an empty object, because the two sides disagree about how
    /// to say "none": a call taking no arguments reaches us over MCP as `{}` and appears
    /// on the bus with no field at all. Reconciling that here as well as in the adapter
    /// is cheap, and an argument-free verb is unattributable if either side gets it
    /// wrong.
    let ofJson (json: string) : string =
        try
            match JsonNode.Parse json with
            | null -> "{}"
            | root -> (rewrite root).ToJsonString()
        with _ ->
            json.Trim()

type private Fact =
    { Key: string
      Endpoint: Endpoint
      At: DateTimeOffset }

type private Waiter =
    { Key: string
      Session: SessionId
      Completion: TaskCompletionSource<Endpoint> }

/// The join, in both directions.
///
/// `capacity` and `ttl` bound the facts held: our tool calls across observed sessions
/// are announced here. An overrun costs
/// an unmatched call, which times out and tells the agent so.
type Attribution(capacity: int, ttl: TimeSpan, clock: Clock) =

    let gate = obj ()
    let facts = List<Fact>()
    let waiting = List<Waiter>()

    /// Session is checked separately; canonical arguments join the two transports.
    let keyOf (invocation: ToolInvocation) : string =
        invocation.Tool + string (char 0x1F) + Canonical.ofJson invocation.Input

    let prune (now: DateTimeOffset) =
        facts.RemoveAll(fun f -> now - f.At > ttl) |> ignore

        while facts.Count > capacity do
            facts.RemoveAt 0

    /// Take the oldest fact matching this key, if one is already here.
    let sessionMatches session (endpoint: Endpoint) =
        session = endpoint.Session

    let takeFact (key: string) (session: SessionId) =
        match facts.FindIndex(fun f -> f.Key = key && sessionMatches session f.Endpoint) with
        | -1 -> None
        | index ->
            let found = facts[index]
            facts.RemoveAt index
            Some found.Endpoint

    /// The harness says this session made this call.
    member _.Record(endpoint: Endpoint, invocation: ToolInvocation) : unit =
        let key = keyOf invocation
        let now = clock.Now()

        let waiter =
            lock gate (fun () ->
                match waiting.FindIndex(fun w -> w.Key = key && sessionMatches w.Session endpoint) with
                | -1 ->
                    prune now
                    facts.Add { Key = key; Endpoint = endpoint; At = now }
                    None
                | index ->
                    let found = waiting[index]
                    waiting.RemoveAt index
                    Some found)

        // Completed outside the lock: a continuation must never run while holding it.
        match waiter with
        | Some found ->
            found.Completion.TrySetResult endpoint |> ignore
            Log.write $"attribution: {invocation.Tool} answered a waiting call"
        // The key is logged on both sides: when the two disagree, seeing them together
        // is the only way to tell a lost fact from a mismatched one.
        | None -> Log.write $"attribution: holding {key}"

    /// Which session made this call. `None` if the harness never said — the agent is
    /// running somewhere we do not observe, or the fact was lost.
    member _.Resolve(invocation: ToolInvocation, timeout: TimeSpan, session: SessionId) : Async<Endpoint option> =
        async {
            let key = keyOf invocation

            // Checking and enrolling under one lock, because a fact arriving between
            // the two would otherwise be recorded with nobody left to claim it.
            let outcome =
                lock gate (fun () ->
                    prune (clock.Now())

                    match takeFact key session with
                    | Some endpoint -> Choice1Of2 endpoint
                    | None ->
                        let waiter =
                            { Key = key
                              Session = session
                              Completion =
                                TaskCompletionSource<Endpoint> TaskCreationOptions.RunContinuationsAsynchronously }

                        waiting.Add waiter
                        Choice2Of2 waiter)

            match outcome with
            | Choice1Of2 endpoint ->
                Log.write $"attribution: {invocation.Tool} matched a held fact"
                return Some endpoint
            | Choice2Of2 waiter ->
                try
                    let! endpoint = waiter.Completion.Task.WaitAsync timeout |> Async.AwaitTask
                    return Some endpoint
                with _ ->
                    lock gate (fun () -> waiting.Remove waiter |> ignore)
                    Log.write $"attribution: nothing ever claimed {key}"
                    return None
        }

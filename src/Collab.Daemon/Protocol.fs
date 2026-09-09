/// The wire between the shim and the daemon.
///
/// One JSON object per line, one answer per call, connection closed after. There is no
/// framing cleverer than a newline because there is nothing to be clever about: the
/// shim makes one call at a time and waits for its answer.
///
/// The daemon decides everything, including the wording, so an answer is a flag and a
/// string. That is what keeps the shim thin enough not to be worth testing: it has no
/// opinion about what any verb means.
namespace Collab.Daemon

open System.Text.Json
open System.Text.Json.Nodes

/// One MCP tool call, forwarded verbatim.
type Call =
    { /// The tool as the agent's runtime named it.
      Verb: string
      /// The arguments exactly as they arrived. Kept whole rather than parsed into
      /// fields, because this is also the correlation key: the same object appears on
      /// the harness's event bus beside the session that sent it, and matching the two
      /// is the only way a caller-blind MCP call can be attributed (DESIGN.md §7).
      Input: JsonNode }

/// What the agent is told.
type Answer = { Ok: bool; Text: string }

/// The three verbs, named once so the shim's tool list and the daemon's dispatch
/// cannot drift apart.
module Verbs =

    [<Literal>]
    let Hello = "hello"

    [<Literal>]
    let Roster = "roster"

    [<Literal>]
    let Send = "send"

/// Reading arguments a model wrote. Total, because nothing between the model and here
/// enforces the schema we published, and a mistyped field must be a refusal we can word
/// rather than an exception on the connection.
module Field =

    let text (name: string) (node: JsonNode) : string option =
        match node with
        | :? JsonObject as object' when object'.ContainsKey name ->
            match object'[name] with
            | null -> None
            | value ->
                try
                    match value.GetValue<string>() with
                    | null -> None
                    | found -> Some found
                with _ ->
                    None
        | _ -> None

module Protocol =

    let private text (node: JsonNode) (name: string) : string option = Field.text name node

    let encodeCall (call: Call) : string =
        let body = JsonObject()
        body["verb"] <- JsonValue.Create call.Verb
        body["input"] <- call.Input.DeepClone()
        body.ToJsonString()

    let decodeCall (line: string) : Call option =
        try
            match JsonNode.Parse line with
            | null -> None
            | root ->
                match text root "verb" with
                | None -> None
                | Some verb ->
                    let input =
                        match root with
                        | :? JsonObject as object' when object'.ContainsKey "input" ->
                            match object'["input"] with
                            | null -> JsonObject() :> JsonNode
                            | value -> value.DeepClone()
                        | _ -> JsonObject() :> JsonNode

                    Some { Verb = verb; Input = input }
        with _ ->
            None

    let encodeAnswer (answer: Answer) : string =
        let body = JsonObject()
        body["ok"] <- JsonValue.Create answer.Ok
        body["text"] <- JsonValue.Create answer.Text
        body.ToJsonString()

    let decodeAnswer (line: string) : Answer option =
        try
            match JsonNode.Parse line with
            | null -> None
            | root ->
                let ok =
                    match root with
                    | :? JsonObject as object' when object'.ContainsKey "ok" ->
                        try
                            object'["ok"].GetValue<bool>()
                        with _ ->
                            false
                    | _ -> false

                text root "text" |> Option.map (fun t -> { Ok = ok; Text = t })
        with _ ->
            None

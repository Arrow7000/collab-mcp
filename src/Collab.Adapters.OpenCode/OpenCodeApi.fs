/// A thin typed client over the opencode2 HTTP API.
///
/// Containment is the point. `synthetic`, `steer`, `queue` and the `/api/...` routes
/// are v2-beta with no stability guarantee (DESIGN.md §9), so they are named in this
/// project and nowhere else; if they move, only this file and its adapter change.
///
/// Nothing here knows what a message means. It moves bytes, and reports exactly what
/// the daemon said — including its failures, which are returned rather than thrown,
/// so the port above cannot be surprised by an exception.
namespace Collab.Adapters.OpenCode

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading

/// When the daemon should act on an admitted message.
///
/// opencode's words, not ours. `Urgency` is the domain's vocabulary; the translation
/// between the two is the single most load-bearing line in the adapter, and it lives
/// in OpenCodeAdapter.fs.
type Delivery =
    /// Land during generation and redirect the model.
    | Steer
    /// Hold until the current turn ends.
    | Queue

/// What went wrong with the daemon, or with reaching it.
///
/// Deliberately not `DeliveryFault`: that type's `SessionGone` carries an `Endpoint`,
/// a notion this layer does not have — it only knows session id strings. The adapter
/// translates, because it is the only part that knows on whose behalf a call was made.
type ApiError =
    | Unreachable of detail: string
    | SessionNotFound of session: string
    | Rejected of status: int * detail: string

/// Where the daemon is listening, and the credential it demands.
///
/// Both are discovered rather than configured: the port is dynamic (DESIGN.md §9) and
/// the password is written by the daemon itself.
type ServiceEndpoint = { BaseUrl: Uri; Password: string }

/// The body of a synthetic admission: input that is not a user turn.
type SyntheticMessage =
    { Text: string
      /// Shown by opencode beside the message. We put the sender there as well as in
      /// the text, so provenance survives anywhere the body is elided.
      Description: string option
      Delivery: Delivery }

/// The daemon's handle on a message it has admitted.
///
/// Kept because promoting an already-queued item to an interrupt addresses it by this
/// id. Nothing in the domain needs it, so it stops here.
type AdmittedId = AdmittedId of string

/// One decoded frame of the event stream, reduced to what a caller can act on: the
/// event type, and its `data` object.
///
/// The surrounding envelope (`id`, `created`, `durable`, `location`) is dropped here
/// so that the adapter's mapping reads as field lookups rather than as navigation.
type EventFrame = { Type: string; Data: JsonNode }

module Delivery =

    /// The wire words. These two strings are the reason this project exists as a
    /// separate assembly, so they appear exactly once.
    let wire (delivery: Delivery) : string =
        match delivery with
        | Steer -> "steer"
        | Queue -> "queue"

/// Total, defensive access into JSON we did not produce. The daemon's schema is beta;
/// a missing or retyped field must degrade to `None`, never to an exception on the
/// event pump.
module Json =

    let field (name: string) (node: JsonNode) : JsonNode option =
        match node with
        | :? JsonObject as object' when object'.ContainsKey name ->
            match object'[name] with
            | null -> None
            | value -> Some value
        | _ -> None

    let stringField (name: string) (node: JsonNode) : string option =
        match field name node with
        | Some value ->
            try
                match value.GetValue<string>() with
                | null -> None
                | text -> Some text
            with _ ->
                None
        | None -> None

/// Finding the daemon. Neither half is in a config file we control.
module Discovery =

    /// Basic auth: the username is fixed, only the password varies.
    [<Literal>]
    let Username = "opencode"

    [<Literal>]
    let private Executable = "opencode2"

    [<Literal>]
    let private StatusTimeoutMs = 5000

    /// `$XDG_CONFIG_HOME/opencode/service.json`, defaulting to `~/.config`.
    let servicePasswordFile () : string =
        let config =
            match Environment.GetEnvironmentVariable "XDG_CONFIG_HOME" with
            | null
            | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".config")
            | directory -> directory

        Path.Combine(config, "opencode", "service.json")

    let private firstUrl (text: string) : Uri option =
        text.Split([| '\n'; '\r'; ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryPick (fun token ->
            match Uri.TryCreate(token, UriKind.Absolute) with
            | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps -> Some uri
            | _ -> None)

    /// Ask the CLI where the daemon is.
    ///
    /// The port is dynamic and otherwise recorded only in opencode's own log, so
    /// shelling out is not laziness — it is the only contract offered (DESIGN.md §9).
    /// It is also the reason the result is cached by the client: this forks a process.
    let baseUrl () : Result<Uri, ApiError> =
        try
            let start =
                ProcessStartInfo(
                    FileName = Executable,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                )

            start.ArgumentList.Add "service"
            start.ArgumentList.Add "status"

            use runner = Process.Start start
            let out = runner.StandardOutput.ReadToEnd()
            let err = runner.StandardError.ReadToEnd()

            if not (runner.WaitForExit StatusTimeoutMs) then
                Error(Unreachable $"`{Executable} service status` did not exit within {StatusTimeoutMs}ms")
            elif runner.ExitCode <> 0 then
                Error(Unreachable $"`{Executable} service status` exited {runner.ExitCode}: {err.Trim()}")
            else
                match firstUrl out with
                | Some uri -> Ok uri
                | None -> Error(Unreachable $"`{Executable} service status` printed no URL: {out.Trim()}")
        with error ->
            Error(Unreachable $"cannot run `{Executable} service status`: {error.Message}")

    let password () : Result<string, ApiError> =
        let path = servicePasswordFile ()

        try
            use document = JsonDocument.Parse(File.ReadAllText path)

            match document.RootElement.TryGetProperty "password" with
            | true, value ->
                match value.GetString() with
                | null -> Error(Unreachable $"\"password\" in {path} is not a string")
                | secret -> Ok secret
            | _ -> Error(Unreachable $"no \"password\" in {path}")
        with error ->
            Error(Unreachable $"cannot read {path}: {error.Message}")

    let locate () : Result<ServiceEndpoint, ApiError> =
        match baseUrl (), password () with
        | Ok url, Ok secret -> Ok { BaseUrl = url; Password = secret }
        | Error error, _ -> Error error
        | _, Error error -> Error error

/// The routes we depend on. Every `/api/...` string in the system is here.
module Routes =

    /// Durably admit input that is not a user turn, and schedule execution. This is
    /// the endpoint that lets us wake an idle session (DESIGN.md §4, F2).
    let synthetic (session: string) : string =
        $"/api/session/{Uri.EscapeDataString session}/synthetic"

    /// The daemon-wide event stream. Not per session: one subscription sees every
    /// session, which is what makes attribution by correlation possible at all.
    [<Literal>]
    let Events = "/api/event"

/// Encoding and decoding the daemon's wire shapes.
module Wire =

    /// Every JSON response is wrapped as `{"data": ...}`; the payload is one level
    /// down. Returns `None` rather than throwing, so an unexpected shape can never
    /// turn a delivery that succeeded into an exception.
    let unwrap (json: string) : JsonNode option =
        try
            match JsonNode.Parse json with
            | null -> None
            | root -> Json.field "data" root
        with _ ->
            None

    /// The admission body. `delivery` is the whole reason the adapter boundary exists.
    let syntheticBody (message: SyntheticMessage) : string =
        let body = JsonObject()
        body["text"] <- JsonValue.Create message.Text :> JsonNode

        match message.Description with
        | Some description -> body["description"] <- JsonValue.Create description :> JsonNode
        | None -> ()

        body["delivery"] <- JsonValue.Create(Delivery.wire message.Delivery) :> JsonNode
        body.ToJsonString()

    /// Decode one event payload.
    ///
    /// The type arrives twice — as the SSE `event:` line and as `type` inside the
    /// payload. We prefer the payload's, because that is the field the daemon's own
    /// schema is keyed on, and fall back to the SSE name.
    let parseEvent (sseName: string) (payload: string) : EventFrame option =
        try
            match JsonNode.Parse payload with
            | null -> None
            | root ->
                let kind = Json.stringField "type" root |> Option.defaultValue sseName

                let data =
                    Json.field "data" root |> Option.defaultValue (JsonObject() :> JsonNode)

                if String.IsNullOrEmpty kind then
                    None
                else
                    Some { Type = kind; Data = data }
        with _ ->
            None

/// Server-sent events, hand-rolled.
///
/// The subset we need is twenty lines, and one fewer dependency on a surface that is
/// already unstable is worth more than the generality a library would bring.
module Sse =

    let private splitField (line: string) : string * string =
        match line.IndexOf ':' with
        | -1 -> line, ""
        | index ->
            let name = line.Substring(0, index)
            let raw = line.Substring(index + 1)

            let value =
                if raw.StartsWith(" ", StringComparison.Ordinal) then
                    raw.Substring 1
                else
                    raw

            name, value

    /// Read frames until the stream closes or the caller cancels.
    ///
    /// `onFrame` is called on the reading thread and must not block: the SSE contract
    /// states that a slow consumer overflows and fails the stream (DESIGN.md §9).
    let pump (reader: StreamReader) (onFrame: EventFrame -> unit) (ct: CancellationToken) : Async<unit> =
        async {
            let name = ref ""
            let data = StringBuilder()
            let running = ref true

            while running.Value && not ct.IsCancellationRequested do
                let! line = reader.ReadLineAsync(ct).AsTask() |> Async.AwaitTask

                match line with
                | null -> running.Value <- false
                | "" ->
                    if data.Length > 0 then
                        Wire.parseEvent name.Value (data.ToString()) |> Option.iter onFrame

                    name.Value <- ""
                    data.Clear() |> ignore
                | comment when comment.StartsWith(":", StringComparison.Ordinal) -> ()
                | text ->
                    match splitField text with
                    | "event", value -> name.Value <- value
                    | "data", value -> data.AppendLine value |> ignore
                    | _ -> ()
        }

/// A client bound to one opencode2 daemon, which it discovers lazily.
///
/// The discovered endpoint is cached and dropped again whenever the daemon proves
/// unreachable: a restarted daemon comes back on a *different* port, so caching for
/// ever would strand us, while discovering per call would fork a process per message.
type OpenCodeClient private (locate: unit -> Result<ServiceEndpoint, ApiError>, requestTimeout: TimeSpan) =

    // No client-wide timeout: the event stream is meant to live as long as the daemon
    // does. Individual requests carry their own deadline instead.
    let http = new HttpClient(Timeout = Timeout.InfiniteTimeSpan)
    let gate = obj ()
    let mutable cached: ServiceEndpoint option = None

    let endpoint () =
        lock gate (fun () ->
            match cached with
            | Some found -> Ok found
            | None ->
                match locate () with
                | Ok found ->
                    cached <- Some found
                    Ok found
                | Error error -> Error error)

    let forget () = lock gate (fun () -> cached <- None)

    let authorisation (service: ServiceEndpoint) =
        let raw = $"{Discovery.Username}:{service.Password}"
        AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes raw))

    /// Discover the daemon the usual way.
    new() = new OpenCodeClient(Discovery.locate, TimeSpan.FromSeconds 30.0)

    /// Point at a known daemon, for tests and for callers that already did discovery.
    new(service: ServiceEndpoint) = new OpenCodeClient((fun () -> Ok service), TimeSpan.FromSeconds 30.0)

    /// Admit a message that is not a user turn, waking the session if it is idle.
    ///
    /// A 404 is reported as `SessionNotFound` rather than as a rejection, because it
    /// is the one failure that means the binding is dead rather than the call wrong.
    member _.PostSynthetic(session: string, message: SyntheticMessage) : Async<Result<AdmittedId option, ApiError>> =
        async {
            match endpoint () with
            | Error error -> return Error error
            | Ok service ->
                try
                    use content =
                        new StringContent(Wire.syntheticBody message, Encoding.UTF8, "application/json")

                    use request =
                        new HttpRequestMessage(
                            HttpMethod.Post,
                            Uri(service.BaseUrl, Routes.synthetic session),
                            Content = content
                        )

                    request.Headers.Authorization <- authorisation service
                    use deadline = new CancellationTokenSource(requestTimeout)
                    use! response = http.SendAsync(request, deadline.Token) |> Async.AwaitTask
                    let! body = response.Content.ReadAsStringAsync deadline.Token |> Async.AwaitTask

                    if response.IsSuccessStatusCode then
                        // An unparseable body does not undo a delivery that happened,
                        // so the handle is optional rather than the call a failure.
                        return Ok(Wire.unwrap body |> Option.bind (Json.stringField "id") |> Option.map AdmittedId)
                    elif int response.StatusCode = 404 then
                        return Error(SessionNotFound session)
                    else
                        return Error(Rejected(int response.StatusCode, body.Trim()))
                with error ->
                    forget ()
                    return Error(Unreachable error.Message)
        }

    /// Stream events until the connection ends.
    ///
    /// Returns `Ok` on a clean close and an error otherwise; in both cases the caller
    /// is expected to reconnect, because the stream ending is normal — it fails on
    /// overflow by design (DESIGN.md §9).
    member _.ReadEvents(onFrame: EventFrame -> unit, ct: CancellationToken) : Async<Result<unit, ApiError>> =
        async {
            match endpoint () with
            | Error error -> return Error error
            | Ok service ->
                try
                    use request =
                        new HttpRequestMessage(HttpMethod.Get, Uri(service.BaseUrl, Routes.Events))

                    request.Headers.Authorization <- authorisation service
                    request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "text/event-stream")

                    use! response =
                        http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                        |> Async.AwaitTask

                    if not response.IsSuccessStatusCode then
                        let! body = response.Content.ReadAsStringAsync ct |> Async.AwaitTask
                        return Error(Rejected(int response.StatusCode, body.Trim()))
                    else
                        use! stream = response.Content.ReadAsStreamAsync ct |> Async.AwaitTask
                        use reader = new StreamReader(stream)
                        do! Sse.pump reader onFrame ct
                        return Ok()
                with
                | :? OperationCanceledException -> return Ok()
                | error ->
                    forget ()
                    return Error(Unreachable error.Message)
        }

    interface IDisposable with
        member _.Dispose() = http.Dispose()

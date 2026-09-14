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
open System.Threading.Tasks

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
    | AdmissionUnknown of detail: string

/// Where the daemon is listening, and the credential it demands.
///
/// Both are discovered rather than configured: the port is dynamic (DESIGN.md §9) and
/// the password is written by the daemon itself.
type ServiceEndpoint = { BaseUrl: Uri; Password: string }

/// The body of a synthetic admission: input that is not a user turn.
type SyntheticMessage =
    { Id: string option
      Text: string
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
/// One decoded event.
///
/// `Directory` comes from the event's top-level `location`, which the daemon puts on
/// every event. It is what scopes an agent to a project, so it is lifted here rather
/// than left buried in the raw node.
type EventFrame =
    { Type: string
      Data: JsonNode
      Directory: string option }

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
    let baseUrlFor (executable: string) (timeout: TimeSpan) : Result<Uri, ApiError> =
        try
            let start =
                ProcessStartInfo(
                    FileName = executable,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                )

            start.ArgumentList.Add "service"
            start.ArgumentList.Add "status"

            use runner = Process.Start start
            use deadline = new CancellationTokenSource(timeout)
            let stdout = runner.StandardOutput.ReadToEndAsync(deadline.Token)
            let stderr = runner.StandardError.ReadToEndAsync(deadline.Token)
            try
                let exiting = runner.WaitForExitAsync(deadline.Token)
                Task.WhenAll([| stdout :> Task; stderr :> Task; exiting |]).GetAwaiter().GetResult()
                let out, err = stdout.Result, stderr.Result
                if runner.ExitCode <> 0 then
                    Error(Unreachable $"`{executable} service status` exited {runner.ExitCode}: {err.Trim()}")
                else
                    match firstUrl out with
                    | Some uri -> Ok uri
                    | None -> Error(Unreachable $"`{executable} service status` printed no URL: {out.Trim()}")
            with :? OperationCanceledException ->
                try runner.Kill(true) with _ -> ()
                runner.WaitForExit 1000 |> ignore
                Error(Unreachable $"`{executable} service status` exceeded its {timeout.TotalMilliseconds}ms deadline")
        with error ->
            Error(Unreachable $"cannot run `{executable} service status`: {error.Message}")

    let baseUrl () = baseUrlFor Executable (TimeSpan.FromMilliseconds(float StatusTimeoutMs))

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

    let inbox (session: string) = $"/api/session/{Uri.EscapeDataString session}/inbox"
    let message (session: string) (id: string) = $"/api/session/{Uri.EscapeDataString session}/message/{Uri.EscapeDataString id}"

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
        message.Id |> Option.iter (fun id -> body["id"] <- JsonValue.Create id)
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

                let directory =
                    Json.field "location" root
                    |> Option.bind (Json.stringField "directory")

                if String.IsNullOrEmpty kind then
                    None
                else
                    Some
                        { Type = kind
                          Data = data
                          Directory = directory }
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
    new(service: ServiceEndpoint, requestTimeout: TimeSpan) = new OpenCodeClient((fun () -> Ok service), requestTimeout)

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
                    use! response =
                        http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                        |> Async.AwaitTask

                    if response.IsSuccessStatusCode then
                        // An unparseable body does not undo a delivery that happened,
                        // so the handle is optional rather than the call a failure.
                        let! body =
                            async {
                                try return! response.Content.ReadAsStringAsync deadline.Token |> Async.AwaitTask
                                with _ -> return ""
                            }
                        return Ok(Wire.unwrap body |> Option.bind (Json.stringField "id") |> Option.map AdmittedId)
                    elif int response.StatusCode = 404 then
                        return Error(SessionNotFound session)
                    else
                        let! body =
                            async {
                                try return! response.Content.ReadAsStringAsync deadline.Token |> Async.AwaitTask
                                with _ -> return response.ReasonPhrase
                            }
                        let status = int response.StatusCode
                        if status >= 500 || status = 408 then
                            return Error(AdmissionUnknown $"HTTP {status}: {body.Trim()}")
                        else return Error(Rejected(status, body.Trim()))
                with error ->
                    forget ()
                    return Error(AdmissionUnknown error.Message)
        }

    /// Eligibility comes from the harness MCP registry for the observed project.
    member _.HasServer(directory: string, server: string) : Async<Result<bool, ApiError>> = async {
        match endpoint () with
        | Error error -> return Error error
        | Ok service ->
            try
                use deadline = new CancellationTokenSource(requestTimeout)
                let route = "/api/mcp?location[directory]=" + Uri.EscapeDataString directory
                use request = new HttpRequestMessage(HttpMethod.Get, Uri(service.BaseUrl, route))
                request.Headers.Authorization <- authorisation service
                use! response = http.SendAsync(request, deadline.Token) |> Async.AwaitTask
                if not response.IsSuccessStatusCode then return Error(Rejected(int response.StatusCode, "MCP registry read failed"))
                else
                    let! body = response.Content.ReadAsStringAsync deadline.Token |> Async.AwaitTask
                    match Wire.unwrap body with
                    | Some(:? JsonArray as entries) ->
                        return Ok(entries |> Seq.exists (fun entry ->
                            Json.stringField "name" entry = Some server &&
                            (Json.field "status" entry |> Option.bind (Json.stringField "status")) = Some "connected"))
                    | _ -> return Error(Unreachable "malformed MCP registry response")
            with error ->
                forget ()
                return Error(Unreachable error.Message)
    }

    /// Positive evidence only: absence cannot establish whether a prior admission ran.
    member _.FindSynthetic(session: string, message: SyntheticMessage) : Async<Result<bool, ApiError>> =
        let get (route: string) = async {
            match endpoint () with
            | Error error -> return Error error
            | Ok service ->
                try
                    use deadline = new CancellationTokenSource(requestTimeout)
                    use request = new HttpRequestMessage(HttpMethod.Get, Uri(service.BaseUrl, route))
                    request.Headers.Authorization <- authorisation service
                    use! response = http.SendAsync(request, deadline.Token) |> Async.AwaitTask
                    if int response.StatusCode = 404 then return Ok None
                    elif not response.IsSuccessStatusCode then
                        return Error(Rejected(int response.StatusCode, "reconciliation read failed"))
                    else
                        let! body = response.Content.ReadAsStringAsync deadline.Token |> Async.AwaitTask
                        match Wire.unwrap body with
                        | Some data -> return Ok(Some data)
                        | None -> return Error(Unreachable "malformed reconciliation response")
                with error ->
                    forget ()
                    return Error(Unreachable error.Message)
        }
        let matches projected node =
            let payload = if projected then Some node else Json.field "payload" node
            Json.stringField "id" node = message.Id &&
            Json.stringField "type" node = Some "synthetic" &&
            (projected || (Json.stringField "sessionID" node = Some session &&
                           Json.stringField "delivery" node = Some(Delivery.wire message.Delivery))) &&
            (payload |> Option.exists (fun p ->
                Json.stringField "text" p = Some message.Text &&
                Json.stringField "description" p = message.Description))
        async {
            match message.Id with
            | None -> return Error(Rejected(0, "reconciliation requires a stable message ID"))
            | Some id ->
                let! inbox = get (Routes.inbox session)
                match inbox with
                | Error error -> return Error error
                | Ok(Some(:? JsonArray as items)) when items |> Seq.exists (matches false) -> return Ok true
                | Ok(Some(:? JsonArray)) | Ok None ->
                    let! projected = get (Routes.message session id)
                    return projected |> Result.map (Option.exists (matches true))
                | Ok _ -> return Error(Unreachable "malformed inbox response")
        }

    /// Stream events until the connection ends.
    ///
    /// Returns `Ok` on a clean close and an error otherwise; in both cases the caller
    /// is expected to reconnect, because the stream ending is normal — it fails on
    /// overflow by design (DESIGN.md §9).
    member _.ReadEvents(onFrame: EventFrame -> unit, ct: CancellationToken, ?onConnected: unit -> unit) : Async<Result<unit, ApiError>> =
        async {
            match endpoint () with
            | Error error -> return Error error
            | Ok service ->
                try
                    use request =
                        new HttpRequestMessage(HttpMethod.Get, Uri(service.BaseUrl, Routes.Events))

                    request.Headers.Authorization <- authorisation service
                    request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "text/event-stream")

                    use handshake = CancellationTokenSource.CreateLinkedTokenSource ct
                    handshake.CancelAfter requestTimeout
                    use! response =
                        http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, handshake.Token)
                        |> Async.AwaitTask

                    if not response.IsSuccessStatusCode then
                        let! body = response.Content.ReadAsStringAsync handshake.Token |> Async.AwaitTask
                        return Error(Rejected(int response.StatusCode, body.Trim()))
                    else
                        handshake.CancelAfter Timeout.InfiniteTimeSpan
                        onConnected |> Option.iter (fun notify -> notify())
                        use! stream = response.Content.ReadAsStreamAsync ct |> Async.AwaitTask
                        use reader = new StreamReader(stream)
                        do! Sse.pump reader onFrame ct
                        return Ok()
                with
                | :? OperationCanceledException when ct.IsCancellationRequested -> return Ok()
                | :? OperationCanceledException ->
                    forget ()
                    return Error(Unreachable "event stream connection deadline exceeded")
                | error ->
                    forget ()
                    return Error(Unreachable error.Message)
        }

    interface IDisposable with
        member _.Dispose() = http.Dispose()

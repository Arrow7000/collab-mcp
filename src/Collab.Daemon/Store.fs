/// Durable state and audit are committed together before delivery intents escape.
namespace Collab.Daemon

open System
open System.IO
open System.Text.Json.Nodes
open Microsoft.Data.Sqlite
open Collab.Domain

type StateStore =
    abstract Load: unit -> RouterState
    abstract Save: state: RouterState * at: DateTimeOffset * action: string -> unit

/// Explicit wire shapes keep F# unions out of the SQLite serialization boundary.
module StateWire =
    let private obj (pairs: (string * string) list) =
        let node = JsonObject()
        for key, value in pairs do node[key] <- JsonValue.Create(value: string)
        node
    let private date (at: DateTimeOffset) = at.ToString "O"
    let private harness = function OpenCode -> "opencode" | ClaudeCode -> "claude"
    let private readHarness = function
        | "opencode" -> OpenCode | "claude" -> ClaudeCode
        | other -> failwith $"unknown stored harness '{other}'"
    let private text key node =
        Field.text key node |> Option.defaultWith (fun () -> failwith $"missing stored field '{key}'")
    let private at key node = DateTimeOffset.Parse(text key node, Globalization.CultureInfo.InvariantCulture)
    let private name key node =
        AgentName.create (text key node) |> Result.defaultWith (fun e -> failwithf "invalid stored name: %A" e)
    let private endpoint (e: Endpoint) =
        let (SessionId session), (Scope scope) = e.Session, e.Scope
        obj [ "harness", harness e.Harness; "session", session; "scope", scope ]
    let private readEndpoint node =
        { Harness = readHarness (text "harness" node)
          Session = SessionId(text "session" node); Scope = Scope(text "scope" node) }
    let private envelope (e: Envelope) =
        let (MessageId id) = e.Id
        let node =
            obj [ "id", string id; "from", AgentName.value e.From; "to", AgentName.value e.To
                  "body", e.Body; "urgency", (match e.Urgency with Interrupt -> "interrupt" | AtTurnBoundary -> "queue")
                  "sentAt", date e.SentAt ]
        node["legacyFormat"] <- JsonValue.Create e.LegacyFormat
        e.FromAddress |> Option.iter (fun address -> node["fromAddress"] <- JsonValue.Create address)
        e.FromPeer |> Option.iter (fun id -> node["fromPeer"] <- JsonValue.Create(PeerId.value id))
        e.ToPeer |> Option.iter (fun id -> node["toPeer"] <- JsonValue.Create(PeerId.value id))
        node
    let private peer key node =
        Field.text key node |> Option.map (fun raw ->
            PeerId.tryParse raw |> Option.defaultWith (fun () -> failwith $"invalid stored peer ID '{key}'"))
    let private readEnvelope node =
        { Id = MessageId(Guid.Parse(text "id" node))
          LegacyFormat = if isNull node["legacyFormat"] then false else node["legacyFormat"].GetValue<bool>()
          FromAddress = Field.text "fromAddress" node
          FromPeer = peer "fromPeer" node; ToPeer = peer "toPeer" node
          From = name "from" node; To = name "to" node
          Body = text "body" node; SentAt = at "sentAt" node
          Urgency = match text "urgency" node with
                    | "interrupt" -> Interrupt | "queue" -> AtTurnBoundary
                    | other -> failwith $"unknown stored urgency '{other}'" }
    let encode (state: RouterState) =
        let root = JsonObject()
        root["version"] <- JsonValue.Create 4
        let registrations = JsonArray()
        for _, r in Map.toList state.Registrations do
            let (Scope scope) = r.Scope
            let node = obj [ "name", AgentName.value r.Name; "scope", scope; "firstSeen", date r.FirstSeen ]
            node["peerId"] <- JsonValue.Create(PeerId.value r.Id)
            node["shortId"] <- JsonValue.Create r.ShortId
            let aliases = JsonArray()
            r.Aliases |> List.iter (fun alias -> aliases.Add(JsonValue.Create(AgentName.value alias)))
            node["aliases"] <- aliases
            match r.Binding with
            | Unbound -> node["binding"] <- JsonValue.Create "unbound"
            | Bound(e, since) ->
                node["binding"] <- JsonValue.Create "bound"
                node["endpoint"] <- endpoint e
                node["since"] <- JsonValue.Create(date since)
            registrations.Add node
        root["registrations"] <- registrations
        let pending = JsonArray()
        for mail in state.Pending do
            let (Scope scope) = mail.Scope
            let node = obj [ "scope", scope; "parkedAt", date mail.ParkedAt; "expiresAt", date mail.ExpiresAt
                             "purpose", (match mail.Purpose with PeerMessage -> "peer" | FailureNotice -> "notice") ]
            node["envelope"] <- envelope mail.Envelope
            match mail.Status with
            | Waiting retryAfter ->
                node["status"] <- JsonValue.Create "waiting"
                node["retryAfter"] <- JsonValue.Create(date retryAfter)
            | Delivering e ->
                node["status"] <- JsonValue.Create "delivering"
                node["endpoint"] <- endpoint e
            | Uncertain(e, detail) ->
                node["status"] <- JsonValue.Create "uncertain"
                node["endpoint"] <- endpoint e
                node["detail"] <- JsonValue.Create detail
            pending.Add node
        root["pending"] <- pending
        root.ToJsonString()

    let decode json : RouterState =
        let root = JsonNode.Parse(json: string)
        let version = root["version"].GetValue<int>()
        if version < 1 || version > 4 then failwith "unsupported state format"
        let registrations =
            root["registrations"].AsArray() |> Seq.map (fun node ->
                let agent, scope = name "name" node, Scope(text "scope" node)
                let binding =
                    match text "binding" node with
                    | "unbound" -> Unbound
                    | "bound" -> Bound(readEndpoint node["endpoint"], at "since" node)
                    | other -> failwith $"unknown stored binding '{other}'"
                let id =
                    if version = 1 then PeerId.legacy (Scope.key scope + "\u001f" + AgentName.key agent + "\u001f" + text "firstSeen" node)
                    else peer "peerId" node |> Option.defaultWith (fun () -> failwith "missing peer ID")
                let aliases =
                    if version = 1 then []
                    else node["aliases"].AsArray() |> Seq.map (fun n ->
                        AgentName.create(n.GetValue<string>()) |> Result.defaultWith (fun e -> failwithf "invalid alias %A" e)) |> Seq.toList
                PeerId.value id,
                { Id = id; ShortId = (if version >= 3 then PeerAddress.tryParse(text "shortId" node) |> Option.defaultWith (fun () -> failwith "invalid stored short ID") else ""); Aliases = aliases; Name = agent; Scope = scope; Binding = binding; FirstSeen = at "firstSeen" node })
            |> Map.ofSeq
        if registrations.Count <> root["registrations"].AsArray().Count then failwith "duplicate stored peer ID"
        let pending =
            root["pending"].AsArray() |> Seq.map (fun node ->
                { Envelope = readEnvelope node["envelope"]; Scope = Scope(text "scope" node)
                  ParkedAt = at "parkedAt" node; ExpiresAt = at "expiresAt" node
                  Purpose = match text "purpose" node with
                            | "peer" -> PeerMessage | "notice" -> FailureNotice
                            | other -> failwith $"unknown stored purpose '{other}'"
                  Status = match text "status" node with
                           | "waiting" -> Waiting(at "retryAfter" node)
                           | "delivering" -> Delivering(readEndpoint node["endpoint"])
                           | "uncertain" -> Uncertain(readEndpoint node["endpoint"], text "detail" node)
                           | other -> failwith $"unknown stored delivery state '{other}'" })
            |> Seq.toList
        let registrations =
            if version >= 3 then
                let addresses = registrations |> Map.toSeq |> Seq.map (snd >> _.ShortId) |> Seq.toList
                if addresses |> List.exists (fun a -> PeerAddress.tryParse a <> Some a) then failwith "invalid stored short ID"
                if (addresses |> Set.ofList |> Set.count) <> addresses.Length then failwith "duplicate stored short ID"
                registrations
            else
                let mutable reserved = registrations |> Map.toSeq |> Seq.map snd |> Seq.collect (fun r ->
                    seq { yield AgentName.key r.Name; yield! r.Aliases |> Seq.map AgentName.key }) |> Set.ofSeq
                registrations |> Map.map (fun _ r ->
                    let mutable attempt = -1
                    let candidate () =
                        attempt <- attempt + 1
                        if attempt = 0 then (PeerId.value r.Id).Substring(5, 8)
                        else
                            let bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(PeerId.value r.Id + ":" + string attempt))
                            Convert.ToHexString(bytes).ToLowerInvariant().Substring(0, 8)
                    let address = PeerAddress.allocate candidate reserved
                    reserved <- Set.add address reserved
                    { r with ShortId = address })
        let state = { Registrations = registrations; Pending = pending }
        if version = 1 then
            // Pin routing IDs while leaving already-submitted synthetic text unchanged.
            let migrated = pending |> List.map (fun mail ->
                let target = RouterState.lookup mail.Scope mail.Envelope.To state |> Option.map _.Id
                let sender = RouterState.lookup mail.Scope mail.Envelope.From state |> Option.map _.Id
                { mail with Envelope = { mail.Envelope with ToPeer = target; FromPeer = sender; LegacyFormat = true } })
            { state with Pending = migrated }
        else state

type SqliteStateStore(path: string) as this =
    let gate = obj ()
    let settings = SqliteConnectionStringBuilder(DataSource = path, Pooling = false)
    let connection = new SqliteConnection(settings.ToString())
    do
        // Create the file privately before SQLite opens it, including for isolated tests.
        use file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)
        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        file.Dispose()
        connection.Open()
        use schema = connection.CreateCommand()
        schema.CommandText <- """
PRAGMA synchronous=FULL;
CREATE TABLE IF NOT EXISTS state (id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS audit (id INTEGER PRIMARY KEY, at TEXT NOT NULL, action TEXT NOT NULL, payload TEXT NOT NULL);
"""
        schema.ExecuteNonQuery() |> ignore

    interface StateStore with
        member _.Load() = lock gate (fun () ->
            use command = connection.CreateCommand()
            command.CommandText <- "SELECT payload FROM state WHERE id=1"
            match command.ExecuteScalar() with
            | null -> RouterState.empty
            | value ->
                let payload = string value
                let loaded = StateWire.decode payload
                let root = JsonNode.Parse payload
                if root["version"].GetValue<int>() < 4 then
                    (this :> StateStore).Save(loaded, DateTimeOffset.UtcNow, "migrate prefix-free peer addresses")
                loaded)
        member _.Save(state, at, action) = lock gate (fun () ->
            let encoded = StateWire.encode state
            use transaction = connection.BeginTransaction()
            use command = connection.CreateCommand()
            command.Transaction <- transaction
            command.CommandText <- """
INSERT INTO state(id,payload) VALUES(1,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload;
INSERT INTO audit(at,action,payload) VALUES($at,$action,$payload);
DELETE FROM audit WHERE id NOT IN (SELECT id FROM audit ORDER BY id DESC LIMIT 64);
"""
            command.Parameters.AddWithValue("$payload", encoded) |> ignore
            command.Parameters.AddWithValue("$at", at.ToString "O") |> ignore
            command.Parameters.AddWithValue("$action", action) |> ignore
            command.ExecuteNonQuery() |> ignore
            transaction.Commit())
    interface IDisposable with member _.Dispose() = connection.Dispose()

module FileSecretStore

open System
open System.IO
open System.Text.Json
open System.Threading
open ToolUp.Platform.Secrets

/// Secret store with scoped file lookup and env-var fallback.
///
/// File layout (working directory):
///   - `secrets.json`            — `_platform` scope only (backward compat)
///   - `secrets-{scopeId}.json`  — per-scope secrets (e.g. `secrets-team-abc123.json`)
///
/// User-level fallback for `_platform` scope: `~/.toolup/secrets.json`
///
/// Each file is a flat JSON object: { "KEY": "value", ... }
/// Within a scope, lookup order is: working-dir file → user file → env var.
/// `_platform` scope also falls through to the bare env var (e.g. `ANTHROPIC_API_KEY`).
/// Per-scope env-var fallback uses `TOOLUP_{SCOPE}_{KEY}` naming so it never
/// collides with other scopes.
///
/// Override all file paths via TOOLUP_SECRETS_PATH or the constructor `path`
/// parameter. In override mode the specified file serves the `_platform` scope;
/// other scopes have no file source and fall through to env vars only.
///
/// Optional `baseDir` parameter overrides the default `Directory.GetCurrentDirectory()`
/// root used for resolving per-scope secret files. Tests pass a unique temp
/// directory per instance to avoid cwd-level collisions; production callers
/// leave it unset.
type FileSecretStore(?baseDir: string, ?path: string) =

    let resolvedBaseDir () =
        baseDir |> Option.defaultWith Directory.GetCurrentDirectory

    let parseSecrets (json: string) =
        let doc = JsonDocument.Parse json
        let mutable map = Map.empty

        for prop in doc.RootElement.EnumerateObject() do
            if prop.Value.ValueKind = JsonValueKind.String then
                map <- map |> Map.add prop.Name (prop.Value.GetString())

        map

    /// Synchronous read. Phase 6k kept this ONLY for the write path
    /// (`SetSecret` / `DeleteSecret`), whose read-modify-write runs
    /// inside `lock cacheLock` — a monitor cannot be held across an
    /// await, so the load there stays synchronous by construction. It
    /// is bounded by the same lock that serialises the write, never
    /// reached from the chat read path, and the file it reads was
    /// just resolved for writing on the same volume.
    let loadFile (filePath: string) =
        if File.Exists filePath then
            try
                parseSecrets (File.ReadAllText filePath)
            with _ ->
                Map.empty
        else
            Map.empty

    /// Phase 6k — the READ path's load. `File.ReadAllText` blocked a
    /// thread-pool thread for the whole duration of a stalled
    /// filesystem call (network share, AV scanner, slow disk): under
    /// load that is thread-pool starvation, and the AI chat path's
    /// 10 s secret-resolve timeout could not fire because the thread
    /// it would have to run its continuation on was the one blocked.
    /// `ReadAllTextAsync` releases the thread while the I/O is in
    /// flight, and the caller's cancellation token now reaches the
    /// read itself rather than only the surrounding timeout.
    let loadFileAsync (ct: CancellationToken) (filePath: string) : Async<Map<string, string>> = async {
        if not (File.Exists filePath) then
            return Map.empty
        else
            let! outcome = File.ReadAllTextAsync(filePath, ct) |> Async.AwaitTask |> Async.Catch

            match outcome with
            | Choice1Of2 json ->
                return
                    (try
                        parseSecrets json
                     with _ ->
                         Map.empty)
            | Choice2Of2 _ ->
                // A cancelled read is the CALLER's signal and must not
                // degrade to "this scope has no secrets" — that would
                // turn a timeout into a silent misconfiguration report,
                // which is the exact class of silent failure this phase
                // exists to remove. Every other failure (missing file,
                // malformed JSON, permission denied) keeps the historic
                // empty-map fallback.
                ct.ThrowIfCancellationRequested()
                return Map.empty
    }

    // Cache keyed by scopeId so lookups for different scopes do not share
    // data. `_platform` has its own entry; each team/user gets its own.
    let mutable cache: Map<string, Map<string, string>> = Map.empty

    // Guards every read/mutation of `cache`. Concurrent first-reads for the
    // same scope otherwise race on the load-then-store (benign data race);
    // SetSecret/DeleteSecret invalidations must also be serialised against
    // in-flight loads. Mirrors the SemaphoreSlim/lock pattern in
    // SingleKeyResolver + ShareTokenStore.
    let cacheLock = obj ()

    let sanitiseScope (scopeId: string) =
        scopeId.Replace('-', '_').ToUpperInvariant()

    let envVarName scopeId key =
        if scopeId = "_platform" then
            key
        else
            $"TOOLUP_{sanitiseScope scopeId}_{key}"

    let emptyMap: Async<Map<string, string>> = async { return Map.empty }

    /// Phase 6k — the read path is async end to end. The resolution
    /// order, precedence and empty-map fallbacks below are exactly what
    /// the synchronous version did; only the file reads changed.
    let loadForScopeAsync (ct: CancellationToken) scopeId : Async<Map<string, string>> = async {
        match cache |> Map.tryFind scopeId with
        | Some c -> return c
        | None ->
            let! secrets =
                match path with
                | Some p when scopeId = "_platform" ->
                    // Override path: applies only to platform scope
                    loadFileAsync ct p
                | Some _ ->
                    // Override mode: no per-scope file sources
                    emptyMap
                | None ->
                    // Phase 698 — the secrets PATH resolves through the
                    // Phase-696 `ConfigResolution` seam; the per-scope
                    // `TOOLUP_{SCOPE}_{KEY}` reads below stay direct, being an
                    // open-ended family the registry does not enumerate and a
                    // manifest therefore cannot name.
                    match ToolUp.Platform.ConfigResolution.tryValue ToolUp.Platform.ConfigKeys.Names.secretsPath with
                    | None -> async {
                        let dir = resolvedBaseDir ()

                        let! platformFile =
                            if scopeId = "_platform" then
                                loadFileAsync ct (Path.Combine(dir, "secrets.json"))
                            else
                                emptyMap

                        let! scopedFile =
                            if scopeId = "_platform" then
                                emptyMap
                            else
                                loadFileAsync ct (Path.Combine(dir, $"secrets-{scopeId}.json"))

                        let! userFile =
                            if scopeId = "_platform" then
                                let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile

                                loadFileAsync ct (Path.Combine(home, ".toolup", "secrets.json"))
                            else
                                emptyMap

                        // Precedence for _platform: app file > user file.
                        // For scoped lookups: only the scope file contributes.
                        if scopeId = "_platform" then
                            return
                                userFile
                                |> Map.fold (fun acc k v -> acc |> Map.add k v) Map.empty
                                |> fun merged -> platformFile |> Map.fold (fun acc k v -> acc |> Map.add k v) merged
                        else
                            return scopedFile
                      }
                    | Some p when scopeId = "_platform" -> loadFileAsync ct p
                    | Some _ -> emptyMap

            // Serialise the cache mutation against concurrent loads and
            // SetSecret/DeleteSecret invalidations; double-check inside the
            // lock so a scope another thread populated meanwhile isn't
            // overwritten (and a concurrent invalidation isn't lost).
            return
                lock cacheLock (fun () ->
                    match cache |> Map.tryFind scopeId with
                    | Some existing -> existing
                    | None ->
                        cache <- cache |> Map.add scopeId secrets
                        secrets)
    }

    // Resolve the file path used for writes on a given scope. Writes
    // always target the base directory (never env vars or the user-home
    // fallback); `_platform` writes go to `secrets.json`, other scopes
    // to `secrets-{scopeId}.json`. An explicit path override (ctor
    // `path` arg or `TOOLUP_SECRETS_PATH`) applies only to `_platform`.
    let writePathFor scopeId =
        let dir = resolvedBaseDir ()

        match path, ToolUp.Platform.ConfigResolution.tryValue ToolUp.Platform.ConfigKeys.Names.secretsPath with
        | Some p, _ when scopeId = "_platform" -> Some p
        | _, Some p when scopeId = "_platform" && not (String.IsNullOrEmpty p) -> Some p
        | _ when scopeId = "_platform" -> Some(Path.Combine(dir, "secrets.json"))
        | _, _ -> Some(Path.Combine(dir, $"secrets-{scopeId}.json"))

    // Phase 6l.J — local secret-file permissions. On Unix-like systems
    // (Linux, macOS), File.WriteAllText creates files with the parent
    // directory's umask (typically 022 = world-readable). Secrets must
    // not be world-readable. Tighten to user-only after every write.
    // On Windows, file permissions inherit from the parent directory's
    // ACL — typically Authenticated Users RX, which on a multi-user
    // box exposes secrets to every local account. The managed ACL API
    // (System.Security.AccessControl) needs a NuGet package the SDK
    // floor deliberately does not carry, so the Windows path shells out
    // to `icacls` (ships with every Windows install): strip inherited
    // ACEs and grant the current user only. Both paths are best-effort
    // defence-in-depth — the startup validator still warns operators to
    // prefer a cloud secret-manager ISecretStore for production.
    let hardenWindowsAcl (filePath: string) =
        try
            let account =
                let domain = Environment.UserDomainName
                let user = Environment.UserName

                if String.IsNullOrEmpty domain then
                    user
                else
                    $"{domain}\\{user}"

            let psi = System.Diagnostics.ProcessStartInfo()
            psi.FileName <- "icacls"
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.ArgumentList.Add filePath
            psi.ArgumentList.Add "/inheritance:r" // remove inherited ACEs (Authenticated Users RX)
            psi.ArgumentList.Add "/grant:r"
            psi.ArgumentList.Add $"{account}:(R,W)" // current user only

            let p = System.Diagnostics.Process.Start psi

            if not (isNull (box p)) then
                use p = p
                p.WaitForExit 5000 |> ignore
        with _ ->
            // icacls may be unavailable (Nano Server, locked-down PATH)
            // or the path may sit on a filesystem without ACL support.
            // Defence-in-depth — not the primary protection.
            ()

    let restrictPermissions (filePath: string) =
        if OperatingSystem.IsWindows() then
            hardenWindowsAcl filePath
        else
            try
                File.SetUnixFileMode(filePath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            with _ ->
                // SetUnixFileMode can throw on filesystems that don't
                // support Unix modes (FAT, exFAT). Best effort — the
                // permission tightening is defence-in-depth, not the
                // primary protection.
                ()

    let writeFile (filePath: string) (secrets: Map<string, string>) =
        let dir = Path.GetDirectoryName(filePath)

        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory dir |> ignore

        let opts = JsonSerializerOptions(WriteIndented = true)

        let obj = secrets |> Map.toSeq |> Seq.map (fun (k, v) -> k, box v) |> dict

        let json = JsonSerializer.Serialize(obj, opts)

        // Write to a temp file in the same directory, harden its
        // permissions, then atomically rename it over the target. A crash
        // mid-write can then never truncate the live secrets file, and a
        // concurrent reader sees either the old or the new complete file —
        // never a torn one. The temp lives in the same directory so the
        // rename stays on one volume (a cross-volume File.Move degrades to
        // copy+delete and loses atomicity); the GUID suffix avoids a
        // collision if two writers ever reach this point for sibling scope
        // files. Permissions are set on the temp so the secret content is
        // never momentarily world-readable at the final path.
        let tempPath = filePath + ".tmp-" + Guid.NewGuid().ToString("N")

        try
            File.WriteAllText(tempPath, json)
            restrictPermissions tempPath
            // Same-volume overwrite rename: MoveFileEx/MOVEFILE_REPLACE_EXISTING
            // on Windows, rename(2) on Unix — atomic on both.
            File.Move(tempPath, filePath, overwrite = true)
        with _ ->
            // Don't leave a stray temp file behind on failure.
            (try
                File.Delete tempPath
             with _ ->
                 ())

            reraise ()

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            // Phase 6k — the ambient cancellation token of whatever async
            // workflow called us. Every timeout the chat path wraps around
            // a secret resolve now reaches `File.ReadAllTextAsync` itself,
            // so a stalled filesystem is cancelled rather than merely
            // abandoned on a still-blocked thread-pool thread.
            let! ct = Async.CancellationToken
            let! secrets = loadForScopeAsync ct scopeId

            match secrets |> Map.tryFind key with
            | Some value -> return Some value
            | None ->
                match Environment.GetEnvironmentVariable(envVarName scopeId key) with
                | null
                | "" -> return None
                | value -> return Some value
        }

        member _.SetSecret(scopeId, key, value) = async {
            match writePathFor scopeId with
            | None -> return Error "No writable file location for this scope"
            | Some filePath ->
                try
                    // Serialise the whole read-modify-write against any
                    // concurrent writer to the same scope file, then evict
                    // the cache in the same critical section. Without the
                    // lock, two concurrent SetSecret calls each loadFile the
                    // old contents and each writeFile their own superset —
                    // last-writer-wins silently drops the other key (e.g. a
                    // just-rotated refresh token persisted alongside a racing
                    // access-token write). cacheLock is the same monitor
                    // loadForScope takes, so an in-flight load either reads
                    // the pre-write file or blocks until the atomic rename
                    // completes.
                    lock cacheLock (fun () ->
                        // Load current file contents (not env vars — we only
                        // persist to the file), merge new key, write back.
                        let current = loadFile filePath
                        let updated = current |> Map.add key value
                        writeFile filePath updated
                        // Invalidate the in-memory cache so subsequent
                        // GetSecret calls see the new value.
                        cache <- cache |> Map.remove scopeId)

                    return Ok()
                with ex ->
                    return Error ex.Message
        }

        member _.DeleteSecret(scopeId, key) = async {
            match writePathFor scopeId with
            | None -> return Error "No writable file location for this scope"
            | Some filePath ->
                try
                    // Serialise the read-modify-write + cache eviction against
                    // concurrent writers to the same scope file, exactly as
                    // SetSecret does — a delete racing a set on a different key
                    // must not resurrect or drop the other's write. The
                    // File.Exists probe sits inside the lock so it can't race a
                    // concurrent writer creating the file between the check and
                    // the read.
                    lock cacheLock (fun () ->
                        if File.Exists filePath then
                            let current = loadFile filePath
                            let updated = current |> Map.remove key
                            writeFile filePath updated
                            // Invalidate cache.
                            cache <- cache |> Map.remove scopeId)
                    // Idempotent — if the file didn't exist there was nothing
                    // to delete and no cache entry to evict.
                    return Ok()
                with ex ->
                    return Error ex.Message
        }

        member _.ListKeys(scopeId) = async {
            // Return only the file-backed keys for the scope — env-var
            // fallback keys are per-scope and not enumerable without
            // probing every possible name. Callers rotate known keys;
            // unknown env-var keys stay untouched.
            let! ct = Async.CancellationToken
            let! secrets = loadForScopeAsync ct scopeId
            return secrets |> Map.toList |> List.map fst
        }

    // Phase 464 — this store memoises a scope's whole secret map on
    // first read and, until now, evicted it only on its OWN
    // `SetSecret` / `DeleteSecret`. That is correct for one process and
    // wrong for several: a secret rotated on instance A stays cached on
    // instance B for the life of B's process — there is no TTL and no
    // periodic refresh, so the stale read never expires on its own.
    // Implementing `ISecretCacheInvalidation` lets a cross-instance
    // rotation broadcast reach this cache (first caller: the Phase 464
    // webhook signing-secret rotation fanout).
    /// Phase 457 — this store writes `secrets*.json` as flat JSON. Whatever
    /// protects those files is the medium's business (disk FDE, an encrypting
    /// volume, a KMS-managed bucket); the store itself does nothing to the
    /// values, and says so rather than leaving preflight to infer it.
    interface ISecretStoreAtRestPosture with
        member _.AtRestPosture =
            PlaintextAtRest "FileSecretStore writes secrets to secrets*.json as flat, unencrypted JSON"

    interface ISecretCacheInvalidation with
        member _.InvalidateScope(scopeId) =
            // Same monitor `SetSecret` / `DeleteSecret` take, so an
            // eviction can neither interleave with a read-modify-write
            // nor be lost to a concurrent load's double-check.
            lock cacheLock (fun () -> cache <- cache |> Map.remove scopeId)
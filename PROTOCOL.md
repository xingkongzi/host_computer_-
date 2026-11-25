# JMD updater protocol 1

The reusable module owns discovery, check orchestration, prompts, retry, signatures and installation. Project code owns check callbacks, real state freshness, data dirty flags and orderly shutdown. Unknown is never Ready.

## Host IPC

Windows named pipe `JmdUpdater.<pid>.<random>` restricted to the current user. One connection carries one UTF-8 JSON request and response, each terminated by LF and bounded to 65536 bytes. All property names are case-sensitive as shown in `Shared/Contracts.cs`. Every request includes `Protocol:1` and a random 64-hex `Token`. Host callbacks run on the proper UI/dispatch thread and must be quick, read-only queries of local state.

- `hello`: returns identity plus the fixed list of registered check IDs (status Unknown without actually running checks).
- `check`: returns every registered check once, status Ready/Blocked/Unknown and a project-owned message.
- `prepare`: includes a fresh 64-hex PrepareId and exact session AckPath. Host pauses new work/edits at its central dispatch boundary, then evaluates the same checks. Failure releases the boundary. Success returns Prepared and retains a 30-second host-owned lease.
- `cancel`: invalidates the matching prepare session and restores host operations. A stale request must never cancel a newer transaction.
- `shutdown`: requires the same PrepareId, exact AckPath and fresh successful checks. Host requests normal closure; it may still refuse. Final successful close writes `{PrepareId,Pid,ExecutablePath}` atomically to `%LOCALAPPDATA%/JmdUpdater/Sessions/<Token>/shutdown.json`. A matching response alone never authorizes file replacement.

Installer validates both shutdown acknowledgement and actual process termination. Failed IPC, refusal, timeout or missing confirmation stops this installation without killing the host. Preparation timeout must restore host operations even when the agent crashes.

Before preparing the host, the agent creates its session receipt directory and starts the installer. The installer acquires the installation lock, verifies/extracts the signed package, excludes other running instances, then atomically writes transaction `staged.json` containing `{PrepareId,HostPid}`. Only then does the agent request `prepare` and `shutdown`. Transaction `cancel.json` with the matching PrepareId invalidates an attempt before the first replacement. Expensive staging never consumes the host's 30-second preparation lease.

After successful preparation the agent checks that its installer is still alive and writes `shutdown-requested.json` containing `{PrepareId,HostPid}` immediately before requesting host closure. The installer starts ExitTimeout at this signal. Preflight failures are reported through `stage-failed.json` containing `{PrepareId,Code,Message}`; the agent owns the single user-facing preflight/cancel prompt.

## Startup and replacement

The installation mutex is `Local\JmdUpdater.Install.<hash>`, where hash is lowercase SHA-256 over UTF-8 of `Path.GetFullPath(installDir).TrimEnd('\\','/').ToUpperInvariant()`. Hosts check it before business initialization, so a user cannot start another instance during file replacement. Only the installer's restart with a validated transaction job may pass this boundary.

Job location: `%LOCALAPPDATA%/JmdUpdater/Transactions/<PrepareId>/job.json`, package `package.zip`, signed release `release.json`. Installer restarts with `JMD_UPDATE_READY_TOKEN=<PrepareId>` and `JMD_UPDATE_READY_PATH=<transaction>/ready.json`. After essential initialization the adapter atomically writes `{Token,Pid}` there. This confirms software initialization, not real hardware operation. Restart never resumes motor commands.

Before replacing a program file, the installer persists an Applying journal and installation-local `.jmd-update/pending.json` binding PrepareId and TransactionDirectory. Startup checks this marker even when no mutex survived a reboot. Nonterminal or corrupt recovery state blocks ordinary business startup and supplies the exact recovery job. Only the validated installer restart in WaitingForStartup may bypass the guard. Complete/RolledBack clears the matching pending marker; RollbackFailed and StartupUnconfirmed remain distinct, recoverable states.

## Publishing and configuration

`jmd-update.json` lives next to the host EXE; Enabled defaults to false. ServerUrl and enabled state can be edited with `JmdUpdater.exe settings --config <path>`. The initial server is `http://192.168.32.99:8787`; the listener and allowed CIDRs are separately configurable.

`<AppId>/latest.json` is a signed envelope `{Payload,Signature}`. Both are Base64. RSA-SHA256 signs the exact decoded UTF-8 payload bytes. Payload is ReleaseManifest, with ZIP hash and per-file inventory. Public trust key is deployed out of band; a release cannot replace its own trusted key or host configuration. Only `JmdUpdater/JmdUpdater.exe` and its optional `.exe.config` are managed inside that otherwise protected directory. They receive the same signed hashes, journal and rollback guarantees as other program files. User data preserve rules are explicit. No business database migration is performed by this first version.

Before starting `install --job`, Agent copies its executable and optional config into `<transaction>/installer/`, verifies each SHA-256 against its source, and runs the copied executable. Installed updater identity comes from `job.InstallDirectory`, never the temporary executable location. Before replacing it, Installer waits up to LockWaitSeconds for other processes with exactly that image path to exit (including settings windows); it never terminates them. The updater is not the product entry EXE and allows rename-aside fallback. Recovery, including `recover --restart true`, may restore its own mapped updater image using this fallback. Older installers reject the new inventory; clients need one new initial deployment before using these releases.

Only after both normal-shutdown receipt and host exit are verified does Installer start a topmost, non-cancellable WinForms window on its own STA thread. Installation stays on the original thread. Progress covers checks, i/n replacement and startup confirmation. Complete closes after about 1.5 seconds. After releasing the install mutex, RolledBack automatically reopens the original product and shows the reason for about five seconds; RollbackFailed / StartupUnconfirmed retain recovery instructions until closed by the user. `JMD_UPDATER_NO_UI=1` creates no UI thread/window and retains the unattended failure behavior without restarting.

# Real Agent flow tests

Windows/.NET Framework, no device or real product required:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Tests\AgentFlow\run.ps1
```

The runner compiles the current production Shared, Agent, Installer and Publisher sources into a separate test executable. Reflection invokes the production `AgentContext.Install` method, exactly as its install button does. Discovery uses the production Agent code and a production read-only `LocalUpdateServer` bound only to `127.0.0.1`. Installation runs in a real child process via `DeliveryCommands.Run("install", ...)`.

Two separately compiled generations of `FlowHost.exe` implement the synthetic product's named pipe and normal-exit/startup acknowledgements. They never load either JMD product or access hardware. Host trace records whether the real Installer's matching `staged.json` already existed at each prepare/shutdown request.

The valid path also confirms the matching `shutdown-requested.json` existed before the shutdown RPC. The runner rejects source changes during compilation and saves the compiled source hashes to ignored `output/source-snapshot.json`. A child Installer exception is saved inside its isolated transaction, and failed scenarios retain that transaction for diagnosis.

Four cases run:

1. A real signed release downloads, stages, requests host preparation/shutdown, receives the old host's normal-exit acknowledgement, replaces the executable and payload, and receives ready from generation 2. The real Installer must reach `Complete` and clear its pending gate.
2. The package bytes are deliberately invalid ZIP data, with a correctly signed matching size and package hash. Download must succeed, staging must fail, Agent must never request prepare/shutdown, the original host must remain responsive, and every installation file must retain its bytes.
3. Staging succeeds but the host refuses shutdown. Agent must promptly cancel preparation and installation, leave the host responsive with its gate released, and preserve every installation file.
4. The host takes six seconds to prepare while `ExitTimeoutSeconds=5`. Installation must still complete, proving the exit deadline starts at the explicit shutdown request rather than at staging or preparation.

`JMD_UPDATER_NO_UI=1` suppresses the updater's test popups. Only synthetic hosts are started; each stops normally through its unique named event. No process is killed. Random fixture keys/releases live under the temporary directory; real protocol transactions and sessions use unique tokens under the fixed `LocalAppData/JmdUpdater` hierarchy. The normal-success path removes its owned fixtures after confirming child processes exit. Failures retain the main fixture directory for diagnosis. Every recursive cleanup validates the resolved target beneath its named fixture parent and rejects reparse points.

The executing account needs write access to its own LocalAppData. These tests verify software orchestration and IPC, not real servo behavior or either product's operator workflow; separate project-adapter tests cover the Manager safety callbacks.

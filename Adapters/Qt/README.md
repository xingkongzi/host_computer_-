# Qt host adapter

This folder contains a reusable Qt 5.12/C++11 bridge. It depends on Qt Core and
Qt Network, and does not depend on Monitor's parameter, serial, or plotting classes.
The helper is deployed as `JmdUpdater/JmdUpdater.exe` beside the product executable.

Monitor references this independent sibling repository through
`../../JmdUpdater/Adapters/Qt/updatehost.pri` and adds `../..` to its include
path so existing `#include "JmdUpdater/Adapters/Qt/updatehost.h"` also works
in generated moc translation units.

Include `updatehost.pri` in a qmake project, or add `updatehost.cpp` and
`updatehost.h` to a Visual Studio project with Qt Network enabled. The class has no
`Q_OBJECT` macro and does not require an extra moc rule.

## Host lifecycle

1. After creating `QApplication`, before constructing application windows, call
   `UpdateHost::startupBlockReason(applicationDirPath())`. Refuse startup if the
   result is nonempty and display it. It distinguishes a live installer (wait)
   from an interrupted transaction (actual recovery command and job path).
   `canStartApplication()` remains a Boolean wrapper. The installer-designated replacement process is admitted only
   with a matching transaction/job/executable and `WaitingForStartup` journal.
2. Create one `UpdateHost` on the GUI thread, and call `configure` with the product
   ID, fallback release version, and absolute `jmd-update.json` path.
3. Register a fixed set of short, read-only checks. Each returns `Ready`,
   `Blocked(message)`, or `Unknown(message)`. Business state reading and messages
   belong to the product. Do not perform blocking device I/O from a callback.
4. Supply preparation/cancel callbacks. Begin must block new business operations
   and edits through the host's central dispatch mechanism. Cancel must be
   idempotent and restore every partially changed state, including a failed begin.
5. Supply a normal-close callback that returns whether the close was accepted.
   The accepted update close event must finish fallible persistence, then call
   `commitShutdown()`, and only then disconnect communication and accept closing.
   A failed receipt write must cancel closing without disconnecting the device.
6. Once the main window has initialized and the event loop is running, call
   `notifyStartupReady()` and `start()`. Startup acknowledgement is independently
   validated even if the optional update config is disabled.
7. Stop the adapter before destroying state captured by its callbacks.

`Enabled=false`, an absent config, or an absent helper makes `start()` inert.
`openSettings()` works independently of that switch. After enabling updates,
`checkNow()` starts an agent; it also detects an exited previous agent.

## Protocol and failure behavior

The current-user-only local pipe uses one UTF-8 JSON line per connection, maximum
64 KiB. Registered checks run on the GUI thread and must return promptly; the
agent has a bounded response wait and treats missing responses as unknown.
`hello` declares the check IDs without running callbacks. Prepare/check/shutdown
results use the same fixed set of IDs. A second prepare cannot replace an active
prepare. Cancellation and shutdown require the matching preparation ID.

Preparation expires after 30 seconds on a host-owned timer. Failed checks,
refused closes, cancellation, and expiry release the host and invalidate its
uncommitted shutdown receipt. After `commitShutdown()` succeeds, a late cancel
neither revokes the grant nor restores the already accepted closing host.
Successful startup acknowledgement also clears both one-use startup environment
variables so subsequently launched helpers cannot inherit that authorization.
The updater never sends a device command, saves a dirty
workspace, or terminates a product process. Installation requires both a matching
receipt and actual process exit.

The fixed shutdown-receipt directory is created and write-probed before entering
the host gate. No test or publisher needs to precreate a session directory.
Reparse points are rejected before and after creation. A durable
`.jmd-update/pending.json` also blocks startup after installer death: nonterminal,
missing, or unrecognized transaction states require recovery, even without a live
installer mutex. Only `Complete`/`RolledBack`, or an explicitly bound installer
child in `WaitingForStartup`, can pass the startup check.
Job/config/IPC input remains bounded to 64 KiB. Transaction journals have an
independent 8 MiB bound because the installer inventory can contain 20,001
records; valid journals larger than an IPC message must still permit the bound
replacement process to acknowledge startup.

## Monitor integration policy

Monitor's callbacks are implemented in `mainwindow.cpp`, outside this reusable
adapter. A session that never opened a device port explicitly has no controlled
device session. After any successful port open, disconnects and stale status
samples are unknown; they are never interpreted as a disabled motor. Live motor
checks accept only the stable disabled CiA402 states already defined by the
project, with no motor-rotating flag. Connected multi-axis devices are blocked as
unknown until all axes can be accounted for; the current protocol cache contains
only the selected axis.

Synchronous device operations are counted through `Parameters`; preparation
blocks the central `CommApp::sendDat` path and pauses the monitor timer. Active
control windows, tasks, acquisition, dirty parameter groups, and dirty curve
records block installation. Canceled or failed exports leave the dirty state set.
The original normal-close path remains separate from update-authorized closing.

## Cross-language checks

`Tests/QtWire` is a headless fixture and a real .NET
`NamedPipeClientStream` client. It exercises the pipe against `QLocalServer`,
session/path validation, check states, prepare/cancel identity, refusal and normal
exit receipts, the disabled switch, independent lease expiry, and .NET mutex/Qt
startup-gate compatibility. It never constructs a product window or opens a
device port. Build/run it in an isolated build directory, not a deployed product
directory: its `JmdUpdater.exe` is a test client, not the production helper.

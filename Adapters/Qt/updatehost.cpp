// JmdUpdater Qt adapter. UTF-8 JSON line protocol over a current-user local pipe.
// A successful prepare expires automatically; mere process exit is not an install grant.
#include "updatehost.h"

#include <QCoreApplication>
#include <QCryptographicHash>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QJsonDocument>
#include <QLocalServer>
#include <QLocalSocket>
#include <QProcess>
#include <QRegularExpression>
#include <QSaveFile>
#include <QScopedValueRollback>
#include <QUuid>
#include <QTemporaryFile>
#ifdef Q_OS_WIN
#include <windows.h>
#endif

namespace JmdUpdater {

static bool s_autoRecoveryLaunched = false;
static bool s_noticeDelegated = false;

bool UpdateHost::autoRecoveryLaunched() { return s_autoRecoveryLaunched; }
bool UpdateHost::quietExitRequested() { return s_autoRecoveryLaunched || s_noticeDelegated; }

// Starts the bundled updater detached and without a console window.
static bool launchUpdaterDetached(const QString &installDirectory, const QStringList &arguments, const QString &recoveryJob = QString())
{
#ifdef Q_OS_WIN
    // recoveryJob is bound to the transaction named by validated pending metadata.
    const QString copy = QDir(QFileInfo(recoveryJob).absolutePath()).filePath(QStringLiteral("installer/JmdUpdater.exe"));
    const QString helper = !recoveryJob.isEmpty() && QFileInfo::exists(copy) ? copy :
        QDir(installDirectory).filePath(QStringLiteral("JmdUpdater/JmdUpdater.exe"));
    if (!QFileInfo::exists(helper)) return false;
    QProcess process;
    process.setProgram(helper);
    process.setArguments(arguments);
    process.setWorkingDirectory(installDirectory);
    process.setCreateProcessArgumentsModifier([](QProcess::CreateProcessArguments *arguments) {
        arguments->flags |= CREATE_NO_WINDOW;
    });
    return process.startDetached();
#else
    Q_UNUSED(installDirectory); Q_UNUSED(arguments);
    return false;
#endif
}

// Starts "recover --restart": the updater waits for this process to exit, restores the previous
// version and reopens the program. Returns false when the helper is absent or could not start.
static bool launchAutoRecovery(const QString &installDirectory, const QString &jobPath)
{
    if (jobPath.isEmpty()) return false;
    return launchUpdaterDetached(installDirectory, QStringList() << QStringLiteral("recover") << QStringLiteral("--job")
        << QDir::toNativeSeparators(jobPath) << QStringLiteral("--restart") << QStringLiteral("true"), jobPath);
}

// While an install or recovery owns the directory, this process must not linger behind a dialog
// (the installer waits for every instance to exit). The notice is shown by Windows PowerShell instead,
// which works with any deployed updater version. The text travels Base64-encoded (no quoting issues).
static QString delegateBusyNotice(const QString &text)
{
#ifdef Q_OS_WIN
    wchar_t system[MAX_PATH];
    const UINT length = GetSystemDirectoryW(system, MAX_PATH);
    if (length > 0 && length < MAX_PATH) {
        const QString powershell = QString::fromWCharArray(system, static_cast<int>(length)) + QStringLiteral("\\WindowsPowerShell\\v1.0\\powershell.exe");
        QString escaped = text;
        escaped.replace(QLatin1Char('\''), QStringLiteral("''"));
        const QString script = QStringLiteral("Add-Type -AssemblyName System.Windows.Forms; [void][System.Windows.Forms.MessageBox]::Show('")
            + escaped + QStringLiteral("', '局域网更新', 'OK', 'Information')");
        const QByteArray utf16(reinterpret_cast<const char *>(script.utf16()), script.size() * 2);
        QProcess process;
        process.setProgram(powershell);
        process.setArguments(QStringList() << QStringLiteral("-NoProfile") << QStringLiteral("-NonInteractive")
            << QStringLiteral("-WindowStyle") << QStringLiteral("Hidden") << QStringLiteral("-EncodedCommand") << QString::fromLatin1(utf16.toBase64()));
        process.setCreateProcessArgumentsModifier([](QProcess::CreateProcessArguments *arguments) {
            arguments->flags |= CREATE_NO_WINDOW;
        });
        if (QFileInfo::exists(powershell) && process.startDetached()) s_noticeDelegated = true;
    }
#endif
    return text;
}

static bool isReparsePath(const QFileInfo &entry)
{
#ifdef Q_OS_WIN
    const QString path = QDir::toNativeSeparators(entry.absoluteFilePath());
    const DWORD attributes = GetFileAttributesW(reinterpret_cast<LPCWSTR>(path.utf16()));
    if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) return true;
#endif
    return entry.isSymLink();
}

CheckResult CheckResult::Ready(const QString &message)
{
    CheckResult result; result.status = QStringLiteral("Ready"); result.message = message; return result;
}
CheckResult CheckResult::Blocked(const QString &message)
{
    CheckResult result; result.status = QStringLiteral("Blocked"); result.message = message; return result;
}
CheckResult CheckResult::Unknown(const QString &message)
{
    CheckResult result; result.status = QStringLiteral("Unknown"); result.message = message; return result;
}

UpdateHost::UpdateHost(QObject *owner)
    : QObject(owner), m_server(nullptr), m_started(false), m_prepared(false),
      m_shutdownRequested(false), m_committed(false), m_handling(false), m_agentPid(0)
{
    m_lease.setSingleShot(true);
    QObject::connect(&m_lease, &QTimer::timeout, this, [this]() { cancelPreparation(); });
}

UpdateHost::~UpdateHost() { stop(); }

void UpdateHost::configure(const QString &appId, const QString &fallbackVersion,
                           const QString &configPath)
{
    if (m_started) return;
    m_appId = appId;
    m_version = fallbackVersion;
    m_configPath = QFileInfo(configPath).absoluteFilePath();
    QFile versionFile(QDir(QCoreApplication::applicationDirPath()).filePath(QStringLiteral("jmd-version.json")));
    if (versionFile.open(QIODevice::ReadOnly) && versionFile.size() <= 16384) {
        const QJsonObject version = QJsonDocument::fromJson(versionFile.readAll()).object();
        if (version.value(QStringLiteral("AppId")).toString() == m_appId &&
            !version.value(QStringLiteral("Version")).toString().isEmpty())
            m_version = version.value(QStringLiteral("Version")).toString();
    }
}

void UpdateHost::registerCheck(const QString &id, const std::function<CheckResult()> &check)
{
    if (!m_started && !id.isEmpty() && check) m_checks.insert(id, check);
}

void UpdateHost::setPreparationHandlers(const std::function<bool()> &begin,
                                        const std::function<void()> &cancel)
{
    if (!m_started) { m_begin = begin; m_cancel = cancel; }
}

void UpdateHost::setShutdownHandler(const std::function<bool()> &shutdown)
{
    if (!m_started) m_shutdown = shutdown;
}

void UpdateHost::start()
{
    if (m_started) return;
    QFile config(m_configPath);
    if (!config.open(QIODevice::ReadOnly) || config.size() > 65536) return;
    const QJsonObject options = QJsonDocument::fromJson(config.readAll()).object();
    if (!options.value(QStringLiteral("Enabled")).toBool(false)) return;
    const QString helper = QDir(QCoreApplication::applicationDirPath()).filePath(QStringLiteral("JmdUpdater/JmdUpdater.exe"));
    if (!QFileInfo::exists(helper) || m_checks.isEmpty() || !m_begin || !m_cancel || !m_shutdown) return;

    m_token = QUuid::createUuid().toString(QUuid::Id128) + QUuid::createUuid().toString(QUuid::Id128);
    m_pipeName = QStringLiteral("JmdUpdater.%1.%2").arg(QCoreApplication::applicationPid()).arg(QUuid::createUuid().toString(QUuid::Id128));
    m_server = new QLocalServer(this);
    m_server->setSocketOptions(QLocalServer::UserAccessOption);
    QObject::connect(m_server, &QLocalServer::newConnection, this, [this]() { acceptConnections(); });
    if (!m_server->listen(m_pipeName)) { delete m_server; m_server = nullptr; return; }

    QStringList arguments;
    arguments << QStringLiteral("agent") << QStringLiteral("--pipe") << m_pipeName
              << QStringLiteral("--token") << m_token << QStringLiteral("--pid")
              << QString::number(QCoreApplication::applicationPid()) << QStringLiteral("--config") << m_configPath;
    if (!launchHelper(arguments, &m_agentPid)) {
        m_server->close(); delete m_server; m_server = nullptr; return;
    }
    m_started = true;
}

void UpdateHost::checkNow()
{
#ifdef Q_OS_WIN
    if (m_started && m_agentPid > 0) {
        HANDLE agent = OpenProcess(SYNCHRONIZE, FALSE, static_cast<DWORD>(m_agentPid));
        const bool exited = !agent || WaitForSingleObject(agent, 0) == WAIT_OBJECT_0;
        if (agent) CloseHandle(agent);
        if (exited) stop();
    }
#endif
    if (!m_started) start();
#ifdef Q_OS_WIN
    if (m_started) {
        const QString eventName = QStringLiteral("Local\\JmdUpdater.Check.") + m_pipeName;
        HANDLE eventHandle = OpenEventW(EVENT_MODIFY_STATE, FALSE, reinterpret_cast<LPCWSTR>(eventName.utf16()));
        if (eventHandle) { SetEvent(eventHandle); CloseHandle(eventHandle); }
    }
#endif
}

bool UpdateHost::launchHelper(const QStringList &arguments, qint64 *pid) const
{
    QProcess process;
    process.setProgram(QDir(QCoreApplication::applicationDirPath()).filePath(QStringLiteral("JmdUpdater/JmdUpdater.exe")));
    process.setArguments(arguments);
    process.setWorkingDirectory(QCoreApplication::applicationDirPath());
#ifdef Q_OS_WIN
    process.setCreateProcessArgumentsModifier([](QProcess::CreateProcessArguments *arguments) {
        arguments->flags |= CREATE_NO_WINDOW;
    });
#endif
    return process.startDetached(pid);
}

bool UpdateHost::openSettings()
{
    return launchHelper(QStringList() << QStringLiteral("settings") << QStringLiteral("--config") << m_configPath);
}

void UpdateHost::stop()
{
    if (!m_committed) cancelPreparation();
    m_lease.stop();
    if (m_server) { m_server->close(); delete m_server; m_server = nullptr; }
    m_started = false;
    m_agentPid = 0;
}

QJsonArray UpdateHost::runChecks(bool *allReady)
{
    QJsonArray results;
    *allReady = !m_checks.isEmpty();
    for (auto it = m_checks.constBegin(); it != m_checks.constEnd(); ++it) {
        CheckResult result;
        try { result = it.value()(); }
        catch (...) { result = CheckResult::Unknown(QStringLiteral("检查发生异常，请稍后重试。")); }
        if (result.status != QStringLiteral("Ready") && result.status != QStringLiteral("Blocked") && result.status != QStringLiteral("Unknown"))
            result = CheckResult::Unknown(QStringLiteral("检查未返回有效状态。"));
        QJsonObject item;
        item.insert(QStringLiteral("Id"), it.key()); item.insert(QStringLiteral("Status"), result.status);
        item.insert(QStringLiteral("Message"), result.message);
        results.append(item);
        if (result.status != QStringLiteral("Ready")) *allReady = false;
    }
    return results;
}

void UpdateHost::cancelPreparation()
{
    // Once a normal close was committed, cancellation must not resurrect the host or revoke its grant.
    if (m_committed) return;
    m_lease.stop();
    const bool wasPrepared = m_prepared;
    if (!m_ackPath.isEmpty()) QFile::remove(m_ackPath);
    m_committed = false;
    m_prepared = false; m_shutdownRequested = false; m_prepareId.clear(); m_ackPath.clear();
    if (wasPrepared && m_cancel) { try { m_cancel(); } catch (...) { } }
}

QString UpdateHost::receiptPath(const QString &group, const QString &token,
                                const QString &fileName, const QString &supplied) const
{
    if (!QRegularExpression(QStringLiteral("^[0-9a-f]{64}$")).match(token).hasMatch()) return QString();
    const QString localData = qEnvironmentVariable("LOCALAPPDATA");
    if (localData.isEmpty()) return QString();
    const QString expected = QDir::cleanPath(QDir(localData).filePath(QStringLiteral("JmdUpdater/") + group + QLatin1Char('/') + token + QLatin1Char('/') + fileName));
    if (QDir::cleanPath(QFileInfo(supplied).absoluteFilePath()).compare(expected, Qt::CaseInsensitive) != 0) return QString();
    QFileInfo directory(QFileInfo(expected).absolutePath());
    while (true) {
        if (isReparsePath(directory)) return QString();
        const QString parent = directory.absolutePath();
        if (parent == directory.absoluteFilePath()) break;
        directory.setFile(parent);
    }
    return expected;
}

bool UpdateHost::writeReceipt(const QString &path, const QJsonObject &receipt) const
{
    if (path.isEmpty() || !ensureReceiptDirectory(path)) return false;
    QSaveFile file(path);
    if (!file.open(QIODevice::WriteOnly)) return false;
    const QByteArray data = QJsonDocument(receipt).toJson(QJsonDocument::Compact);
    return file.write(data) == data.size() && file.commit();
}

bool UpdateHost::ensureReceiptDirectory(const QString &path) const
{
    if (path.isEmpty() || isReparsePath(QFileInfo(path))) return false;
    const QString parent = QFileInfo(path).absolutePath();
    QFileInfo cursor(parent);
    while (true) {
        if (isReparsePath(cursor)) return false;
        const QString next = cursor.absolutePath();
        if (next == cursor.absoluteFilePath()) break;
        cursor.setFile(next);
    }
    if (!QDir().mkpath(parent)) return false;
    cursor.setFile(parent);
    while (true) {
        if (isReparsePath(cursor)) return false;
        const QString next = cursor.absolutePath();
        if (next == cursor.absoluteFilePath()) break;
        cursor.setFile(next);
    }
    return QFileInfo(parent).isDir();
}

bool UpdateHost::probeReceiptDirectory(const QString &path) const
{
    if (!ensureReceiptDirectory(path)) return false;
    QTemporaryFile probe(QDir(QFileInfo(path).absolutePath()).filePath(QStringLiteral(".write-probe-XXXXXX")));
    return probe.open() && probe.write("1", 1) == 1 && probe.flush();
}

bool UpdateHost::commitShutdown()
{
    if (!m_prepared || !m_shutdownRequested || m_prepareId.isEmpty()) return false;
    QJsonObject receipt;
    receipt.insert(QStringLiteral("PrepareId"), m_prepareId);
    receipt.insert(QStringLiteral("Pid"), static_cast<double>(QCoreApplication::applicationPid()));
    // Windows-native separators: updater agents compare this text with paths normalized by .NET.
    receipt.insert(QStringLiteral("ExecutablePath"), QDir::toNativeSeparators(QCoreApplication::applicationFilePath()));
    m_committed = writeReceipt(m_ackPath, receipt);
    if (m_committed) m_lease.stop();
    return m_committed;
}

void UpdateHost::notifyStartupReady()
{
    QString token, path;
    if (!startupContext(&path, &token)) return;
    QJsonObject receipt;
    receipt.insert(QStringLiteral("Token"), token);
    receipt.insert(QStringLiteral("Pid"), static_cast<double>(QCoreApplication::applicationPid()));
    if (writeReceipt(path, receipt)) {
        qunsetenv("JMD_UPDATE_READY_TOKEN");
        qunsetenv("JMD_UPDATE_READY_PATH");
    }
}

bool UpdateHost::startupContext(QString *path, QString *token) const
{
    *token = qEnvironmentVariable("JMD_UPDATE_READY_TOKEN");
    *path = receiptPath(QStringLiteral("Transactions"), *token, QStringLiteral("ready.json"), qEnvironmentVariable("JMD_UPDATE_READY_PATH"));
    if (path->isEmpty()) return false;
    QString state;
    return transactionState(*token, QFileInfo(*path).absolutePath(), &state) && state == QStringLiteral("WaitingForStartup");
}

bool UpdateHost::transactionState(const QString &token, const QString &directory, QString *state) const
{
    const QString expectedReceipt = QDir(directory).filePath(QStringLiteral("ready.json"));
    if (receiptPath(QStringLiteral("Transactions"), token, QStringLiteral("ready.json"), expectedReceipt).isEmpty()) return false;
    QFile job(QDir(directory).filePath(QStringLiteral("job.json")));
    QFile journal(QDir(directory).filePath(QStringLiteral("journal.json")));
    if (isReparsePath(QFileInfo(job)) || isReparsePath(QFileInfo(journal))) return false;
    // A journal contains the installer inventory (up to 20,001 records), unlike IPC/job metadata.
    if (!job.open(QIODevice::ReadOnly) || job.size() > 65536 ||
        !journal.open(QIODevice::ReadOnly) || journal.size() > 8388608) return false;
    const QJsonObject jobData = QJsonDocument::fromJson(job.readAll()).object();
    const QJsonObject journalData = QJsonDocument::fromJson(journal.readAll()).object();
    if (jobData.value(QStringLiteral("PrepareId")).toString() != token ||
        journalData.value(QStringLiteral("PrepareId")).toString() != token) return false;
    *state = journalData.value(QStringLiteral("State")).toString();
    if (state->isEmpty()) return false;
    if (!m_appId.isEmpty() && jobData.value(QStringLiteral("AppId")).toString() != m_appId) return false;
    const QString install = jobData.value(QStringLiteral("InstallDirectory")).toString();
    const QString executable = jobData.value(QStringLiteral("ExecutablePath")).toString();
    if (install.isEmpty() || executable.isEmpty()) return false;
    return QDir::cleanPath(QFileInfo(install).absoluteFilePath()).compare(QDir::cleanPath(QCoreApplication::applicationDirPath()), Qt::CaseInsensitive) == 0 &&
           QDir::cleanPath(QFileInfo(executable).absoluteFilePath()).compare(QDir::cleanPath(QCoreApplication::applicationFilePath()), Qt::CaseInsensitive) == 0;
}

bool UpdateHost::isPrepared() const { return m_prepared; }
bool UpdateHost::isShutdownRequested() const { return m_shutdownRequested; }

bool UpdateHost::canStartApplication(const QString &installDirectory)
{
    return startupBlockReason(installDirectory).isEmpty();
}

QString UpdateHost::startupBlockReason(const QString &installDirectory)
{
#ifdef Q_OS_WIN
    UpdateHost verifier;
    QString pendingReason;
    QString recoverableJob;     // a valid, unfinished transaction that recover --restart may handle
    bool authorizedStartup = false;
    const auto recoverCommand = [&installDirectory](const QString &transaction) {
        const QString copy = QDir(transaction).filePath(QStringLiteral("installer/JmdUpdater.exe"));
        const QString helper = QDir::toNativeSeparators(QFileInfo::exists(copy) ? copy :
            QDir(installDirectory).filePath(QStringLiteral("JmdUpdater/JmdUpdater.exe")));
        const QString job = QDir::toNativeSeparators(QDir(transaction).filePath(QStringLiteral("job.json")));
        return QLatin1Char('"') + helper + QStringLiteral("\" recover --job \"") + job + QLatin1Char('"');
    };
    const QString pendingPath = QDir(installDirectory).filePath(QStringLiteral(".jmd-update/pending.json"));
    QFile pending(pendingPath);
    if (QFileInfo::exists(pendingPath) || isReparsePath(QFileInfo(pending))) {
        if (isReparsePath(QFileInfo(pending)) || isReparsePath(QFileInfo(QFileInfo(pending).absolutePath())) ||
            !pending.open(QIODevice::ReadOnly) || pending.size() > 16384) {
            pendingReason = QStringLiteral("无法读取更新恢复记录，请联系发布人员检查以下文件后再启动：\n") + QDir::toNativeSeparators(pendingPath);
        } else {
            const QJsonObject pointer = QJsonDocument::fromJson(pending.readAll()).object();
            const QString pendingToken = pointer.value(QStringLiteral("PrepareId")).toString();
            const QString transaction = pointer.value(QStringLiteral("TransactionDirectory")).toString();
            QString state;
            const bool boundPath = !transaction.isEmpty() && !verifier.receiptPath(QStringLiteral("Transactions"), pendingToken,
                QStringLiteral("ready.json"), QDir(transaction).filePath(QStringLiteral("ready.json"))).isEmpty();
            if (!boundPath || !verifier.transactionState(pendingToken, transaction, &state)) {
                pendingReason = QStringLiteral("更新事务记录缺失、超限或与本程序不匹配，请先检查并恢复更新。\n");
                pendingReason += boundPath ? recoverCommand(transaction) : QDir::toNativeSeparators(pendingPath);
            } else if (state != QStringLiteral("Complete") && state != QStringLiteral("RolledBack")) {
                pendingReason = QStringLiteral("上次更新尚未完成（%1），请先执行恢复命令再启动：\n%2").arg(state, recoverCommand(transaction));
                recoverableJob = QDir(transaction).filePath(QStringLiteral("job.json"));
                QString startupPath, startupToken;
                authorizedStartup = verifier.startupContext(&startupPath, &startupToken) && startupToken == pendingToken;
            }
        }
    } else {
        QString startupPath, startupToken;
        authorizedStartup = verifier.startupContext(&startupPath, &startupToken);
    }
    QString normalized = QDir::toNativeSeparators(QFileInfo(installDirectory).absoluteFilePath());
    while (normalized.endsWith(QLatin1Char('\\'))) normalized.chop(1);
    const QString hash = QString::fromLatin1(QCryptographicHash::hash(normalized.toUpper().toUtf8(), QCryptographicHash::Sha256).toHex());
    const QString name = QStringLiteral("Local\\JmdUpdater.Install.") + hash;
    // No installer holds the lock: an unfinished update is restored automatically and the program
    // reopened by the updater. Only if that cannot be started are the manual instructions shown.
    const auto unlockedResult = [&]() -> QString {
        if (authorizedStartup) return QString();
        if (!recoverableJob.isEmpty() && launchAutoRecovery(installDirectory, recoverableJob)) {
            s_autoRecoveryLaunched = true;
            return QStringLiteral("上次更新没有完成，正在自动恢复上一版本，完成后软件会自动重新打开。");
        }
        return pendingReason;
    };
    HANDLE mutexHandle = OpenMutexW(SYNCHRONIZE | MUTEX_MODIFY_STATE, FALSE, reinterpret_cast<LPCWSTR>(name.utf16()));
    if (!mutexHandle) {
        if (GetLastError() == ERROR_FILE_NOT_FOUND) return unlockedResult();
        return QStringLiteral("无法确认更新安装锁状态，请检查本机权限后再启动。\n") + pendingReason;
    }
    const DWORD wait = WaitForSingleObject(mutexHandle, 0);
    if (wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED) {
        // WAIT_ABANDONED grants ownership too. The durable pending journal decides recovery.
        ReleaseMutex(mutexHandle); CloseHandle(mutexHandle);
        return unlockedResult();
    }
    CloseHandle(mutexHandle);
    if (wait == WAIT_TIMEOUT) {
        return authorizedStartup ? QString() : delegateBusyNotice(
            QStringLiteral("软件正在更新或恢复上一版本，请等待完成后再打开（完成后会自动打开）。"));
    }
    return QStringLiteral("无法读取更新安装锁状态，请稍后重试。\n") + pendingReason;
#else
    Q_UNUSED(installDirectory);
    return QString();
#endif
}

QJsonObject UpdateHost::dispatch(const QJsonObject &request)
{
    QJsonObject reply;
    reply.insert(QStringLiteral("Ok"), false);
    if (request.value(QStringLiteral("Protocol")).toInt() != 1 || request.value(QStringLiteral("Token")).toString() != m_token) {
        reply.insert(QStringLiteral("Message"), QStringLiteral("Invalid protocol or session.")); return reply;
    }
    if (m_handling) { reply.insert(QStringLiteral("Message"), QStringLiteral("Host is busy.")); return reply; }
    QScopedValueRollback<bool> handlingGuard(m_handling, true);
    const QString command = request.value(QStringLiteral("Command")).toString();
    reply.insert(QStringLiteral("AppId"), m_appId); reply.insert(QStringLiteral("Version"), m_version);
    reply.insert(QStringLiteral("InstallDirectory"), QDir::toNativeSeparators(QCoreApplication::applicationDirPath()));
    reply.insert(QStringLiteral("ExecutablePath"), QDir::toNativeSeparators(QCoreApplication::applicationFilePath()));
    reply.insert(QStringLiteral("Pid"), static_cast<double>(QCoreApplication::applicationPid()));
    bool ok = true;
    if (command == QStringLiteral("hello")) {
        QJsonArray declared;
        for (auto it = m_checks.constBegin(); it != m_checks.constEnd(); ++it) {
            QJsonObject item; item.insert(QStringLiteral("Id"), it.key());
            item.insert(QStringLiteral("Status"), QStringLiteral("Unknown"));
            item.insert(QStringLiteral("Message"), QStringLiteral("未执行")); declared.append(item);
        }
        reply.insert(QStringLiteral("Checks"), declared);
    }
    else if (command == QStringLiteral("check")) {
        bool ready; reply.insert(QStringLiteral("Checks"), runChecks(&ready));
    } else if (command == QStringLiteral("prepare")) {
        const QString prepareId = request.value(QStringLiteral("PrepareId")).toString();
        const QString ack = receiptPath(QStringLiteral("Sessions"), m_token, QStringLiteral("shutdown.json"), request.value(QStringLiteral("AckPath")).toString());
        if (m_prepared || !QRegularExpression(QStringLiteral("^[0-9a-f]{64}$")).match(prepareId).hasMatch() || ack.isEmpty()) ok = false;
        else if (!probeReceiptDirectory(ack)) {
            ok = false;
            reply.insert(QStringLiteral("Message"), QStringLiteral("更新确认目录不可写，请检查本地目录权限后重试。"));
        }
        else {
            // Set the flag first so any partial failure invokes the idempotent cancel hook.
            m_prepared = true;
            bool began = false;
            try { began = m_begin && m_begin(); } catch (...) { began = false; }
            bool ready = false;
            reply.insert(QStringLiteral("Checks"), runChecks(&ready));
            if (!began || !ready) {
                cancelPreparation(); ok = false;
            } else {
                m_prepareId = prepareId; m_ackPath = ack;
                m_lease.start(30000);
            }
        }
    } else if (command == QStringLiteral("cancel")) {
        if (!m_prepareId.isEmpty() && request.value(QStringLiteral("PrepareId")).toString() != m_prepareId) ok = false;
        else cancelPreparation();
    } else if (command == QStringLiteral("shutdown")) {
        if (!m_prepared || request.value(QStringLiteral("PrepareId")).toString() != m_prepareId) ok = false;
        else {
            bool ready = false; reply.insert(QStringLiteral("Checks"), runChecks(&ready));
            if (!ready) { cancelPreparation(); ok = false; }
            else {
                m_shutdownRequested = true;
                bool closing = false;
                try { closing = m_shutdown && m_shutdown(); } catch (...) { closing = false; }
                if (!closing) { cancelPreparation(); ok = false; }
            }
        }
    } else ok = false;
    reply.insert(QStringLiteral("Ok"), ok);
    reply.insert(QStringLiteral("Prepared"), m_prepared);
    reply.insert(QStringLiteral("PrepareId"), m_prepareId);
    if (!ok && !reply.contains(QStringLiteral("Message"))) reply.insert(QStringLiteral("Message"), QStringLiteral("更新准备未完成，请处理检查项后重试。"));
    return reply;
}

void UpdateHost::acceptConnections()
{
    while (m_server && m_server->hasPendingConnections()) {
        QLocalSocket *socket = m_server->nextPendingConnection();
        socket->setReadBufferSize(65537);
        QTimer::singleShot(10000, socket, [socket]() { socket->abort(); socket->deleteLater(); });
        QObject::connect(socket, &QLocalSocket::disconnected, socket, &QObject::deleteLater);
        QObject::connect(socket, &QLocalSocket::readyRead, this, [this, socket]() {
            QByteArray data = socket->property("jmdBuffer").toByteArray();
            data += socket->readAll();
            if (data.size() > 65536) { socket->abort(); return; }
            const int newline = data.indexOf('\n');
            if (newline < 0) { socket->setProperty("jmdBuffer", data); return; }
            QJsonParseError error;
            const QJsonDocument document = QJsonDocument::fromJson(data.left(newline), &error);
            QJsonObject reply;
            if (error.error == QJsonParseError::NoError && document.isObject()) reply = dispatch(document.object());
            else { reply.insert(QStringLiteral("Ok"), false); reply.insert(QStringLiteral("Message"), QStringLiteral("Invalid JSON request.")); }
            socket->write(QJsonDocument(reply).toJson(QJsonDocument::Compact) + '\n');
            socket->flush(); socket->disconnectFromServer();
        });
    }
}

} // namespace JmdUpdater

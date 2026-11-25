// JmdUpdater Qt adapter: project-owned callbacks run on the GUI thread.
// The adapter owns no motor or workspace policy. Unknown results block installation.
#ifndef JMDUPDATER_QT_UPDATEHOST_H
#define JMDUPDATER_QT_UPDATEHOST_H

#include <QObject>
#include <QJsonArray>
#include <QJsonObject>
#include <QMap>
#include <QTimer>
#include <functional>

class QLocalServer;
class QLocalSocket;

namespace JmdUpdater {

struct CheckResult
{
    QString id;
    QString status;
    QString message;

    static CheckResult Ready(const QString &message = QString());
    static CheckResult Blocked(const QString &message);
    static CheckResult Unknown(const QString &message);
};

class UpdateHost : public QObject
{
public:
    explicit UpdateHost(QObject *owner = nullptr);
    ~UpdateHost();

    // Configure before start. configPath points to the host's optional update config.
    void configure(const QString &appId, const QString &fallbackVersion,
                   const QString &configPath);
    // Register stable IDs before start. Callbacks must be short, read-only and local.
    void registerCheck(const QString &id, const std::function<CheckResult()> &check);
    // begin temporarily blocks new host operations; cancel restores them on any abort.
    void setPreparationHandlers(const std::function<bool()> &begin,
                                const std::function<void()> &cancel);
    // Return true only when a normal close has been requested; never force termination.
    void setShutdownHandler(const std::function<bool()> &shutdown);
    void start();
    void checkNow();
    bool openSettings();
    void stop();
    // Invoke after the main UI initialized, even when background updating is disabled.
    void notifyStartupReady();
    // Invoke after fallible persistence, before irreversible disconnect/accepted close.
    // The installer requires this grant AND actual process exit; a grant alone cannot install.
    bool commitShutdown();
    bool isPrepared() const;
    bool isShutdownRequested() const;
    // Call before constructing application windows, so an active file swap cannot race a launch.
    static bool canStartApplication(const QString &installDirectory);
    // Empty means allowed; otherwise show the returned wait/recovery instructions verbatim.
    // Exception: when autoRecoveryLaunched() is true afterwards, exit quietly; the updater is restoring
    // the previous version and will reopen the program itself.
    static QString startupBlockReason(const QString &installDirectory);
    static bool autoRecoveryLaunched();
    // True when the program must exit immediately without its own dialog: either auto-recovery was started,
    // or an install/recovery is in progress and the updater is showing the notice instead.
    static bool quietExitRequested();

private:
    QJsonArray runChecks(bool *allReady);
    QJsonObject dispatch(const QJsonObject &request);
    void acceptConnections();
    void cancelPreparation();
    QString receiptPath(const QString &group, const QString &token,
                        const QString &fileName, const QString &supplied) const;
    bool writeReceipt(const QString &path, const QJsonObject &receipt) const;
    bool startupContext(QString *path, QString *token) const;
    bool transactionState(const QString &token, const QString &directory, QString *state) const;
    bool ensureReceiptDirectory(const QString &path) const;
    bool probeReceiptDirectory(const QString &path) const;
    bool launchHelper(const QStringList &arguments, qint64 *pid = nullptr) const;

    QString m_appId;
    QString m_version;
    QString m_configPath;
    QString m_pipeName;
    QString m_token;
    QString m_prepareId;
    QString m_ackPath;
    QMap<QString, std::function<CheckResult()> > m_checks;
    std::function<bool()> m_begin;
    std::function<void()> m_cancel;
    std::function<bool()> m_shutdown;
    QLocalServer *m_server;
    QTimer m_lease;
    bool m_started;
    bool m_prepared;
    bool m_shutdownRequested;
    bool m_committed;
    bool m_handling;
    qint64 m_agentPid;
};

} // namespace JmdUpdater
#endif

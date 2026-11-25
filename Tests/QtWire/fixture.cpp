// Cross-language protocol fixture. No product windows, serial ports or hardware are used.
#include "updatehost.h"
#include <QCoreApplication>
#include <QDir>
#include <QFile>
#include <QJsonDocument>
#include <QSaveFile>
#include <QTimer>

static void writeText(const QString &name, const QByteArray &text)
{
    QSaveFile file(QDir(QCoreApplication::applicationDirPath()).filePath(name));
    if (file.open(QIODevice::WriteOnly)) { file.write(text); file.commit(); }
}

int main(int argc, char **argv)
{
    QCoreApplication app(argc, argv);
    if (app.arguments().contains(QStringLiteral("--lock-probe")))
        return JmdUpdater::UpdateHost::canStartApplication(app.applicationDirPath()) ? 10 : 0;
    if (app.arguments().contains(QStringLiteral("--unlocked-probe")))
        return JmdUpdater::UpdateHost::canStartApplication(app.applicationDirPath()) ? 0 : 11;
    if (app.arguments().contains(QStringLiteral("--waiting-reason-probe"))) {
        const QString reason = JmdUpdater::UpdateHost::startupBlockReason(app.applicationDirPath());
        writeText(QStringLiteral("startup-reason.txt"), reason.toUtf8());
        return !reason.isEmpty() && !reason.contains(QStringLiteral("recover --job")) ? 0 : 14;
    }
    if (app.arguments().contains(QStringLiteral("--recovery-reason-probe"))) {
        const QString reason = JmdUpdater::UpdateHost::startupBlockReason(app.applicationDirPath());
        writeText(QStringLiteral("startup-reason.txt"), reason.toUtf8());
        return reason.contains(QStringLiteral("recover --job")) ? 0 : 15;
    }
    if (app.arguments().contains(QStringLiteral("--ready-probe")) ||
        app.arguments().contains(QStringLiteral("--ready-write-failure-probe"))) {
        if (!JmdUpdater::UpdateHost::canStartApplication(app.applicationDirPath())) return 12;
        const QString readyPath = qEnvironmentVariable("JMD_UPDATE_READY_PATH");
        const QString readyToken = qEnvironmentVariable("JMD_UPDATE_READY_TOKEN");
        const bool writeFailure = app.arguments().contains(QStringLiteral("--ready-write-failure-probe"));
        if (writeFailure && !QDir().mkdir(readyPath)) return 16;
        JmdUpdater::UpdateHost probe;
        probe.configure(QStringLiteral("QtWireFixture"), QStringLiteral("1.0.0.0"), QStringLiteral("unused.json"));
        probe.notifyStartupReady();
        if (writeFailure) {
            const bool preserved = qEnvironmentVariable("JMD_UPDATE_READY_PATH") == readyPath &&
                qEnvironmentVariable("JMD_UPDATE_READY_TOKEN") == readyToken;
            QDir().rmdir(readyPath);
            return preserved ? 0 : 17;
        }
        return QFile::exists(readyPath) && qEnvironmentVariable("JMD_UPDATE_READY_PATH").isEmpty() &&
            qEnvironmentVariable("JMD_UPDATE_READY_TOKEN").isEmpty() ? 0 : 13;
    }
    const bool disabled = app.arguments().contains(QStringLiteral("--disabled"));
    const bool lease = app.arguments().contains(QStringLiteral("--lease"));
    writeText(QStringLiteral("fixture-mode.txt"), disabled ? "disabled" : (lease ? "lease" : "wire"));
    writeText(QStringLiteral("jmd-update.json"), disabled ? "{\"Enabled\":false}" : "{\"Enabled\":true}");
    QFile::remove(QDir(app.applicationDirPath()).filePath(QStringLiteral("client-result.txt")));
    QFile::remove(QDir(app.applicationDirPath()).filePath(QStringLiteral("client-started.txt")));
    int checks = 0, begins = 0, cancels = 0, shutdowns = 0;
    JmdUpdater::UpdateHost host;
    host.configure(QStringLiteral("QtWireFixture"), QStringLiteral("1.0.0.0"), QDir(app.applicationDirPath()).filePath(QStringLiteral("jmd-update.json")));
    host.registerCheck(QStringLiteral("motor"), [&]() {
        ++checks;
        if (!lease && checks == 1) return JmdUpdater::CheckResult::Blocked(QStringLiteral("first-read"));
        if (!lease && checks == 2) return JmdUpdater::CheckResult::Unknown(QStringLiteral("second-read"));
        return JmdUpdater::CheckResult::Ready();
    });
    host.registerCheck(QStringLiteral("workspace"), []() { return JmdUpdater::CheckResult::Ready(); });
    host.setPreparationHandlers([&]() { ++begins; return lease || begins > 1; }, [&]() { ++cancels; });
    host.setShutdownHandler([&]() {
        ++shutdowns;
        if (shutdowns == 1) return false;
        if (!host.commitShutdown()) return false;
        QTimer::singleShot(1000, &app, [&]() { app.quit(); });
        return true;
    });
    host.start();
    if (disabled) {
        QTimer::singleShot(300, &app, [&]() {
            host.stop();
            const bool clean = checks == 0 && begins == 0 && cancels == 0 && shutdowns == 0 &&
                !QFile::exists(QDir(app.applicationDirPath()).filePath(QStringLiteral("client-started.txt")));
            writeText(QStringLiteral("fixture-result.txt"), clean ? "PASS disabled: no helper or callbacks" : "FAIL disabled");
            app.exit(clean ? 0 : 2);
        });
    } else if (lease) {
        QTimer::singleShot(34000, &app, [&]() {
            const bool clean = begins == 1 && cancels == 1 && !host.isPrepared();
            writeText(QStringLiteral("fixture-result.txt"), clean ? "PASS lease: host released independently" : "FAIL lease");
            app.exit(clean ? 0 : 3);
        });
    }
    QTimer::singleShot(45000, &app, [&]() { writeText(QStringLiteral("fixture-result.txt"), "FAIL timeout"); app.exit(99); });
    const int code = app.exec();
    if (!disabled && !lease && code == 0) {
        const bool clean = checks == 9 && begins == 5 && cancels == 4 && shutdowns == 3;
        writeText(QStringLiteral("fixture-result.txt"), clean ? "PASS wire: callback/prepare/cancel/shutdown counts" : "FAIL wire counters");
        return clean ? 0 : 4;
    }
    return code;
}

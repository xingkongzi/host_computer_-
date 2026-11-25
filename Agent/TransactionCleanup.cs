// Bounded retention for this installation's own completed/cancelled updater transactions.
// Incomplete recovery journals and the two newest completed backups are always retained.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace JmdUpdater
{
    internal static class TransactionCleanup
    {
        // Pending still owns the recovery helper until ClearPending. Unreadable metadata retains all.
        internal static bool HasPendingReference(string installDirectory, string directory, string id)
        {
            try
            {
                string path = UpdatePaths.UnderRoot(installDirectory, ".jmd-update/pending.json");
                if (!File.Exists(path)) return Directory.Exists(path);
                UpdatePaths.EnsureNoReparse(path);
                if (new FileInfo(path).Length > 4096) return true;
                Dictionary<string, object> pending = JsonIO.Read<Dictionary<string, object>>(path);
                object token, target;
                return pending == null || !pending.TryGetValue("PrepareId", out token) ||
                    !pending.TryGetValue("TransactionDirectory", out target) || Convert.ToString(token) == id ||
                    String.Equals(Path.GetFullPath(Convert.ToString(target)), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return true; }
        }

        public static void Run(UpdateConfig config, string installDirectory)
        {
            string root = Path.Combine(UpdatePaths.UserRoot, "Transactions");
            if (!Directory.Exists(root)) return;
            string normalized = Path.GetFullPath(installDirectory).TrimEnd('\\', '/');
            List<string> completed = new List<string>();
            foreach (string directory in Directory.GetDirectories(root).OrderByDescending(Directory.GetLastWriteTimeUtc).Take(2000))
            {
                try
                {
                    string id = Path.GetFileName(directory); UpdatePaths.ValidateToken(id);
                    if (!String.Equals(Path.GetFullPath(directory), UpdatePaths.TransactionDirectory(id), StringComparison.OrdinalIgnoreCase)) continue;
                    string jobPath = Path.Combine(directory, "job.json");
                    if (!File.Exists(jobPath) || new FileInfo(jobPath).Length > 65536) continue;
                    UpdatePaths.EnsureNoReparse(jobPath);
                    InstallJob job = JsonIO.Read<InstallJob>(jobPath);
                    if (job == null || job.PrepareId != id || job.AppId != config.AppId ||
                        !String.Equals(Path.GetFullPath(job.InstallDirectory).TrimEnd('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase)) continue;
                    string journalPath = Path.Combine(directory, "journal.json");
                    if (!File.Exists(journalPath)) continue;
                    UpdatePaths.EnsureNoReparse(journalPath);
                    if (new FileInfo(journalPath).Length > 8 * 1024 * 1024) continue;
                    Dictionary<string, object> journal = JsonIO.Read<Dictionary<string, object>>(journalPath);
                    object state, prepare;
                    if (journal == null || !journal.TryGetValue("PrepareId", out prepare) || Convert.ToString(prepare) != id ||
                        !journal.TryGetValue("State", out state) || (Convert.ToString(state) != "Complete" && Convert.ToString(state) != "RolledBack")) continue;
                    if (HasPendingReference(installDirectory, directory, id)) continue;
                    completed.Add(directory);
                }
                catch (Exception) { /* Unverifiable data is retained, never deleted speculatively. */ }
            }
            foreach (string directory in completed.Skip(2))
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) < TimeSpan.FromDays(14)) continue;
                    if (HasPendingReference(installDirectory, directory, Path.GetFileName(directory))) continue;
                    // Every resolved target is a validated token directory inside this module's user state root.
                    UpdatePaths.EnsureNoReparse(directory);
                    foreach (string child in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)) UpdatePaths.EnsureNoReparse(child);
                    Directory.Delete(directory, true);
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}

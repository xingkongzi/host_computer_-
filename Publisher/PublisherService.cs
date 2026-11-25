// Project: JmdUpdater. Local release creation and explicit LAN serving commands.
// Private keys stay outside both source packages and the public release root.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace JmdUpdater
{
    public sealed class ServerConfig
    {
        public string RootDirectory { get; set; }
        public string BindAddress { get; set; }
        public int Port { get; set; }
        public string[] AllowedCidrs { get; set; }
        public string[] DeniedCidrs { get; set; }
        public Dictionary<string, string> ClientChannels { get; set; }
        public ServerConfig()
        {
            RootDirectory = "releases"; BindAddress = "127.0.0.1"; Port = 8787;
            AllowedCidrs = new string[] { "127.0.0.0/8" };
        }
    }

    public static class DeliveryCommands
    {
        // args contains only --name/value pairs after mode. No command is shell-expanded.
        public static int Run(string mode, string[] args)
        {
            // Accept the standalone check switch while preserving strict name/value parsing elsewhere.
            if (mode == "serve" && args.Length > 0 && args[args.Length - 1] == "--check")
            {
                List<string> expanded = new List<string>(args); expanded.Add("true"); args = expanded.ToArray();
            }
            Dictionary<string, string> options = CliArgs.Parse(args, 0);
            if (mode == "keygen") return PublisherService.GenerateKey(options);
            if (mode == "publish") return PublisherService.Publish(options);
            if (mode == "install") return InstallerService.Run(CliArgs.Required(options, "job"));
            if (mode == "recover") return InstallerService.Recover(CliArgs.Required(options, "job"),
                options.ContainsKey("restart") && String.Equals(options["restart"], "true", StringComparison.OrdinalIgnoreCase));
            if (mode == "serve")
            {
                // Detached service owns log handles instead of inheriting caller pipes.
                if (options.ContainsKey("stdout")) Console.SetOut(new StreamWriter(options["stdout"], true, new UTF8Encoding(false)) { AutoFlush = true });
                if (options.ContainsKey("stderr")) Console.SetError(new StreamWriter(options["stderr"], true, new UTF8Encoding(false)) { AutoFlush = true });
                string configPath = Path.GetFullPath(CliArgs.Required(options, "config"));
                ServerConfig config = JsonIO.Read<ServerConfig>(configPath);
                if (config == null) throw new InvalidDataException("Missing LAN server configuration.");
                string root = Path.IsPathRooted(config.RootDirectory) ? config.RootDirectory :
                    Path.Combine(Path.GetDirectoryName(configPath), config.RootDirectory);
                if (options.ContainsKey("check"))
                {
                    if (!String.Equals(options["check"], "true", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("--check requires true.");
                    LocalUpdateServer.Validate(root, config.BindAddress, config.Port, config.AllowedCidrs, config.ClientChannels, config.DeniedCidrs);
                    Console.WriteLine("Server configuration valid."); return 0;
                }
                using (CancellationTokenSource stopped = new CancellationTokenSource())
                {
                    ConsoleCancelEventHandler cancel = delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; stopped.Cancel(); };
                    Console.CancelKeyPress += cancel;
                    try { LocalUpdateServer.Run(root, config.BindAddress, config.Port, config.AllowedCidrs, stopped.Token, Console.WriteLine, config.ClientChannels, config.DeniedCidrs); }
                    finally { Console.CancelKeyPress -= cancel; }
                }
                return 0;
            }
            throw new ArgumentException("Unknown delivery mode: " + mode);
        }
    }

    public static class PublisherService
    {
        // Generates a new RSA key pair without overwriting existing trust material.
        // The private file receives an explicit current-user/SYSTEM/administrators ACL at creation.
        public static int GenerateKey(IDictionary<string, string> options)
        {
            string privatePath = Path.GetFullPath(CliArgs.Required(options, "private"));
            string publicPath = Path.GetFullPath(CliArgs.Required(options, "public"));
            if (privatePath.Equals(publicPath, StringComparison.OrdinalIgnoreCase) || File.Exists(privatePath) || File.Exists(publicPath))
                throw new IOException("Key files already exist or paths are identical. Existing trust is never replaced automatically.");
            UpdatePaths.EnsureNoReparse(privatePath); UpdatePaths.EnsureNoReparse(publicPath);
            Directory.CreateDirectory(Path.GetDirectoryName(privatePath));
            Directory.CreateDirectory(Path.GetDirectoryName(publicPath));
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(3072))
            {
                rsa.PersistKeyInCsp = false;
                FileSecurity security = new FileSecurity();
                security.SetAccessRuleProtection(true, false);
                SecurityIdentifier owner = WindowsIdentity.GetCurrent().User;
                security.SetOwner(owner);
                security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
                using (FileStream file = new FileStream(privatePath, FileMode.CreateNew, FileSystemRights.FullControl,
                    FileShare.None, 4096, FileOptions.None, security))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(rsa.ToXmlString(true));
                    file.Write(bytes, 0, bytes.Length); file.Flush(true);
                }
                using (FileStream file = new FileStream(publicPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(rsa.ToXmlString(false));
                    file.Write(bytes, 0, bytes.Length); file.Flush(true);
                }
            }
            Console.WriteLine("Public key: " + publicPath);
            Console.WriteLine("Public key SHA-256: " + ManifestIO.Sha256File(publicPath));
            return 0;
        }

        // Publishes an immutable complete program package, then atomically advertises it.
        // Runtime configuration and product-owned data are excluded by the same preserve policy used at installation.
        public static int Publish(IDictionary<string, string> options)
        {
            string source = Path.GetFullPath(CliArgs.Required(options, "source")).TrimEnd(Path.DirectorySeparatorChar);
            string root = Path.GetFullPath(CliArgs.Required(options, "root")).TrimEnd(Path.DirectorySeparatorChar);
            string keyPath = Path.GetFullPath(CliArgs.Required(options, "key"));
            string configPath = Path.GetFullPath(CliArgs.Required(options, "config"));
            UpdateConfig config = ManifestIO.LoadConfig(configPath);
            PreservePolicy.ValidateRequired(config);
            bool allowEmpty = options.ContainsKey("allow-empty-preserve") && String.Equals(options["allow-empty-preserve"], "true", StringComparison.OrdinalIgnoreCase);
            if ((config.PreservePaths == null || config.PreservePaths.Length == 0) && !allowEmpty)
                throw new InvalidDataException("Data preservation must be declared; a data-free generic product requires explicit --allow-empty-preserve true.");
            string versionText = CliArgs.Required(options, "version");
            Version version;
            if (!Version.TryParse(versionText, out version)) throw new ArgumentException("Version must have two to four numeric components.");
            versionText = version.ToString();
            string entry = UpdatePaths.Relative(CliArgs.Required(options, "entry"));
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
            if (IsWithin(source, root) || IsWithin(root, source) || IsWithin(source, keyPath) || IsWithin(root, keyPath))
                throw new InvalidDataException("Source, public release root and private key must be separate locations.");
            UpdatePaths.EnsureNoReparse(source); UpdatePaths.EnsureNoReparse(root); UpdatePaths.EnsureNoReparse(keyPath);
            string productRoot = UpdatePaths.UnderRoot(root, config.AppId);
            Directory.CreateDirectory(productRoot);
            // A filesystem lease also serializes publishers from different Windows sessions.
            // It is released by the OS after a crash and is never exposed by the HTTP server.
            using (FileStream publishLock = new FileStream(Path.Combine(productRoot, ".publish.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                return PublishLocked(source, keyPath, config, versionText, entry, options, productRoot);
        }

        private static int PublishLocked(string source, string keyPath, UpdateConfig config, string versionText,
            string entry, IDictionary<string, string> options, string productRoot)
        {
            string latest = UpdatePaths.UnderRoot(productRoot, "latest.json");
            if (File.Exists(latest))
            {
                ReleaseManifest previous = ManifestIO.Verify(File.ReadAllText(latest, Encoding.UTF8), config.PublicKeyXml);
                if (previous.AppId != config.AppId || ManifestIO.NormalizedVersion(versionText) <= ManifestIO.NormalizedVersion(previous.Version))
                    throw new InvalidDataException("A new publication must advance this product's latest version.");
            }
            string packageName = config.AppId + "-" + versionText + ".zip";
            string finalPackage = UpdatePaths.UnderRoot(productRoot, packageName);
            string historicalEnvelope = UpdatePaths.UnderRoot(productRoot, config.AppId + "-" + versionText + ".release.json");
            if (File.Exists(finalPackage) || File.Exists(historicalEnvelope))
                throw new IOException("This version has already been published; choose a new version.");
            string temporaryPackage = finalPackage + "." + Guid.NewGuid().ToString("N") + ".tmp";
            ReleaseManifest manifest = new ReleaseManifest {
                AppId = config.AppId, Version = versionText, EntryPoint = entry, PackageName = packageName,
                ReleaseNotes = options.ContainsKey("notes-file") ? File.ReadAllText(options["notes-file"], Encoding.UTF8) :
                    (options.ContainsKey("notes") ? options["notes"] : "")
            };
            // 主程序改名：--previous-entry "Old.exe;Older.exe" 声明本版本替换的旧入口（会签名，安装端据此允许改名）。
            if (options.ContainsKey("previous-entry") && !String.IsNullOrWhiteSpace(options["previous-entry"]))
                manifest.PreviousEntryPoints = options["previous-entry"].Split(';').Select(p => p.Trim()).Where(p => p.Length > 0)
                    .Select(p => UpdatePaths.Relative(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            try
            {
                List<string> files = EnumerateFiles(source, config);
                if (!files.Any(p => p.Equals(entry, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Entry point is absent or preserved.");
                if (UpdatePaths.IsPreserved(config, "jmd-version.json")) throw new InvalidDataException("jmd-version.json cannot be preserved.");
                files.RemoveAll(p => p.Equals("jmd-version.json", StringComparison.OrdinalIgnoreCase));
                using (FileStream output = new FileStream(temporaryPackage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (ZipArchive archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                    {
                        foreach (string relative in files)
                        {
                            string full = UpdatePaths.UnderRoot(source, relative);
                            ZipArchiveEntry zipEntry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                            using (FileStream input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
                            using (Stream target = zipEntry.Open())
                            using (SHA256 hash = SHA256.Create())
                            {
                                long length = input.Length;
                                byte[] buffer = new byte[65536]; int read;
                                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                                { target.Write(buffer, 0, read); hash.TransformBlock(buffer, 0, read, null, 0); }
                                hash.TransformFinalBlock(new byte[0], 0, 0);
                                manifest.Files.Add(new ReleaseFile { Path = relative, Size = length,
                                    Sha256 = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant() });
                            }
                        }
                        byte[] versionBytes = new UTF8Encoding(false).GetBytes(JsonIO.Serialize(new { AppId = config.AppId, Version = versionText }));
                        ZipArchiveEntry versionEntry = archive.CreateEntry("jmd-version.json", CompressionLevel.Optimal);
                        using (Stream target = versionEntry.Open()) target.Write(versionBytes, 0, versionBytes.Length);
                        using (SHA256 hash = SHA256.Create()) manifest.Files.Add(new ReleaseFile {
                            Path = "jmd-version.json", Size = versionBytes.Length,
                            Sha256 = BitConverter.ToString(hash.ComputeHash(versionBytes)).Replace("-", "").ToLowerInvariant()
                        });
                    }
                    output.Flush(true);
                }
                manifest.PackageSize = new FileInfo(temporaryPackage).Length;
                if (manifest.PackageSize > config.MaxPackageBytes) throw new InvalidDataException("Package exceeds client configured size limit.");
                manifest.PackageSha256 = ManifestIO.Sha256File(temporaryPackage);
                SignedRelease envelope = ManifestIO.Sign(manifest, File.ReadAllText(keyPath, Encoding.UTF8));
                // Detect accidental publication with a key that the deployed clients do not trust.
                ManifestIO.Verify(JsonIO.Serialize(envelope), config.PublicKeyXml);
                File.Move(temporaryPackage, finalPackage);
                JsonIO.WriteAtomic(historicalEnvelope, envelope);
                JsonIO.WriteAtomic(UpdatePaths.UnderRoot(productRoot, "latest.json"), envelope);
                Console.WriteLine("Published " + config.AppId + " " + versionText + ": " + finalPackage);
                return 0;
            }
            finally { if (File.Exists(temporaryPackage)) File.Delete(temporaryPackage); }
        }

        private static bool IsWithin(string root, string path)
        {
            return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> EnumerateFiles(string root, UpdateConfig config)
        {
            List<string> result = new List<string>();
            Queue<string> pending = new Queue<string>(); pending.Enqueue(root);
            HashSet<string> intermediate = new HashSet<string>(new string[] {
                ".obj", ".pdb", ".ilk", ".exp", ".idb", ".res", ".tlog", ".log", ".tmp", ".bak",
                ".pfx", ".p12", ".key", ".pem", ".snk"
            }, StringComparer.OrdinalIgnoreCase);
            while (pending.Count > 0)
            {
                string directory = pending.Dequeue(); UpdatePaths.EnsureNoReparse(directory);
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    string relative = UpdatePaths.Relative(child.Substring(root.Length + 1));
                    if (relative.Equals("JmdUpdater", StringComparison.OrdinalIgnoreCase) || !UpdatePaths.IsPreserved(config, relative)) { UpdatePaths.EnsureNoReparse(child); pending.Enqueue(child); }
                }
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    string relative = UpdatePaths.Relative(file.Substring(root.Length + 1));
                    if (UpdatePaths.IsPreserved(config, relative) || intermediate.Contains(Path.GetExtension(file)) ||
                        relative.EndsWith(".private.xml", StringComparison.OrdinalIgnoreCase) ||
                        relative.Split('/').Any(p => p.Equals("keys", StringComparison.OrdinalIgnoreCase))) continue;
                    UpdatePaths.EnsureNoReparse(file); result.Add(relative);
                    if (result.Count > 9999) throw new InvalidDataException("Too many release files.");
                }
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }
    }

    // Required data paths are declarations supplied by each host's integration.
    // The reusable updater has no product IDs, device types, tables or business file names.
    public static class PreservePolicy
    {
        public static void ValidateRequired(UpdateConfig config)
        {
            foreach (string required in config.RequiredPreservePaths ?? new string[0])
            {
                if (String.IsNullOrWhiteSpace(required)) throw new InvalidDataException("A required data preservation rule is empty.");
                string probe;
                if (required.StartsWith("*.", StringComparison.Ordinal) && required.IndexOf('/') < 0 && required.IndexOf('\\') < 0)
                    probe = "__jmd_preserve_probe_" + UpdatePaths.NewToken() + required.Substring(1);
                else probe = required.Replace('\\', '/').TrimEnd('/');
                UpdatePaths.Relative(probe);
                if (!UpdatePaths.IsPreserved(config, probe))
                    throw new InvalidDataException("Required data declaration is not covered by PreservePaths: " + required);
            }
        }
    }
}

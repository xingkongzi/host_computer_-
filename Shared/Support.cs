// JMD updater shared parsing, signatures and path boundaries.
// Only publisher-signed inventory files may be installed; runtime data stays outside it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace JmdUpdater
{
    public static class JsonIO
    {
        /// <summary>Serializes a protocol object to bounded JSON; throws for unsupported values.</summary>
        /// <param name="value">Protocol object.</param><returns>JSON text.</returns>
        public static string Serialize(object value)
        {
            return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024, RecursionLimit = 64 }.Serialize(value);
        }
        /// <summary>Parses bounded protocol JSON; malformed or oversized input is rejected.</summary>
        /// <param name="value">JSON text.</param><returns>Parsed object; callers validate its required fields.</returns>
        public static T Deserialize<T>(string value)
        {
            if (value == null || value.Length > 8 * 1024 * 1024) throw new InvalidDataException("JSON size exceeded.");
            return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024, RecursionLimit = 64 }.Deserialize<T>(value);
        }
        /// <summary>Reads UTF-8 JSON. Callers bound file size and validate path before reading untrusted files.</summary>
        /// <param name="path">Full file path.</param><returns>Parsed object.</returns>
        public static T Read<T>(string path) { return Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)); }

        // Same-directory replacement keeps readers from seeing half-written manifests/journals.
        /// <summary>Durably writes an object via same-directory atomic replacement; errors propagate.</summary>
        /// <param name="path">Validated destination.</param><param name="value">Protocol object.</param>
        public static void WriteAtomic(string path, object value) { WriteTextAtomic(path, Serialize(value)); }
        /// <summary>Durably replaces UTF-8 text, creating its parent directory; does not validate business paths.</summary>
        /// <param name="path">Previously validated destination.</param><param name="value">Complete file text.</param>
        public static void WriteTextAtomic(string path, string value)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(value);
                    stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
                if (!File.Exists(path)) { File.Move(temp, path); return; }
                // A reader without delete sharing (old server, antivirus, indexer) makes Replace fail before
                // anything changes; retry briefly while both files are still intact, otherwise report the error.
                for (int attempt = 1; ; attempt++)
                {
                    try { File.Replace(temp, path, null); return; }
                    catch (IOException)
                    {
                        if (attempt >= 20 || !File.Exists(temp) || !File.Exists(path)) throw;
                        System.Threading.Thread.Sleep(250);
                    }
                }
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public static class UpdatePaths
    {
        public static string UserRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JmdUpdater"); } }
        /// <summary>Resolves a validated session token below the current user's updater state.</summary>
        /// <param name="token">64-hex session token.</param><returns>Absolute session directory.</returns>
        public static string SessionDirectory(string token) { ValidateToken(token); return Path.Combine(UserRoot, "Sessions", token); }
        /// <summary>Resolves a validated transaction token below the current user's updater state.</summary>
        /// <param name="token">64-hex preparation token.</param><returns>Absolute transaction directory.</returns>
        public static string TransactionDirectory(string token) { ValidateToken(token); return Path.Combine(UserRoot, "Transactions", token); }
        /// <summary>Rejects non-64-hex tokens before they can enter file paths.</summary>
        /// <param name="value">Candidate token.</param>
        public static void ValidateToken(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length != 64 || !value.All(IsHex)) throw new InvalidDataException("Invalid session token.");
        }
        /// <summary>Creates an unpredictable 256-bit token using the operating system random generator.</summary>
        /// <returns>Lowercase 64-hex token.</returns>
        public static string NewToken()
        {
            byte[] bytes = new byte[32]; using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }
        /// <summary>Tests one ASCII hexadecimal digit.</summary><param name="c">Character.</param><returns>Whether hexadecimal.</returns>
        public static bool IsHex(char c) { return c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F'; }
        /// <summary>Rejects product IDs that cannot safely form URL or filesystem components.</summary>
        /// <param name="value">Product identifier.</param>
        public static void ValidateAppId(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 80 || !value.All(c => Char.IsLetterOrDigit(c) && c < 128 || c == '-' || c == '_'))
                throw new InvalidDataException("Invalid product identifier.");
        }
        /// <summary>Normalizes an inventory path and rejects traversal, streams, aliases and device names.</summary>
        /// <param name="value">Relative inventory path.</param><returns>Slash-separated validated path.</returns>
        public static string Relative(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > 220 || Path.IsPathRooted(value)) throw new InvalidDataException("Invalid relative path.");
            string normalized = value.Replace('\\', '/');
            foreach (string segment in normalized.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == ".." || segment.EndsWith(".") || segment.EndsWith(" ") ||
                    segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.IndexOf(':') >= 0) throw new InvalidDataException("Unsafe path: " + value);
                string stem = segment.Split('.')[0].ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                    stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] >= '0' && stem[3] <= '9')
                    throw new InvalidDataException("Reserved Windows name.");
            }
            return normalized;
        }
        /// <summary>Resolves an inventory path strictly inside root and rejects existing reparse ancestors.</summary>
        /// <param name="root">Allowed directory.</param><param name="relative">Relative inventory path.</param><returns>Validated absolute path.</returns>
        public static string UnderRoot(string root, string relative)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string result = Path.GetFullPath(Path.Combine(fullRoot, Relative(relative).Replace('/', Path.DirectorySeparatorChar)));
            if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escapes root.");
            EnsureNoReparse(result); return result;
        }
        /// <summary>Rejects a path if it or an existing ancestor redirects through a reparse point.</summary>
        /// <param name="path">Candidate absolute or relative path.</param>
        public static void EnsureNoReparse(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Reparse points are not allowed in update paths.");
                current = Path.GetDirectoryName(current);
            }
        }
        /// <summary>Applies the host's preserve policy plus mandatory updater/config/trust exclusions.</summary>
        /// <param name="config">Host policy.</param><param name="relative">Inventory path.</param><returns>Whether replacement is forbidden.</returns>
        public static bool IsPreserved(UpdateConfig config, string relative)
        {
            string name = Relative(relative);
            string first = name.Split('/')[0];
            // Only these two program files may cross the trust-directory boundary.
            if (first.Equals("JmdUpdater", StringComparison.OrdinalIgnoreCase))
                return !name.Equals("JmdUpdater/JmdUpdater.exe", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("JmdUpdater/JmdUpdater.exe.config", StringComparison.OrdinalIgnoreCase);
            if (first.Equals(".jmd-update", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("jmd-update.json", StringComparison.OrdinalIgnoreCase) || first.StartsWith(".git", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string raw in config.PreservePaths ?? new string[0])
            {
                string rule = raw.Replace('\\', '/').TrimEnd('/');
                if (rule.StartsWith("*.", StringComparison.Ordinal) && rule.IndexOf('/') < 0)
                { if (name.EndsWith(rule.Substring(1), StringComparison.OrdinalIgnoreCase)) return true; }
                else if (name.Equals(rule, StringComparison.OrdinalIgnoreCase) || name.StartsWith(rule + "/", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    public static class ManifestIO
    {
        /// <summary>Loads host configuration, bounds operational limits and resolves its out-of-band public key.</summary>
        /// <param name="path">Host-root jmd-update.json.</param><returns>Validated runtime configuration.</returns>
        public static UpdateConfig LoadConfig(string path)
        {
            UpdateConfig config = JsonIO.Read<UpdateConfig>(path);
            if (config == null) throw new InvalidDataException("Missing update configuration.");
            UpdatePaths.ValidateAppId(config.AppId);
            config.PollSeconds = Math.Max(30, Math.Min(86400, config.PollSeconds));
            config.RequestTimeoutSeconds = Math.Max(3, Math.Min(120, config.RequestTimeoutSeconds));
            config.ExitTimeoutSeconds = Math.Max(5, Math.Min(120, config.ExitTimeoutSeconds));
            config.StartupTimeoutSeconds = Math.Max(10, Math.Min(300, config.StartupTimeoutSeconds));
            config.StageTimeoutSeconds = Math.Max(30, Math.Min(900, config.StageTimeoutSeconds));
            if (config.MaxPackageBytes <= 0 || config.MaxPackageBytes > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("Invalid package size limit.");
            if (String.IsNullOrWhiteSpace(config.PublicKeyXml) && !String.IsNullOrWhiteSpace(config.PublicKeyFile))
                config.PublicKeyXml = File.ReadAllText(UpdatePaths.UnderRoot(Path.GetDirectoryName(Path.GetFullPath(path)), config.PublicKeyFile), Encoding.UTF8);
            return config;
        }
        /// <summary>Hashes a file without modifying it.</summary><param name="path">File path.</param><returns>Lowercase SHA-256.</returns>
        public static string Sha256File(string path)
        {
            using (FileStream stream = File.OpenRead(path)) using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        /// <summary>Signs exact serialized manifest bytes using RSA-SHA256; publisher-only API.</summary>
        /// <param name="manifest">Validated release inventory.</param><param name="privateKeyXml">Locally protected private key.</param><returns>Base64 signed envelope.</returns>
        public static SignedRelease Sign(ReleaseManifest manifest, string privateKeyXml)
        {
            Validate(manifest);
            byte[] bytes = new UTF8Encoding(false).GetBytes(JsonIO.Serialize(manifest));
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false; rsa.FromXmlString(privateKeyXml);
                return new SignedRelease { Payload = Convert.ToBase64String(bytes), Signature = Convert.ToBase64String(rsa.SignData(bytes, CryptoConfig.MapNameToOID("SHA256"))) };
            }
        }
        /// <summary>Verifies exact payload bytes before interpreting the release inventory.</summary>
        /// <param name="envelopeJson">Downloaded envelope.</param><param name="publicKeyXml">Out-of-band trusted public key.</param><returns>Verified manifest; failure throws.</returns>
        public static ReleaseManifest Verify(string envelopeJson, string publicKeyXml)
        {
            SignedRelease envelope = JsonIO.Deserialize<SignedRelease>(envelopeJson);
            if (envelope == null || String.IsNullOrWhiteSpace(publicKeyXml)) throw new InvalidDataException("Trusted publisher key is not configured.");
            byte[] bytes = Convert.FromBase64String(envelope.Payload);
            if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Manifest size exceeded.");
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false; rsa.FromXmlString(publicKeyXml);
                if (!rsa.VerifyData(bytes, CryptoConfig.MapNameToOID("SHA256"), Convert.FromBase64String(envelope.Signature))) throw new CryptographicException("Publisher signature is invalid.");
            }
            ReleaseManifest manifest = JsonIO.Deserialize<ReleaseManifest>(new UTF8Encoding(false, true).GetString(bytes));
            Validate(manifest); return manifest;
        }
        /// <summary>Rejects invalid protocol identity, lengths, hashes and inventory paths.</summary>
        /// <param name="manifest">Release to validate.</param>
        public static void Validate(ReleaseManifest manifest)
        {
            if (manifest == null || manifest.Protocol != 1) throw new InvalidDataException("Unsupported release protocol.");
            UpdatePaths.ValidateAppId(manifest.AppId);
            Version version; if (!Version.TryParse(manifest.Version, out version)) throw new InvalidDataException("Invalid product version.");
            UpdatePaths.Relative(manifest.EntryPoint);
            string package = UpdatePaths.Relative(manifest.PackageName);
            if (package.IndexOf('/') >= 0 || !package.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid package name.");
            if (manifest.PackageSize <= 0 || !IsHash(manifest.PackageSha256) || manifest.Files == null || manifest.Files.Count == 0 || manifest.Files.Count > 10000)
                throw new InvalidDataException("Invalid package inventory.");
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            foreach (ReleaseFile file in manifest.Files)
            {
                string name = UpdatePaths.Relative(file.Path);
                if (!seen.Add(name) || file.Size < 0 || !IsHash(file.Sha256)) throw new InvalidDataException("Invalid or duplicate inventory file.");
                expanded = checked(expanded + file.Size);
                if (expanded > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("Expanded package size exceeded.");
            }
            if (!seen.Contains(manifest.EntryPoint.Replace('\\', '/'))) throw new InvalidDataException("Entry point is missing from package.");
            if (manifest.PreviousEntryPoints != null)
            {
                if (manifest.PreviousEntryPoints.Count > 8) throw new InvalidDataException("Too many previous entry points.");
                string entry = UpdatePaths.Relative(manifest.EntryPoint);
                string entryDirectory = entry.LastIndexOf('/') < 0 ? "" : entry.Substring(0, entry.LastIndexOf('/'));
                foreach (string previous in manifest.PreviousEntryPoints)
                {
                    string name = UpdatePaths.Relative(previous);
                    string directory = name.LastIndexOf('/') < 0 ? "" : name.Substring(0, name.LastIndexOf('/'));
                    // 旧入口必须是与新入口同目录的 .exe，且不在本版本文件清单里（它会作为过期文件被删除）。
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || seen.Contains(name) ||
                        !String.Equals(directory, entryDirectory, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Invalid previous entry point: " + previous);
                }
            }
        }

        /// <summary>relativeExecutable 是本版本的入口，或本版本声明替换的旧入口时返回 true。</summary>
        public static bool AcceptsEntryPoint(ReleaseManifest manifest, string relativeExecutable)
        {
            string current = relativeExecutable.Replace('\\', '/');
            if (String.Equals(manifest.EntryPoint.Replace('\\', '/'), current, StringComparison.OrdinalIgnoreCase)) return true;
            if (manifest.PreviousEntryPoints == null) return false;
            foreach (string previous in manifest.PreviousEntryPoints)
                if (previous != null && String.Equals(previous.Replace('\\', '/'), current, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        /// <summary>Checks SHA-256 text format.</summary><param name="value">Candidate hash.</param><returns>Whether 64-hex.</returns>
        public static bool IsHash(string value) { return value != null && value.Length == 64 && value.All(UpdatePaths.IsHex); }
        /// <summary>Uses the same four-component numeric version order in publishing, discovery and installation.</summary>
        /// <param name="value">Two-to-four-component product version.</param><returns>Normalized version.</returns>
        public static Version NormalizedVersion(string value)
        {
            Version parsed;
            if (!Version.TryParse(value, out parsed)) throw new InvalidDataException("Invalid product version.");
            return new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        }
    }

    public static class CliArgs
    {
        /// <summary>Parses literal --name value arguments, rejecting duplicates and incomplete pairs.</summary>
        /// <param name="args">Process arguments.</param><param name="start">First option index.</param><returns>Case-insensitive options.</returns>
        public static Dictionary<string, string> Parse(string[] args, int start)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = start; i < args.Length; i += 2)
            {
                if (!args[i].StartsWith("--") || i + 1 >= args.Length || result.ContainsKey(args[i].Substring(2))) throw new ArgumentException("Expected --name value arguments.");
                result.Add(args[i].Substring(2), args[i + 1]);
            }
            return result;
        }
        /// <summary>Reads a required nonempty option.</summary><param name="args">Parsed options.</param><param name="name">Option name without dashes.</param><returns>Literal option value.</returns>
        public static string Required(IDictionary<string, string> args, string name)
        { string result; if (!args.TryGetValue(name, out result) || String.IsNullOrWhiteSpace(result)) throw new ArgumentException("Missing --" + name); return result; }
    }
}

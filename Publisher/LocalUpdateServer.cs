// Project: JmdUpdater. Read-only LAN delivery; never accepts upload or executes a requested path.
// The bind address and explicit IPv4 CIDRs are deployment settings, not discovery results.
// DeniedCidrs (optional) is checked before AllowedCidrs: a denied address is refused even inside an allowed range.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;

namespace JmdUpdater
{
    public static class LocalUpdateServer
    {
        // Starts a bounded, read-only HTTP endpoint. Cancellation stops accepting new clients.
        // Existing transfers finish or time out; root must contain only public release artifacts.
        public static void Run(string root, string bindAddress, int port, string[] allowedCidrs,
            CancellationToken cancellation, Action<string> log, IDictionary<string, string> clientChannels = null,
            string[] deniedCidrs = null)
        {
            Settings settings = Prepare(root, bindAddress, port, allowedCidrs, clientChannels, deniedCidrs);
            root = settings.Root;
            IPAddress address = settings.Address;
            Dictionary<string, string> channels = settings.Channels;
            HashSet<string> knownChannels = settings.KnownChannels;
            List<Ipv4Cidr> ranges = settings.Ranges;
            List<Ipv4Cidr> denied = settings.Denied;
            // Each refused address is logged once per run and reason (bounded per reason) so the log names who was blocked
            // without growing per poll. Writing happens off the accept thread so a stalled log cannot stop new clients.
            HashSet<string> reportedDenied = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> reportedOutside = new HashSet<string>(StringComparer.Ordinal);
            SemaphoreSlim clients = new SemaphoreSlim(16, 16);
            TcpListener listener = new TcpListener(address, port);
            listener.Start(32);
            if (log != null) log("Listening on http://" + address + ":" + port + "/");
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    if (!listener.Pending())
                    {
                        cancellation.WaitHandle.WaitOne(100);
                        continue;
                    }
                    TcpClient client = listener.AcceptTcpClient();
                    client.ReceiveTimeout = 10000;
                    client.SendTimeout = 10000;
                    IPEndPoint remote = client.Client.RemoteEndPoint as IPEndPoint;
                    bool allowed = false, blocked = false;
                    foreach (Ipv4Cidr range in denied)
                        if (remote != null && range.Contains(remote.Address)) { blocked = true; break; }
                    if (!blocked)
                        foreach (Ipv4Cidr range in ranges)
                            if (remote != null && range.Contains(remote.Address)) { allowed = true; break; }
                    HashSet<string> reported = blocked ? reportedDenied : reportedOutside;
                    if (!allowed && log != null && remote != null && reported.Count < 1024 && reported.Add(remote.Address.ToString()))
                    {
                        string line = "Refused " + remote.Address + (blocked ? " (DeniedCidrs)" : " (not in AllowedCidrs)");
                        ThreadPool.QueueUserWorkItem(delegate
                        {
                            try { lock (log) log(line); }
                            catch (IOException) { }
                            catch (ObjectDisposedException) { }
                        });
                    }
                    if (!allowed || !clients.Wait(0))
                    {
                        try { WriteError(client.GetStream(), allowed ? 503 : 403, allowed ? "Busy" : "Forbidden", false); }
                        catch (IOException) { }
                        catch (SocketException) { }
                        finally { client.Close(); }
                        continue;
                    }
                    TcpClient acceptedClient = client;
                    string releaseRoot = root;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try { Handle(acceptedClient, releaseRoot, channels, knownChannels); }
                        catch (IOException) { }
                        catch (SocketException) { }
                        catch (UnauthorizedAccessException) { }
                        catch (ArgumentException) { }
                        finally { acceptedClient.Close(); clients.Release(); }
                    });
                }
            }
            finally { listener.Stop(); }
        }

        // Validates all startup settings without opening a listener or changing the release root.
        public static void Validate(string root, string bindAddress, int port, string[] allowedCidrs,
            IDictionary<string, string> clientChannels, string[] deniedCidrs = null)
        {
            Prepare(root, bindAddress, port, allowedCidrs, clientChannels, deniedCidrs);
        }

        private sealed class Settings
        {
            public string Root; public IPAddress Address;
            public Dictionary<string, string> Channels; public HashSet<string> KnownChannels;
            public List<Ipv4Cidr> Ranges; public List<Ipv4Cidr> Denied;
        }

        private static Settings Prepare(string root, string bindAddress, int port, string[] allowedCidrs,
            IDictionary<string, string> clientChannels, string[] deniedCidrs)
        {
            // Validate before listening; snapshot settings so request workers see immutable routing.
            Dictionary<string, string> channels = new Dictionary<string, string>(StringComparer.Ordinal);
            HashSet<string> knownChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "test", "official" };
            foreach (KeyValuePair<string, string> pair in clientChannels ?? new Dictionary<string, string>())
            {
                IPAddress ip;
                if (!IPAddress.TryParse(pair.Key, out ip) || ip.AddressFamily != AddressFamily.InterNetwork || ip.ToString() != pair.Key ||
                    pair.Value == null || String.Equals(pair.Value, "official", StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(pair.Value, @"\A[A-Za-z0-9_-]{1,32}\z"))
                    throw new ArgumentException("Invalid ClientChannels IPv4 address or channel name.");
                channels.Add(pair.Key, pair.Value); knownChannels.Add(pair.Value);
            }
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException("port");
            root = Path.GetFullPath(root);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            RejectReparse(root, root);
            IPAddress address;
            if (!IPAddress.TryParse(bindAddress, out address) || address.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("BindAddress must be an IPv4 address.");
            bool local = address.Equals(IPAddress.Any) || address.Equals(IPAddress.Loopback);
            foreach (System.Net.NetworkInformation.NetworkInterface adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                if (adapter.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                    foreach (System.Net.NetworkInformation.UnicastIPAddressInformation item in adapter.GetIPProperties().UnicastAddresses)
                        if (item.Address.Equals(address)) local = true;
            if (!local) throw new ArgumentException("BindAddress is not active on this computer.");
            if (allowedCidrs == null || allowedCidrs.Length == 0)
                throw new ArgumentException("At least one allowed IPv4 CIDR is required.");
            List<Ipv4Cidr> ranges = new List<Ipv4Cidr>();
            foreach (string cidr in allowedCidrs) ranges.Add(new Ipv4Cidr(cidr));
            // Deny entries accept a CIDR or a bare IPv4 address (treated as /32).
            List<Ipv4Cidr> denied = new List<Ipv4Cidr>();
            foreach (string entry in deniedCidrs ?? new string[0])
                denied.Add(new Ipv4Cidr(entry != null && entry.IndexOf('/') < 0 ? entry + "/32" : entry));
            return new Settings { Root = root, Address = address, Channels = channels, KnownChannels = knownChannels, Ranges = ranges, Denied = denied };
        }

        private static void Handle(TcpClient client, string root, IDictionary<string, string> channels, HashSet<string> knownChannels)
        {
            client.ReceiveTimeout = 10000;
            client.SendTimeout = 30000;
            using (NetworkStream stream = client.GetStream())
            {
                string header = ReadHeader(stream);
                if (header == null) { WriteError(stream, 400, "Bad Request", false); return; }
                string firstLine = header.Split(new string[] { "\r\n" }, StringSplitOptions.None)[0];
                string[] request = firstLine.Split(' ');
                bool head = request.Length == 3 && request[0] == "HEAD";
                if (request.Length != 3 || (request[0] != "GET" && !head) ||
                    (request[2] != "HTTP/1.1" && request[2] != "HTTP/1.0"))
                { WriteError(stream, 405, "Method Not Allowed", head); return; }
                string target = request[1];
                if (!target.StartsWith("/", StringComparison.Ordinal) || target.StartsWith("//", StringComparison.Ordinal) ||
                    target.IndexOf('?') >= 0 || target.IndexOf('#') >= 0 || target.IndexOf('\\') >= 0)
                { WriteError(stream, 400, "Bad Request", head); return; }
                try { target = Uri.UnescapeDataString(target); }
                catch (UriFormatException) { WriteError(stream, 400, "Bad Request", head); return; }
                string relative = target.Substring(1);
                string[] segments = relative.Split('/');
                if (segments.Length == 0) { WriteError(stream, 404, "Not Found", head); return; }
                foreach (string segment in segments)
                {
                    string baseName = segment.Split('.')[0].ToUpperInvariant();
                    if (segment.Length == 0 || segment == "." || segment == ".." || segment.EndsWith(".", StringComparison.Ordinal) ||
                        segment.EndsWith(" ", StringComparison.Ordinal) || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                        baseName == "CON" || baseName == "PRN" || baseName == "AUX" || baseName == "NUL" ||
                        (baseName.Length == 4 && (baseName.StartsWith("COM", StringComparison.Ordinal) || baseName.StartsWith("LPT", StringComparison.Ordinal)) &&
                        baseName[3] >= '1' && baseName[3] <= '9'))
                    { WriteError(stream, 400, "Bad Request", head); return; }
                }
                bool official = String.Equals(segments[0], "official", StringComparison.OrdinalIgnoreCase);
                if (official && segments.Length != 3) { WriteError(stream, 404, "Not Found", head); return; }
                if (official) relative = segments[1] + "/" + segments[2];
                string extension = Path.GetExtension(relative);
                if (!String.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
                { WriteError(stream, 404, "Not Found", head); return; }
                string fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                { WriteError(stream, 403, "Forbidden", head); return; }
                try
                {
                    // Only product/file requests are routed. Explicit channel URLs never nest channels.
                    string channel;
                    IPEndPoint remote = client.Client.RemoteEndPoint as IPEndPoint;
                    if (segments.Length == 2 && !knownChannels.Contains(segments[0]) && remote != null &&
                        channels.TryGetValue(remote.Address.ToString(), out channel))
                    {
                        string candidate = Path.GetFullPath(Path.Combine(root, channel, relative.Replace('/', Path.DirectorySeparatorChar)));
                        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                            throw new UnauthorizedAccessException("Channel path escapes release root.");
                        try
                        {
                            RejectReparse(root, candidate);
                            if (File.Exists(candidate)) fullPath = candidate;
                        }
                        catch (FileNotFoundException) { }
                        catch (DirectoryNotFoundException) { }
                    }
                    RejectReparse(root, fullPath);
                    // FileShare.Delete lets the publisher atomically replace latest.json while a client is still downloading
                    // the previous one; this reader keeps the old content, the next request gets the new file.
                    using (FileStream file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                    {
                        WriteHeader(stream, 200, "OK", file.Length,
                            String.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase) ? "application/zip" : "application/json; charset=utf-8");
                        if (!head) file.CopyTo(stream, 65536);
                    }
                }
                catch (FileNotFoundException) { WriteError(stream, 404, "Not Found", head); }
                catch (DirectoryNotFoundException) { WriteError(stream, 404, "Not Found", head); }
                catch (UnauthorizedAccessException) { WriteError(stream, 403, "Forbidden", head); }
            }
        }

        private static string ReadHeader(Stream stream)
        {
            byte[] bytes = new byte[16384];
            int used = 0;
            Stopwatch deadline = Stopwatch.StartNew();
            while (used < bytes.Length)
            {
                long remaining = 10000 - deadline.ElapsedMilliseconds;
                if (remaining <= 0) return null;
                stream.ReadTimeout = (int)remaining;
                int value = stream.ReadByte();
                if (value < 0) return null;
                if (value == 0 || value > 127) return null;
                bytes[used++] = (byte)value;
                if (used >= 4 && bytes[used - 4] == 13 && bytes[used - 3] == 10 && bytes[used - 2] == 13 && bytes[used - 1] == 10)
                    return Encoding.ASCII.GetString(bytes, 0, used);
            }
            return null;
        }

        private static void WriteError(Stream stream, int status, string reason, bool head)
        {
            byte[] body = Encoding.UTF8.GetBytes(reason + "\n");
            WriteHeader(stream, status, reason, body.Length, "text/plain; charset=utf-8");
            if (!head) stream.Write(body, 0, body.Length);
        }

        private static void WriteHeader(Stream stream, int status, string reason, long length, string type)
        {
            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " " + reason +
                "\r\nContent-Length: " + length.ToString(CultureInfo.InvariantCulture) + "\r\nContent-Type: " + type +
                "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
            stream.Write(header, 0, header.Length);
        }

        private static void RejectReparse(string root, string fullPath)
        {
            string current = Path.GetFullPath(root);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Reparse points are not published.");
            string relative = fullPath.Substring(current.Length).TrimStart(Path.DirectorySeparatorChar);
            foreach (string segment in relative.Split(Path.DirectorySeparatorChar))
            {
                if (segment.Length == 0) continue;
                current = Path.Combine(current, segment);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Reparse points are not published.");
            }
        }

        private sealed class Ipv4Cidr
        {
            private readonly uint network;
            private readonly uint mask;
            public Ipv4Cidr(string value)
            {
                string[] parts = (value ?? "").Split('/');
                IPAddress address;
                int prefix;
                // Canonical dotted form only: "192.168.1.010" would otherwise parse as .8 and match the wrong host.
                if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out address) || address.AddressFamily != AddressFamily.InterNetwork ||
                    address.ToString() != parts[0] ||
                    !Int32.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix < 0 || prefix > 32)
                    throw new ArgumentException("Invalid IPv4 CIDR: " + value);
                mask = prefix == 0 ? 0 : UInt32.MaxValue << (32 - prefix);
                network = ToUInt32(address) & mask;
            }
            public bool Contains(IPAddress address)
            { return address.AddressFamily == AddressFamily.InterNetwork && (ToUInt32(address) & mask) == network; }
            private static uint ToUInt32(IPAddress address)
            {
                byte[] bytes = address.GetAddressBytes();
                return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            }
        }
    }
}

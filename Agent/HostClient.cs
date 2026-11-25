// JMD updater authenticated, bounded local IPC client. Host callbacks stay in the host process.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace JmdUpdater
{
    internal sealed class HostClient
    {
        private readonly string pipeName;
        private readonly string token;
        private HashSet<string> registeredChecks;

        public HostClient(string pipe, string sessionToken) { pipeName = pipe; token = sessionToken; }

        public HostResponse Call(string command, string prepareId, string ackPath)
        {
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                pipe.Connect(3000);
                using (Timer deadline = new Timer(delegate { try { pipe.Dispose(); } catch (ObjectDisposedException) { } }, null, 7000, Timeout.Infinite))
                {
                    byte[] request = new UTF8Encoding(false).GetBytes(JsonIO.Serialize(new HostRequest { Token = token, Command = command, PrepareId = prepareId, AckPath = ackPath }) + "\n");
                    if (request.Length > 65536) throw new InvalidDataException("Host request exceeded limit.");
                    pipe.Write(request, 0, request.Length); pipe.Flush();
                    using (MemoryStream line = new MemoryStream())
                    {
                        while (line.Length < 65536)
                        {
                            int next = pipe.ReadByte();
                            if (next < 0) throw new EndOfStreamException("Host closed the update session.");
                            if (next == '\n')
                            {
                                HostResponse reply = JsonIO.Deserialize<HostResponse>(new UTF8Encoding(false, true).GetString(line.ToArray()));
                                if (reply == null) throw new InvalidDataException("Empty host response.");
                                return reply;
                            }
                            line.WriteByte((byte)next);
                        }
                        throw new InvalidDataException("Host response exceeded limit.");
                    }
                }
            }
        }

        public void SetRegisteredChecks(List<CheckResult> checks)
        {
            if (checks == null || checks.Count == 0 || checks.Count > 128) throw new InvalidDataException("Host has not registered its update checks.");
            registeredChecks = new HashSet<string>(StringComparer.Ordinal);
            foreach (CheckResult check in checks)
                if (check == null || String.IsNullOrWhiteSpace(check.Id) || !registeredChecks.Add(check.Id)) throw new InvalidDataException("Invalid registered check list.");
        }

        public string BlockingReasons(HostResponse reply)
        {
            if (reply == null) return "原程序未返回更新检查结果。";
            if (registeredChecks == null || reply.Checks == null || reply.Checks.Count != registeredChecks.Count)
                return !reply.Ok && !String.IsNullOrWhiteSpace(reply.Message) ? reply.Message : "更新检查结果不完整，暂时无法确认是否可以安装。";
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<string> messages = new List<string>();
            foreach (CheckResult check in reply.Checks)
            {
                if (check == null || !registeredChecks.Contains(check.Id) || !seen.Add(check.Id)) return "更新检查项缺失或重复，请重新启动软件后重试。";
                if (check.Status != "Ready")
                {
                    if (check.Status != "Blocked" && check.Status != "Unknown") return "收到无法识别的检查状态，已停止本次安装。";
                    messages.Add("• " + (String.IsNullOrWhiteSpace(check.Message) ? check.Id + "：无法确认状态" : check.Message));
                }
            }
            if (messages.Count > 0) return String.Join(Environment.NewLine, messages);
            return reply.Ok ? "" : (String.IsNullOrWhiteSpace(reply.Message) ? "原程序未能完成更新检查，请稍后重试。" : reply.Message);
        }
    }
}

// Synthetic, hardware-free product used by the real Agent/Installer flow tests.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using JmdUpdater;
#if NEW_HOST
[assembly: AssemblyVersion("2.0.0.0")]
#else
[assembly: AssemblyVersion("1.0.0.0")]
#endif

internal sealed class FixtureConfiguration
{
    public string AppId { get; set; }
    public string Pipe { get; set; }
    public string Token { get; set; }
    public string TracePath { get; set; }
    public string StopEvent { get; set; }
    public string ReadyEvent { get; set; }
    public bool DenyShutdown { get; set; }
    public int PrepareDelayMilliseconds { get; set; }
}

internal static class HostFixture
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static FixtureConfiguration config;
    private static string prepared;
    private static readonly ManualResetEvent shutdown = new ManualResetEvent(false);
    private static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version.ToString();

    private static int Main()
    {
        try
        {
            config = Json.Deserialize<FixtureConfiguration>(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "flow-host.json")));
            using (EventWaitHandle stop = EventWaitHandle.OpenExisting(config.StopEvent))
            using (EventWaitHandle ready = EventWaitHandle.OpenExisting(config.ReadyEvent))
            {
                string token = Environment.GetEnvironmentVariable("JMD_UPDATE_READY_TOKEN");
                string path = Environment.GetEnvironmentVariable("JMD_UPDATE_READY_PATH");
                if (!String.IsNullOrEmpty(token))
                {
                    string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JmdUpdater", "Transactions", token, "ready.json");
                    if (!String.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase)) return 3;
                    WriteJson(expected, new { Token = token, Pid = Process.GetCurrentProcess().Id });
                    Record("new-ready", token, false);
                    ready.Set(); stop.WaitOne(); return 0;
                }
                Thread worker = new Thread(delegate() { Serve(stop, ready); }); worker.IsBackground = true; worker.Start();
                WaitHandle.WaitAny(new WaitHandle[] { stop, shutdown });
                worker.Join(1500);
                return 0;
            }
        }
        catch (Exception error)
        {
            try { Record("fatal:" + error, null, false); } catch (Exception) { }
            return 1;
        }
    }

    private static void Serve(EventWaitHandle stop, EventWaitHandle ready)
    {
        while (!stop.WaitOne(0) && !shutdown.WaitOne(0))
        {
            using (var pipe = new NamedPipeServerStream(config.Pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            {
                var wait = pipe.BeginWaitForConnection(null, null); ready.Set();
                while (!wait.AsyncWaitHandle.WaitOne(100)) if (stop.WaitOne(0) || shutdown.WaitOne(0)) return;
                pipe.EndWaitForConnection(wait);
                bool exit = false;
                using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, true))
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                {
                    HostRequest request = Json.Deserialize<HostRequest>(reader.ReadLine());
                    bool staged = HasStaged(request.PrepareId);
                    Record(request.Command, request.PrepareId, staged);
                    HostResponse response = Identity();
                    if (request.Protocol != 1 || request.Token != config.Token) { response.Ok = false; response.Message = "Bad test session."; }
                    else if (request.Command == "hello") response.Checks[0].Status = "Unknown";
                    else if (request.Command == "prepare")
                    {
                        if (config.PrepareDelayMilliseconds > 0) Thread.Sleep(config.PrepareDelayMilliseconds);
                        if (!staged) { response.Ok = false; response.Message = "Agent prepared the host before installer staging."; }
                        else { prepared = request.PrepareId; response.Prepared = true; response.PrepareId = prepared; }
                    }
                    else if (request.Command == "cancel") { prepared = null; response.Prepared = false; }
                    else if (request.Command == "shutdown")
                    {
                        if (!staged || prepared != request.PrepareId) { response.Ok = false; response.Message = "Invalid preparation order."; }
                        else if (config.DenyShutdown) { response.Ok = false; response.Message = "Synthetic unsaved work refused shutdown."; }
                        else
                        {
                            string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JmdUpdater", "Sessions", config.Token, "shutdown.json");
                            if (!String.Equals(Path.GetFullPath(request.AckPath), expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Wrong acknowledgement path.");
                            WriteJson(expected, new { PrepareId = prepared, Pid = Process.GetCurrentProcess().Id, ExecutablePath = Assembly.GetExecutingAssembly().Location });
                            Record("shutdown-ack", prepared, true); exit = true;
                        }
                    }
                    writer.WriteLine(Json.Serialize(response));
                }
                if (exit) { shutdown.Set(); return; }
            }
        }
    }

    private static HostResponse Identity()
    {
        return new HostResponse { Ok = true, AppId = config.AppId, Version = Version, Pid = Process.GetCurrentProcess().Id,
            InstallDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'), ExecutablePath = Assembly.GetExecutingAssembly().Location,
            Prepared = prepared != null, PrepareId = prepared,
            Checks = new List<CheckResult> { new CheckResult { Id = "fixture-safe", Status = "Ready", Message = "Synthetic host, no hardware." } } };
    }

    private static bool HasStaged(string id)
    {
        if (String.IsNullOrEmpty(id)) return false;
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JmdUpdater", "Transactions", id, "staged.json");
        if (!File.Exists(path)) return false;
        var value = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
        return Convert.ToString(value["PrepareId"]) == id && Convert.ToInt32(value["HostPid"]) == Process.GetCurrentProcess().Id;
    }

    private static void Record(string command, string id, bool staged)
    {
        File.AppendAllText(config.TracePath, Json.Serialize(new { Command = command, PrepareId = id, Staged = staged,
            ShutdownRequested = HasShutdownRequest(id), Pid = Process.GetCurrentProcess().Id, Build = Version,
            Timestamp = DateTime.UtcNow.ToString("O") }) + Environment.NewLine, new UTF8Encoding(false));
    }

    private static bool HasShutdownRequest(string id)
    {
        if (String.IsNullOrEmpty(id)) return false;
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JmdUpdater", "Transactions", id, "shutdown-requested.json");
        if (!File.Exists(path)) return false;
        var value = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
        return Convert.ToString(value["PrepareId"]) == id && Convert.ToInt32(value["HostPid"]) == Process.GetCurrentProcess().Id;
    }

    private static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(Json.Serialize(value)); stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
        }
    }
}

// JMD reusable updater executable. Hosts launch the agent without a console; delivery commands also work from a terminal.
using System;
using System.Net;
using System.Windows.Forms;

namespace JmdUpdater
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                // .NET 4.5 otherwise defaults to obsolete TLS versions for an optional HTTPS endpoint.
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                if (args.Length == 0) { Console.WriteLine("JmdUpdater: agent | settings | serve | publish | keygen | install | recover"); return 2; }
                string[] rest = new string[args.Length - 1]; Array.Copy(args, 1, rest, 0, rest.Length);
                if (args[0] == "agent") return Agent.Run(rest);
                if (args[0] == "settings") return UpdateSettings.Run(rest);
                return DeliveryCommands.Run(args[0], rest);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
                // Install preflight/cancellation belongs to Agent's single prompt. Post-apply
                // failures are reported by InstallerService after releasing the install mutex.
                if (args.Length > 0 && args[0] == "recover" && !(error is OperationCanceledException) &&
                    Environment.UserInteractive && Environment.GetEnvironmentVariable("JMD_UPDATER_NO_UI") != "1")
                    MessageBox.Show("更新未完成：" + error.Message + "\r\n\r\n请正常启动原软件，或按部署说明检查安装记录。不要手动覆盖正在运行的程序。",
                        "局域网更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }
    }
}

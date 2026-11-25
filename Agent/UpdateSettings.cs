// Standalone settings remain available when automatic updating is disabled.
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace JmdUpdater
{
    internal static class UpdateSettings
    {
        public static int Run(string[] args)
        {
            Dictionary<string, string> options = CliArgs.Parse(args, 0);
            string path = Path.GetFullPath(CliArgs.Required(options, "config"));
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (!File.Exists(path))
            {
                MessageBox.Show("当前目录尚未部署更新配置。请先使用部署脚本配置发布地址和受信任公钥。", "局域网更新设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }
            UpdateConfig config = JsonIO.Read<UpdateConfig>(path);
            if (config == null) throw new InvalidDataException("更新配置无效。" );
            using (Form dialog = new Form { Text = "局域网更新设置", Width = 560, Height = 245, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
            {
                CheckBox enabled = new CheckBox { Left = 18, Top = 15, Width = 480, Text = "启用局域网更新（仅在上位机运行期间检查）", Checked = config.Enabled };
                Label label = new Label { Left = 18, Top = 53, Width = 500, Text = "服务器 IP 或完整地址，例如 http://192.168.32.99:8787" };
                TextBox input = new TextBox { Left = 18, Top = 78, Width = 500, Text = config.ServerUrl };
                Label notice = new Label { Left = 18, Top = 114, Width = 500, Height = 35, Text = "启用后重新启动上位机生效。停用后不会再发起新更新；已开始的文件替换会安全完成或恢复。" };
                Button save = new Button { Left = 335, Top = 164, Width = 85, Text = "保存" };
                Button cancel = new Button { Left = 435, Top = 164, Width = 85, Text = "取消", DialogResult = DialogResult.Cancel };
                save.Click += delegate
                {
                    try
                    {
                        string value = input.Text.Trim();
                        if (value.IndexOf("://", StringComparison.Ordinal) < 0) value = "http://" + value + (value.IndexOf(':') < 0 ? ":8787" : "");
                        config.ServerUrl = Agent.ServerUri(value.TrimEnd('/') + "/").AbsoluteUri.TrimEnd('/');
                        config.Enabled = enabled.Checked; JsonIO.WriteAtomic(path, config);
                        MessageBox.Show(dialog, "设置已保存。启用更新后请重新启动上位机。", "局域网更新设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        dialog.DialogResult = DialogResult.OK;
                    }
                    catch (Exception error) { MessageBox.Show(dialog, error.Message, "无法保存设置", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                };
                dialog.Controls.AddRange(new Control[] { enabled, label, input, notice, save, cancel }); dialog.AcceptButton = save; dialog.CancelButton = cancel;
                dialog.ShowDialog(); return 0;
            }
        }
    }
}

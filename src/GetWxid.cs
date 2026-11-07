using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// 取微信号.exe —— 复用主程序引擎，导出全部微信号
class GrabMain
{
    static string AppDir = AppDomain.CurrentDomain.BaseDirectory;
    static string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "群好友助手");

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.Run(new F());
    }

    public static List<string> RunEngine(Action<string> log, int seconds)
    {
        string exe = Path.Combine(AppDir, "群好友助手.exe");
        if (!File.Exists(exe)) { log("缺少 群好友助手.exe（需与本程序放在同一目录）"); return null; }
        string all = Path.Combine(DataDir, "wxid-all.txt");
        try { if (File.Exists(all)) File.Delete(all); } catch { }

        log("正在读取微信内存（约 " + seconds + " 秒）…");
        var psi = new ProcessStartInfo(exe, "--read " + seconds);
        psi.UseShellExecute = true;
        psi.WindowStyle = ProcessWindowStyle.Hidden;
        var p = Process.Start(psi);
        p.WaitForExit((seconds + 60) * 1000);

        if (!File.Exists(all)) { log("引擎没有产出结果"); return null; }
        var list = new List<string>();
        foreach (var line in File.ReadAllLines(all, Encoding.UTF8))
        {
            string t = line.Trim();
            if (t.Length == 0 || t == "微信号") continue;
            list.Add(t);
        }
        log("完成，共 " + list.Count + " 个微信号");
        return list;
    }

    class F : Form
    {
        Button btnRead, btnCopy, btnExport;
        ListBox list;
        Label lbStatus;
        List<string> data = new List<string>();

        public F()
        {
            Text = "取微信号";
            ClientSize = new Size(760, 900);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.White;

            var tip = new Label();
            tip.Text = "从微信内存读取全部微信号。只读操作，微信不必在前台。";
            tip.Dock = DockStyle.Top; tip.Height = 34;
            tip.Padding = new Padding(12, 10, 0, 0);
            tip.ForeColor = Color.FromArgb(110, 118, 128);
            Controls.Add(tip);

            var bar = new Panel();
            bar.Dock = DockStyle.Top; bar.Height = 48;
            Controls.Add(bar);

            btnRead = Mk("读取微信号", Color.FromArgb(7, 193, 96), Color.White);
            btnRead.Location = new Point(10, 8);
            btnRead.Click += delegate { StartRead(); };
            bar.Controls.Add(btnRead);

            btnCopy = Mk("复制全部", Color.White, Color.FromArgb(24, 28, 34));
            btnCopy.Location = new Point(140, 8);
            btnCopy.Click += delegate
            {
                if (data.Count == 0) { MessageBox.Show("还没有数据"); return; }
                try { Clipboard.SetDataObject(string.Join("\r\n", data.ToArray()), true); lbStatus.Text = "已复制 " + data.Count + " 个"; }
                catch { MessageBox.Show("复制失败"); }
            };
            bar.Controls.Add(btnCopy);

            btnExport = Mk("导出 CSV", Color.White, Color.FromArgb(24, 28, 34));
            btnExport.Location = new Point(256, 8);
            btnExport.Click += delegate
            {
                if (data.Count == 0) { MessageBox.Show("还没有数据"); return; }
                var dlg = new SaveFileDialog();
                dlg.Filter = "CSV 文件 (*.csv)|*.csv";
                dlg.FileName = "微信号_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv";
                if (dlg.ShowDialog() != DialogResult.OK) return;
                var sb = new StringBuilder();
                sb.AppendLine("微信号");
                foreach (var x in data) sb.AppendLine(x);
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                MessageBox.Show("已导出 " + data.Count + " 个到：\r\n" + dlg.FileName);
            };
            bar.Controls.Add(btnExport);

            lbStatus = new Label();
            lbStatus.Dock = DockStyle.Bottom; lbStatus.Height = 30;
            lbStatus.Text = "点「读取微信号」开始（需要管理员权限）";
            lbStatus.Padding = new Padding(12, 8, 0, 0);
            lbStatus.ForeColor = Color.FromArgb(110, 118, 128);
            Controls.Add(lbStatus);

            list = new ListBox();
            list.Dock = DockStyle.Fill;
            list.BorderStyle = BorderStyle.None;
            list.Font = new Font("Consolas", 10f);
            list.IntegralHeight = false;
            Controls.Add(list);
            list.BringToFront(); bar.BringToFront(); tip.BringToFront();
        }

        Button Mk(string t, Color back, Color fore)
        {
            var b = new Button();
            b.Text = t; b.Width = 110; b.Height = 32;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(222, 226, 232);
            b.BackColor = back; b.ForeColor = fore;
            return b;
        }

        void StartRead()
        {
            btnRead.Enabled = false;
            lbStatus.Text = "正在读取…";
            var th = new Thread(delegate()
            {
                List<string> res = null; string err = null;
                try { res = RunEngine(delegate(string s) { try { BeginInvoke((Action)delegate { lbStatus.Text = s; }); } catch { } }, 25); }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        btnRead.Enabled = true;
                        if (err != null) { lbStatus.Text = "出错：" + err; return; }
                        if (res == null) { lbStatus.Text = "读取失败"; return; }
                        data = res;
                        list.BeginUpdate(); list.Items.Clear();
                        foreach (var x in data) list.Items.Add(x);
                        list.EndUpdate();
                        lbStatus.Text = "共读取到 " + data.Count + " 个微信号";
                    });
                }
                catch { }
            });
            th.IsBackground = true;
            th.Start();
        }
    }
}
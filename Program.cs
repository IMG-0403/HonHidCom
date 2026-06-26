using System;
using System.Drawing;
using System.Windows.Forms;

namespace HonHidVerifier
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (var server = new WebAppServer())
            {
                try
                {
                    server.Start();
                    server.OpenBrowser();
                    Application.Run(new ServerStatusForm(server.Url));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        "Webアプリの起動に失敗しました。\r\n" + ex.Message,
                        "起動エラー",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }
    }

    internal sealed class ServerStatusForm : Form
    {
        private readonly string _url;

        public ServerStatusForm(string url)
        {
            _url = url;
            Text = "Honeywell バーコードリーダー デバイス情報";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            ClientSize = new Size(520, 180);
            Font = new Font("Yu Gothic UI", 9f);
            BuildUi();
        }

        private void BuildUi()
        {
            var message = new Label
            {
                Text = "ローカルアプリを起動中です。\r\nこの画面を閉じるとブラウザからの通信も終了します。",
                AutoSize = false,
                Location = new Point(18, 18),
                Size = new Size(480, 50)
            };

            var urlBox = new TextBox
            {
                Text = _url,
                ReadOnly = true,
                Location = new Point(18, 78),
                Size = new Size(360, 26)
            };

            var openButton = new Button
            {
                Text = "ブラウザを開く",
                Location = new Point(390, 76),
                Size = new Size(110, 30)
            };
            openButton.Click += delegate
            {
                try { System.Diagnostics.Process.Start(_url); } catch { }
            };

            var closeButton = new Button
            {
                Text = "終了",
                Location = new Point(390, 128),
                Size = new Size(110, 32)
            };
            closeButton.Click += delegate { Close(); };

            Controls.Add(message);
            Controls.Add(urlBox);
            Controls.Add(openButton);
            Controls.Add(closeButton);
        }
    }
}

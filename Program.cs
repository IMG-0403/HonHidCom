using System;
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
                    MessageBox.Show(
                        "Webアプリを起動しました。\r\n\r\nブラウザを閉じた後、このメッセージをOKするとアプリを終了します。\r\n\r\n" + server.Url,
                        "Honeywell バーコードリーダー デバイス情報",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
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
}

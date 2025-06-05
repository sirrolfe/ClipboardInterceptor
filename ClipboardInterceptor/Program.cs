using System;
using System.Threading;
using System.Windows.Forms;

namespace ClipboardInterceptor
{
    internal static class Program
    {
        private static readonly Mutex SingleInstance =
            new Mutex(true, "ClipboardInterceptorInstance");

        [STAThread]
        private static void Main()
        {
            // pastikan hanya satu instance
            if (!SingleInstance.WaitOne(TimeSpan.Zero, true))
            {
                MessageBox.Show(
                    "ClipboardInterceptor is already running.",
                    "Application Running",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            // --- simpan default timeout 60 ms sekali saja ---
            var db = DatabaseManager.Instance;
            if (db.GetSetting("DecryptionTimeout", null) == null)
                db.SaveSetting("DecryptionTimeout", "60");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                Application.Run(new MainForm());
            }
            finally
            {
                SingleInstance.ReleaseMutex();
            }
        }
    }
}

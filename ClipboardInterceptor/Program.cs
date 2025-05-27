using System;
using System.Windows.Forms;
using System.Threading;

namespace ClipboardInterceptor
{
    static class Program
    {
        private static Mutex mutex = new Mutex(true, "ClipboardInterceptorInstance");

        [STAThread]
        static void Main()
        {
            // Ensure only one instance runs
            if (!mutex.WaitOne(TimeSpan.Zero, true))
            {
                MessageBox.Show(
                    "ClipboardInterceptor is already running.",
                    "Application Running",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                Application.Run(new MainForm());
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
    }
}
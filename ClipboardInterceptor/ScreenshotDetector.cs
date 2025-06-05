using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClipboardInterceptor
{
    public class ScreenshotDetector
    {
        // Win32 API untuk deteksi keystrokes dan hotkeys sistem
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int VK_SNAPSHOT = 0x2C; // PrintScreen key

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private LowLevelKeyboardProc _keyboardProc;
        private IntPtr _keyboardHookId = IntPtr.Zero;

        // Event yang dipicu ketika screenshot terdeteksi
        public event EventHandler<EventArgs> ScreenshotDetected;

        // Flag untuk enable/disable
        private bool _isEnabled = false;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        public ScreenshotDetector()
        {
            _keyboardProc = KeyboardHookCallback;
        }

        public void Start()
        {
            if (!_isEnabled)
            {
                _keyboardHookId = SetKeyboardHook(_keyboardProc);
                _isEnabled = true;
            }
        }

        public void Stop()
        {
            if (_isEnabled && _keyboardHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHookId);
                _keyboardHookId = IntPtr.Zero;
                _isEnabled = false;
            }
        }

        private IntPtr SetKeyboardHook(LowLevelKeyboardProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                return SetWindowsHookEx(
                    WH_KEYBOARD_LL,
                    proc,
                    GetModuleHandle(curModule.ModuleName),
                    0);
            }
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                int vkCode = Marshal.ReadInt32(lParam);

                // Deteksi PrintScreen key
                if (vkCode == VK_SNAPSHOT)
                {
                    OnScreenshotDetected();
                }

                // Deteksi Win+Shift+S (Snipping Tool shortcut)
                bool win = (Control.ModifierKeys & Keys.LWin) == Keys.LWin ||
                           (Control.ModifierKeys & Keys.RWin) == Keys.RWin;
                bool shift = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;

                if (win && shift && vkCode == (int)Keys.S)
                {
                    OnScreenshotDetected();
                }
            }

            return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
        }

        private void OnScreenshotDetected()
        {
            // Trigger event in main thread to avoid cross-thread issues
            ScreenshotDetected?.Invoke(this, EventArgs.Empty);
        }
    }
}
using Windows.ApplicationModel.DataTransfer;
using WinRTClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace ClipboardInterceptor
{
    internal static class ClipboardHelper
    {
        public static void SetTextNoHistory(string text)
        {
            var dp = new DataPackage();
            dp.SetText(text);

            var opt = new ClipboardContentOptions
            {
                IsAllowedInHistory = false,
                IsRoamable = false
            };

            WinRTClipboard.SetContentWithOptions(dp, opt);

            // ⬇️  Paksa sinkronisasi ke clipboard Win32 (Notepad, dst.)
            WinRTClipboard.Flush();
        }
    }
}
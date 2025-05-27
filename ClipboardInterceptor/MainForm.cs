using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading;
using System.Text.RegularExpressions;
using System.Security;
using System.Linq;

namespace ClipboardInterceptor
{
    public class MainForm : Form
    {
        // --- Clipboard listener ---
        private const int WM_CLIPBOARDUPDATE = 0x031D;
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern bool EmptyClipboard();
        [DllImport("user32.dll")]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        private void InitializeComponent() { }

        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();

        // --- Low-level keyboard hook ---
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private LowLevelKeyboardProc _proc;
        private IntPtr _hookID = IntPtr.Zero;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
                                                     IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode,
                                                    IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        // --- Tray & session state ---
        private readonly NotifyIcon trayIcon;
        private readonly ToolStripMenuItem toggleItem;
        private readonly ToolStripMenuItem showHistoryItem;
        private bool sessionActive = false;
        private bool suppressClipboardEncryption = false;

        // --- Enhanced clipboard management ---
        private string lastEncryptedContent = "";
        private DateTime lastDecryptTime = DateTime.MinValue;
        private readonly object clipboardLock = new object();
        private System.Threading.Timer restoreTimer;
        private bool isProcessingClipboard = false;

        // --- Security enhancements ---
        private readonly System.Windows.Forms.Timer autoClearTimer;
        private readonly System.Windows.Forms.Timer inactivityTimer;
        private int decryptionTimeout = 2000; // 2 seconds - increased from 30ms
        private bool autoClearEnabled = false;
        private bool autoLockEnabled = true;
        private int autoLockMinutes = 5;
        private DateTime lastActivity = DateTime.Now;
        private bool isAuthenticated = false;
        private string encryptionMarker = "ENC:";
        private bool detectSensitiveData = true;

        // --- Screenshot detection ---
        private readonly ScreenshotDetector screenshotDetector;
        private bool screenshotDetectionEnabled = true;

        // --- Global hotkey for history ---
        private const int MOD_CONTROL = 0x0002;
        private const int MOD_ALT = 0x0001;
        private const int WM_HOTKEY = 0x0312;
        private const int HISTORY_HOTKEY_ID = 1;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public MainForm()
        {
            LoadSettings();

            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Visible = false;

            trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "ClipboardInterceptor (OFF)",
                Visible = true
            };

            var menu = new ContextMenuStrip();
            toggleItem = new ToolStripMenuItem("Start Protection", null, OnToggleProtection);
            showHistoryItem = new ToolStripMenuItem("Show Clipboard History", null, OnShowHistory);

            menu.Items.Add(toggleItem);
            menu.Items.Add(showHistoryItem);
            menu.Items.Add("Settings", null, OnShowSettings);
            menu.Items.Add("Clear Clipboard & History", null, OnClearClipboard);
            menu.Items.Add("Exit", null, (s, e) =>
            {
                SecureCleanup();
                trayIcon.Visible = false;
                Application.Exit();
            });

            trayIcon.ContextMenuStrip = menu;
            trayIcon.DoubleClick += (s, e) => OnShowHistory(s, e);

            // Auto-clear timer
            autoClearTimer = new System.Windows.Forms.Timer
            {
                Interval = 60000,
                Enabled = autoClearEnabled
            };
            autoClearTimer.Tick += (s, e) => ClearClipboardSecurely();

            // Inactivity timer for auto-lock
            inactivityTimer = new System.Windows.Forms.Timer
            {
                Interval = 60000,
                Enabled = autoLockEnabled
            };
            inactivityTimer.Tick += (s, e) => CheckInactivity();

            // Screenshot detector
            screenshotDetector = new ScreenshotDetector();
            screenshotDetector.ScreenshotDetected += OnScreenshotDetected;

            if (screenshotDetectionEnabled)
            {
                screenshotDetector.Start();
            }

            if (bool.Parse(DatabaseManager.Instance.GetSetting("AutoStartProtection", "false")))
            {
                sessionActive = true;
                toggleItem.Text = "Stop Protection";
                trayIcon.Text = "ClipboardInterceptor (ON)";
            }

            if (!Crypto.IsPinSet())
            {
                using var pinForm = new PinForm();
                if (pinForm.ShowDialog() == DialogResult.OK)
                {
                    Crypto.SetPIN(pinForm.PIN);
                    MessageBox.Show("Security PIN set successfully. This PIN will be required to view your encrypted clipboard history.",
                        "Security", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }

            System.Threading.Timer cleanupTimer = new System.Threading.Timer(
                _ => DatabaseManager.Instance.CleanupExpiredItems(),
                null,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromHours(12));
        }

        private void LoadSettings()
        {
            var db = DatabaseManager.Instance;
            decryptionTimeout = int.Parse(db.GetSetting("DecryptionTimeout", "2000"));
            autoClearEnabled = bool.Parse(db.GetSetting("AutoCleanupHistory", "true"));
            autoLockEnabled = bool.Parse(db.GetSetting("AutoLock", "true"));
            autoLockMinutes = int.Parse(db.GetSetting("AutoLockMinutes", "5"));
            screenshotDetectionEnabled = bool.Parse(db.GetSetting("ScreenshotDetection", "true"));
            detectSensitiveData = bool.Parse(db.GetSetting("DetectSensitiveData", "true"));
            encryptionMarker = db.GetSetting("EncryptionMarker", "ENC:");
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            AddClipboardFormatListener(Handle);

            _proc = HookCallback;
            _hookID = SetHook(_proc);

            RegisterHotKey(Handle, HISTORY_HOTKEY_ID, MOD_CONTROL | MOD_ALT, (uint)Keys.V);
        }

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            return SetWindowsHookEx(
                WH_KEYBOARD_LL,
                proc,
                GetModuleHandle(curModule.ModuleName!),
                0
            );
        }

        private void OnToggleProtection(object sender, EventArgs e)
        {
            sessionActive = !sessionActive;
            if (sessionActive)
            {
                toggleItem.Text = "Stop Protection";
                trayIcon.Text = "ClipboardInterceptor (ON)";
            }
            else
            {
                toggleItem.Text = "Start Protection";
                trayIcon.Text = "ClipboardInterceptor (OFF)";
                ClearClipboardSecurely();
            }
        }

        private void OnShowHistory(object sender, EventArgs e)
        {
            lastActivity = DateTime.Now;
            using var historyForm = new HistoryViewerForm();
            historyForm.ShowDialog();
        }

        private void OnShowSettings(object sender, EventArgs e)
        {
            lastActivity = DateTime.Now;
            using var settingsForm = new SettingsForm();
            if (settingsForm.ShowDialog() == DialogResult.OK)
            {
                LoadSettings();
                autoClearTimer.Enabled = autoClearEnabled;
                inactivityTimer.Enabled = autoLockEnabled;

                if (screenshotDetectionEnabled)
                    screenshotDetector.Start();
                else
                    screenshotDetector.Stop();
            }
        }

        private void OnScreenshotDetected(object sender, EventArgs e)
        {
            if (!screenshotDetectionEnabled || !sessionActive)
                return;

            this.Invoke((Action)(() =>
            {
                trayIcon.ShowBalloonTip(
                    5000,
                    "Screenshot Detected",
                    "A screenshot was taken. Be aware that sensitive data may be in plain text on your screen.",
                    ToolTipIcon.Warning);
            }));
        }

        private void OnClearClipboard(object sender, EventArgs e)
        {
            DialogResult result = MessageBox.Show(
                "Are you sure you want to clear clipboard and all history? This cannot be undone.",
                "Confirm Clear All",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                ClearClipboardSecurely();
                DatabaseManager.Instance.DeleteAllItems();

                trayIcon.ShowBalloonTip(
                    3000,
                    "Clipboard Cleared",
                    "Clipboard contents and history have been securely cleared.",
                    ToolTipIcon.Info);
            }
        }

        private void CheckInactivity()
        {
            if (!autoLockEnabled || !isAuthenticated)
                return;

            TimeSpan idle = DateTime.Now - lastActivity;
            if (idle.TotalMinutes >= autoLockMinutes)
            {
                isAuthenticated = false;

                trayIcon.ShowBalloonTip(
                    3000,
                    "Security Lock",
                    "Your session has been locked due to inactivity.",
                    ToolTipIcon.Info);
            }
        }

        private void ClearClipboardSecurely()
        {
            try
            {
                lock (clipboardLock)
                {
                    RemoveClipboardFormatListener(Handle);

                    if (OpenClipboard(Handle))
                    {
                        EmptyClipboard();
                        CloseClipboard();
                    }

                    Thread.Sleep(10);
                    Clipboard.SetText(" ");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error clearing clipboard: {ex.Message}");
            }
            finally
            {
                AddClipboardFormatListener(Handle);
            }
        }

        private void SecureCleanup()
        {
            try
            {
                ClearClipboardSecurely();
                isAuthenticated = false;

                if (screenshotDetectionEnabled)
                {
                    screenshotDetector.Stop();
                }

                restoreTimer?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during cleanup: {ex.Message}");
            }
        }

        private readonly SemaphoreSlim clipboardSemaphore = new SemaphoreSlim(1, 1);

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE && sessionActive)
            {
                // Use async processing to avoid blocking
                Task.Run(async () =>
                {
                    await clipboardSemaphore.WaitAsync();
                    try
                    {
                        await Task.Delay(50); // Give time for clipboard to stabilize

                        this.Invoke((Action)(() =>
                        {
                            if (autoClearEnabled)
                            {
                                autoClearTimer.Stop();
                                autoClearTimer.Start();
                            }

                            lastActivity = DateTime.Now;

                            if (!suppressClipboardEncryption && !isProcessingClipboard)
                            {
                                ProcessClipboardContent();
                            }
                        }));
                    }
                    finally
                    {
                        clipboardSemaphore.Release();
                    }
                });

                return;
            }

            base.WndProc(ref m);
        }
        private void ProcessClipboardContent()
        {
            lock (clipboardLock)
            {
                if (isProcessingClipboard)
                    return;

                isProcessingClipboard = true;
            }

            try
            {
                // Determine clipboard content type and priority
                bool hasText = Clipboard.ContainsText();
                bool hasImage = Clipboard.ContainsImage();
                bool hasFiles = Clipboard.ContainsFileDropList();

                // Skip if already encrypted text
                if (hasText)
                {
                    string currentText = Clipboard.GetText();
                    if (currentText.StartsWith(encryptionMarker) || string.IsNullOrWhiteSpace(currentText))
                    {
                        return;
                    }
                }

                string contentId = Guid.NewGuid().ToString();
                DateTime timestamp = DateTime.Now;

                ClipboardItem item = new ClipboardItem
                {
                    Timestamp = timestamp,
                    ContentId = contentId
                };

                // Priority: Files > Images > Text
                // This is important because Windows sometimes sets multiple formats
                if (hasFiles)
                {
                    ProcessFileContent(item, contentId);
                }
                else if (hasImage && !hasText) // Only process image if no text (to avoid processing screenshots as both)
                {
                    ProcessImageContent(item, contentId);
                }
                else if (hasText)
                {
                    ProcessTextContent(item, contentId);
                }

                // Save to database
                if (item.EncryptedData != null)
                {
                    try
                    {
                        DatabaseManager.Instance.SaveClipboardItem(item);
                        Debug.WriteLine($"Saved {item.ItemType} item to database with ID: {item.ContentId}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error saving to database: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error processing clipboard: {ex.Message}");
            }
            finally
            {
                lock (clipboardLock)
                {
                    isProcessingClipboard = false;
                }
            }
        }

        private void ProcessFileContent(ClipboardItem item, string contentId)
        {
            try
            {
                StringCollection files = Clipboard.GetFileDropList();
                string[] filePaths = new string[files.Count];
                files.CopyTo(filePaths, 0);

                RemoveClipboardFormatListener(Handle);

                string encryptedPaths = Crypto.EncryptFilePaths(filePaths, contentId);

                Clipboard.Clear();
                Thread.Sleep(10);
                Clipboard.SetText($"{encryptionMarker}{contentId}|{encryptedPaths}");

                item.ItemType = ClipboardItemType.File;
                item.EncryptedData = encryptedPaths;
                item.Preview = $"[{files.Count} file(s): {Path.GetFileName(filePaths[0])}" +
                              (files.Count > 1 ? $" +{files.Count - 1} more" : "") + "]";
                item.IsSensitive = false;

                AddClipboardFormatListener(Handle);
                Debug.WriteLine($"Encrypted {files.Count} files");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error processing files: {ex.Message}");
                AddClipboardFormatListener(Handle);
            }
        }

        private void ProcessImageContent(ClipboardItem item, string contentId)
        {
            try
            {
                Image img = Clipboard.GetImage();

                RemoveClipboardFormatListener(Handle);

                string encryptedImage = Crypto.EncryptImage(img, contentId);

                item.ItemType = ClipboardItemType.Image;
                item.EncryptedData = encryptedImage;
                item.Preview = "[Encrypted Image]";
                item.IsSensitive = false;

                using (Bitmap placeholder = new Bitmap(200, 100))
                using (Graphics g = Graphics.FromImage(placeholder))
                {
                    g.FillRectangle(Brushes.LightGray, 0, 0, 200, 100);
                    g.DrawString("Encrypted Image", new Font("Arial", 10), Brushes.Black, 10, 40);
                    g.DrawString($"ID: {contentId.Substring(0, 8)}...", new Font("Arial", 8), Brushes.DarkGray, 10, 60);

                    Clipboard.Clear();
                    Thread.Sleep(10);
                    Clipboard.SetImage(placeholder);
                }

                AddClipboardFormatListener(Handle);
                Debug.WriteLine("Encrypted image content");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error processing image: {ex.Message}");
                AddClipboardFormatListener(Handle);
            }
        }

        private void ProcessTextContent(ClipboardItem item, string contentId)
        {
            try
            {
                string text = Clipboard.GetText();

                if (detectSensitiveData)
                {
                    item.IsSensitive = IsSensitiveData(text);
                }

                string preview = Crypto.CreateEncryptedPreview(text);

                // Tambahkan delay yang lebih konsisten
                RemoveClipboardFormatListener(Handle);

                byte[] encryptedData = Crypto.Encrypt(text, contentId);
                string encodedText = Convert.ToBase64String(encryptedData);
                string encryptedFormat = $"{encryptionMarker}{contentId}|{encodedText}";

                // Tambahkan retry mechanism
                int retryCount = 3;
                bool success = false;

                while (retryCount > 0 && !success)
                {
                    try
                    {
                        Clipboard.Clear();
                        Thread.Sleep(50); // Increase delay
                        Clipboard.SetText(encryptedFormat);

                        // Verify clipboard content
                        Thread.Sleep(20);
                        string verification = Clipboard.GetText();
                        if (verification == encryptedFormat)
                        {
                            success = true;
                        }
                        else
                        {
                            retryCount--;
                            Thread.Sleep(100);
                        }
                    }
                    catch
                    {
                        retryCount--;
                        Thread.Sleep(100);
                    }
                }

                item.ItemType = ClipboardItemType.Text;
                item.EncryptedData = encodedText;
                item.Preview = preview;

                if (item.IsSensitive)
                {
                    item.ExpiresAt = DateTime.Now.AddHours(24);
                    ShowSensitiveDataNotification();
                }

                AddClipboardFormatListener(Handle);
                Debug.WriteLine($"Encrypted text content - Success: {success}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error processing text: {ex.Message}");
                AddClipboardFormatListener(Handle);
            }
        }
        private bool IsSensitiveData(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            if (Regex.IsMatch(text, @"(?i)pass(word)?|admin|administrator|123456|qwerty|abc123"))
                return true;

            if (Regex.IsMatch(text, @"[a-zA-Z0-9_\-]{20,}"))
                return true;

            if (Regex.IsMatch(text, @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}"))
                return true;

            if (Regex.IsMatch(text, @"\b((25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])\.){3}(25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])\b"))
                return true;

            return false;
        }

        private void ShowSensitiveDataNotification()
        {
            trayIcon.ShowBalloonTip(
                5000,
                "Sensitive Data Detected",
                "Sensitive information has been encrypted and will be automatically removed from history after 24 hours.",
                ToolTipIcon.Warning);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN && sessionActive)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                bool ctrl = (Control.ModifierKeys & Keys.Control) == Keys.Control;

                if (ctrl && vkCode == (int)Keys.V)
                {
                    lastActivity = DateTime.Now;

                    this.Invoke((Action)(() =>
                    {
                        lock (clipboardLock)
                        {
                            try
                            {
                                if (Clipboard.ContainsText())
                                {
                                    string txt = Clipboard.GetText();

                                    if (txt.StartsWith(encryptionMarker))
                                    {
                                        string payload = txt.Substring(encryptionMarker.Length);
                                        int separatorIndex = payload.IndexOf('|');

                                        if (separatorIndex > 0)
                                        {
                                            string contentId = payload.Substring(0, separatorIndex);
                                            string encryptedBase64 = payload.Substring(separatorIndex + 1);

                                            // Check if this is a file list or regular text
                                            ClipboardItem relatedItem = FindItemByContentId(contentId);

                                            if (relatedItem != null && relatedItem.ItemType == ClipboardItemType.File)
                                            {
                                                // This is a file list, restore as FileDropList
                                                ProcessFilePaste(contentId, encryptedBase64);
                                            }
                                            else
                                            {
                                                // This is regular text
                                                ProcessTextPaste(contentId, encryptedBase64);
                                            }
                                        }
                                    }
                                }
                                else if (Clipboard.ContainsImage())
                                {
                                    ProcessImagePaste();
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Error in paste processing: {ex.Message}");
                                suppressClipboardEncryption = false;
                            }
                        }
                    }));
                }
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private ClipboardItem FindItemByContentId(string contentId)
        {
            try
            {
                var recentItems = DatabaseManager.Instance.GetRecentItems(50);
                return recentItems.FirstOrDefault(item => item.ContentId == contentId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error finding item by content ID: {ex.Message}");
                return null;
            }
        }

        private void ProcessTextPaste(string contentId, string encryptedBase64)
        {
            try
            {
                byte[] data = Convert.FromBase64String(encryptedBase64);
                string plain = Crypto.Decrypt(data, contentId);

                // Store original encrypted content
                lastEncryptedContent = $"{encryptionMarker}{contentId}|{encryptedBase64}";
                lastDecryptTime = DateTime.Now;

                // Remove listener and set plaintext
                RemoveClipboardFormatListener(Handle);
                suppressClipboardEncryption = true;

                Clipboard.Clear();
                Thread.Sleep(10);
                Clipboard.SetText(plain);

                // Schedule restoration
                restoreTimer?.Dispose();
                restoreTimer = new System.Threading.Timer(RestoreEncryptedContent,
                    lastEncryptedContent, decryptionTimeout, Timeout.Infinite);

                AddClipboardFormatListener(Handle);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Text decryption error: {ex.Message}");
                suppressClipboardEncryption = false;
                AddClipboardFormatListener(Handle);
            }
        }

        private void ProcessFilePaste(string contentId, string encryptedBase64)
        {
            try
            {
                string[] filePaths = Crypto.DecryptFilePaths(encryptedBase64, contentId);

                // Store original encrypted content for restoration
                lastEncryptedContent = $"{encryptionMarker}{contentId}|{encryptedBase64}";
                lastDecryptTime = DateTime.Now;

                // Remove listener and set file list
                RemoveClipboardFormatListener(Handle);
                suppressClipboardEncryption = true;

                Clipboard.Clear();
                Thread.Sleep(10);

                // Set as file drop list
                StringCollection fileCollection = new StringCollection();
                fileCollection.AddRange(filePaths);
                Clipboard.SetFileDropList(fileCollection);

                // Schedule restoration
                restoreTimer?.Dispose();
                restoreTimer = new System.Threading.Timer(RestoreEncryptedContent,
                    lastEncryptedContent, decryptionTimeout, Timeout.Infinite);

                AddClipboardFormatListener(Handle);

                Debug.WriteLine($"Restored {filePaths.Length} files to clipboard");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"File decryption error: {ex.Message}");
                suppressClipboardEncryption = false;
                AddClipboardFormatListener(Handle);
            }
        }

        private void RestoreEncryptedContent(object state)
        {
            string encryptedContent = state as string;

            this.Invoke((Action)(() =>
            {
                lock (clipboardLock)
                {
                    try
                    {
                        RemoveClipboardFormatListener(Handle);

                        Clipboard.Clear();
                        Thread.Sleep(10);

                        // Set back to encrypted content
                        Clipboard.SetText(encryptedContent);

                        // Reset suppression flag
                        suppressClipboardEncryption = false;

                        Debug.WriteLine("Restored encrypted content to clipboard");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error restoring encrypted content: {ex.Message}");
                        suppressClipboardEncryption = false;
                    }
                    finally
                    {
                        AddClipboardFormatListener(Handle);
                    }
                }
            }));
        }

        private void ProcessImagePaste()
        {
            try
            {
                List<ClipboardItem> recentItems = DatabaseManager.Instance.GetRecentItems(10);
                ClipboardItem latestImageItem = null;

                foreach (var item in recentItems)
                {
                    if (item.ItemType == ClipboardItemType.Image)
                    {
                        latestImageItem = item;
                        break;
                    }
                }

                if (latestImageItem != null)
                {
                    RemoveClipboardFormatListener(Handle);
                    suppressClipboardEncryption = true;
                    lastDecryptTime = DateTime.Now;

                    Image originalImage = Crypto.DecryptImage(latestImageItem.EncryptedData, latestImageItem.ContentId);

                    if (originalImage != null)
                    {
                        Clipboard.Clear();
                        Thread.Sleep(10);
                        Clipboard.SetImage(originalImage);

                        // Schedule restoration to placeholder
                        restoreTimer?.Dispose();
                        restoreTimer = new System.Threading.Timer(RestoreImagePlaceholder,
                            null, decryptionTimeout, Timeout.Infinite);
                    }

                    AddClipboardFormatListener(Handle);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error decrypting image on paste: {ex.Message}");
                suppressClipboardEncryption = false;
                AddClipboardFormatListener(Handle);
            }
        }

        private void RestoreImagePlaceholder(object state)
        {
            this.Invoke((Action)(() =>
            {
                lock (clipboardLock)
                {
                    try
                    {
                        RemoveClipboardFormatListener(Handle);

                        Clipboard.Clear();
                        Thread.Sleep(10);

                        // Create and set placeholder image
                        using (Bitmap placeholder = new Bitmap(200, 100))
                        using (Graphics g = Graphics.FromImage(placeholder))
                        {
                            g.FillRectangle(Brushes.White, 0, 0, 200, 100);
                            g.DrawString("Encrypted Image", new Font("Arial", 10), Brushes.Black, 10, 40);
                            Clipboard.SetImage(placeholder);
                        }

                        suppressClipboardEncryption = false;

                        Debug.WriteLine("Restored image placeholder to clipboard");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error restoring image placeholder: {ex.Message}");
                        suppressClipboardEncryption = false;
                    }
                    finally
                    {
                        AddClipboardFormatListener(Handle);
                    }
                }
            }));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing &&
                bool.Parse(DatabaseManager.Instance.GetSetting("MinimizeToTray", "true")))
            {
                e.Cancel = true;
                WindowState = FormWindowState.Minimized;
                Hide();

                trayIcon.ShowBalloonTip(
                    3000,
                    "ClipboardInterceptor",
                    "Application is still running in the system tray.",
                    ToolTipIcon.Info);

                return;
            }

            UnregisterHotKey(Handle, HISTORY_HOTKEY_ID);
            UnhookWindowsHookEx(_hookID);
            RemoveClipboardFormatListener(Handle);
            screenshotDetector.Stop();
            trayIcon.Dispose();

            SecureCleanup();

            base.OnFormClosing(e);
        }
    }
}
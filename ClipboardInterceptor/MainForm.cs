﻿// MainForm.cs

using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ClipboardInterceptor
{
    // -----------------------------------------------------------------
    //  Main window is hidden – everything runs from tray + clipboard
    // -----------------------------------------------------------------
    public class MainForm : Form
    {
        // ---------- Win32 + clipboard --------------------------------
        private const int WM_CLIPBOARDUPDATE = 0x031D;
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool EmptyClipboard();
        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();

        // ---------- Low-level keyboard hook (detect Ctrl-V) -----------
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private delegate IntPtr LowLevelKeyboardProc(int n, IntPtr w, IntPtr l);
        private LowLevelKeyboardProc _proc;
        private IntPtr _hookID = IntPtr.Zero;
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
                                                      IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int n, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string name);

        // ---------- Tray / UI ----------------------------------------
        private readonly NotifyIcon trayIcon;
        private readonly ToolStripMenuItem toggleItem;
        private readonly ToolStripMenuItem showHistoryItem;

        // ---------- Runtime flags ------------------------------------
        private bool sessionActive = false;
        private bool suppressClipboardEncryption = false;
        private readonly object clipboardLock = new();
        private bool isProcessingClipboard = false;
        private bool isAuthenticated = false;

        // ---------- Timing / security --------------------------------
        private const int DEFAULT_DECRYPTION_TIMEOUT = 60; // ms
        private int decryptionTimeout = DEFAULT_DECRYPTION_TIMEOUT;
        private readonly System.Threading.Timer restoreTimer;
        private readonly System.Windows.Forms.Timer autoClearTimer;
        private readonly System.Windows.Forms.Timer inactivityTimer;
        private DateTime lastActivity = DateTime.Now;
        private DateTime lastDecryptTime = DateTime.MinValue;
        private readonly SemaphoreSlim clipboardSem = new(1, 1);

        // ---------- Configurable settings / detection ----------------
        private bool autoClearEnabled = false;
        private bool autoLockEnabled = true;
        private int autoLockMinutes = 5;
        private bool screenshotDetectionEnabled = true;
        private bool detectSensitiveData = true;
        private string encryptionMarker = "ENC:";

        // ---------- NEW: Enhanced retention and notification settings
        private int sensitiveDataRetentionHours = 3;  // Default 3 hours for sensitive
        private int normalDataRetentionHours = 12;    // Default 12 hours for normal
        private bool enableSensitiveNotifications = true;
        private readonly HashSet<string> notifiedSensitiveData = new HashSet<string>();

        // ---------- Misc helpers -------------------------------------
        private string lastEncryptedContent = "";
        private ScreenshotDetector screenshotDetector;
        private string appName = "ClipboardInterceptor";

        // ---------- Hot-key for History viewer -----------------------
        private const int MOD_CONTROL = 0x0002;
        private const int MOD_ALT = 0x0001;
        private const int WM_HOTKEY = 0x0312;
        private const int HISTORY_HOTKEY_ID = 1;
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey
            (IntPtr hWnd, int id, uint fs, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey
            (IntPtr hWnd, int id);

        // =============================================================
        //  Constructor – build tray, timers, hooks
        // =============================================================
        public MainForm()
        {
            LoadSettings();                       // read DB -> fields

            // ---------- invisible main window ------------------------
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Visible = false;

            // ---------- PERBAIKAN: Validasi PIN pada setiap startup ----------
            if (!Crypto.IsPinSet())
            {
                // Jika PIN belum diset, minta user untuk set PIN
                using var pinForm = new PinForm(true); // true untuk set PIN baru
                if (pinForm.ShowDialog() == DialogResult.OK)
                {
                    Crypto.SetPIN(pinForm.PIN);
                    MessageBox.Show("Security PIN set successfully.",
                                    "PIN Set", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    isAuthenticated = true;
                }
                else
                {
                    // Jika user cancel, tutup aplikasi
                    MessageBox.Show("PIN is required to run ClipboardInterceptor.",
                                    "PIN Required", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Application.Exit();
                    return;
                }
            }
            else
            {
                // PIN sudah diset, minta validasi PIN
                if (!ValidatePinOnStartup())
                {
                    Application.Exit();
                    return;
                }
            }

            // ---------- tray icon + menu -----------------------------
            trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = $"{appName} (OFF)",
                Visible = true
            };

            var menu = new ContextMenuStrip();
            toggleItem = new ToolStripMenuItem("Start Protection", null, OnToggleProtection);
            showHistoryItem = new ToolStripMenuItem("Show Clipboard History", null, OnShowHistory);

            menu.Items.AddRange(new ToolStripItem[]{
                toggleItem, showHistoryItem,
                new ToolStripMenuItem("Settings",null,OnShowSettings),
                new ToolStripMenuItem("Clear Clipboard & History",null,OnClearClipboard),
                new ToolStripSeparator(),
                new ToolStripMenuItem("Exit",null,(s,e)=>{
                    SecureCleanup();
                    trayIcon.Visible=false;
                    Application.Exit();
                })
            });

            trayIcon.ContextMenuStrip = menu;
            trayIcon.DoubleClick += (sender, e) =>
            {
                OnShowHistory(sender, e);
            };

            // ---------- timers ---------------------------------------
            autoClearTimer = new() { Interval = 60_000 };
            autoClearTimer.Tick += (_, __) => ClearClipboardSecurely();

            inactivityTimer = new() { Interval = 60_000 };
            inactivityTimer.Tick += (_, __) => CheckInactivity();

            // ---------- screenshot detector --------------------------
            screenshotDetector = new ScreenshotDetector();
            screenshotDetector.ScreenshotDetected += OnScreenshotDetected;
            if (screenshotDetectionEnabled) screenshotDetector.Start();

            // ---------- protection autostart? ------------------------
            if (bool.Parse(DatabaseManager.Instance.GetSetting("AutoStartProtection", "false")))
            {
                sessionActive = true;
                toggleItem.Text = "Stop Protection";
                trayIcon.Text = $"{appName} (ON)";

                AddClipboardFormatListener(Handle);
                autoClearTimer.Start();
                inactivityTimer.Start();
            }

            // ---------- UPDATED: Enhanced periodic cleanup with category-based retention
            _ = new System.Threading.Timer(_ =>
            {
                DatabaseManager.Instance.CleanupExpiredItemsByCategory();
            }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));
        }

        // ---------- Method baru untuk validasi PIN startup ----------
        private bool ValidatePinOnStartup()
        {
            int maxAttempts = 3;
            int currentAttempt = 0;

            while (currentAttempt < maxAttempts)
            {
                // Check lockout status
                var (isLocked, remainingMinutes) = Crypto.GetLockoutStatus();
                if (isLocked)
                {
                    MessageBox.Show(
                        $"Too many failed PIN attempts. Please try again in {remainingMinutes} minutes.",
                        "Authentication Locked",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return false;
                }

                using var pinForm = new PinForm(false); // false untuk validasi PIN
                pinForm.Text = $"Enter PIN to start ClipboardInterceptor (Attempt {currentAttempt + 1}/{maxAttempts})";

                if (pinForm.ShowDialog() != DialogResult.OK)
                {
                    // User cancelled
                    return false;
                }

                if (Crypto.VerifyPIN(pinForm.PIN))
                {
                    // PIN benar, set authenticated flag
                    isAuthenticated = true;
                    return true;
                }
                else
                {
                    currentAttempt++;
                    if (currentAttempt < maxAttempts)
                    {
                        MessageBox.Show(
                            $"Incorrect PIN. {maxAttempts - currentAttempt} attempts remaining.",
                            "Authentication Failed",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                    else
                    {
                        MessageBox.Show(
                            "Maximum PIN attempts exceeded. Application will close.",
                            "Authentication Failed",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        return false;
                    }
                }
            }

            return false;
        }

        // =============================================================
        //  Settings
        // =============================================================
        private void LoadSettings()
        {
            var db = DatabaseManager.Instance;

            decryptionTimeout = int.Parse(db.GetSetting("DecryptionTimeout",
                                                DEFAULT_DECRYPTION_TIMEOUT.ToString()));
            autoClearEnabled = bool.Parse(db.GetSetting("AutoCleanupHistory", "true"));
            autoLockEnabled = bool.Parse(db.GetSetting("AutoLock", "true"));
            autoLockMinutes = int.Parse(db.GetSetting("AutoLockMinutes", "5"));
            screenshotDetectionEnabled = bool.Parse(db.GetSetting("ScreenshotDetection", "true"));
            detectSensitiveData = bool.Parse(db.GetSetting("DetectSensitiveData", "true"));
            encryptionMarker = db.GetSetting("EncryptionMarker", "ENC:");

            // NEW: Load retention and notification settings
            sensitiveDataRetentionHours = int.Parse(db.GetSetting("SensitiveRetention", "3"));
            normalDataRetentionHours = int.Parse(db.GetSetting("NormalRetention", "12"));
            enableSensitiveNotifications = bool.Parse(db.GetSetting("EnableSensitiveNotifications", "true"));
        }

        // =============================================================
        //  Windows lifecycle & hooks
        // =============================================================
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
            using var curProc = Process.GetCurrentProcess();
            using var curMod = curProc.MainModule!;
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc,
                                    GetModuleHandle(curMod.ModuleName!), 0);
        }

        // =============================================================
        //  Tray menu callbacks
        // =============================================================
        private void OnToggleProtection(object sender, EventArgs e)
        {
            sessionActive = !sessionActive;

            if (sessionActive)            // ---- PROTECTION ON ----
            {
                AddClipboardFormatListener(Handle);
                autoClearTimer.Start();
                inactivityTimer.Start();

                toggleItem.Text = "Stop Protection";
                trayIcon.Text = $"{appName} (ON)";
            }
            else                          // ---- PROTECTION OFF ----
            {
                autoClearTimer.Stop();
                inactivityTimer.Stop();
                RemoveClipboardFormatListener(Handle);
                ClearClipboardSecurely();          // bersihkan sekali saja

                toggleItem.Text = "Start Protection";
                trayIcon.Text = $"{appName} (OFF)";
            }
        }

        private void OnShowHistory(object s, EventArgs e)
        {
            lastActivity = DateTime.Now;
            using var f = new HistoryViewerForm();
            f.ShowDialog();
        }

        private void OnShowSettings(object s, EventArgs e)
        {
            lastActivity = DateTime.Now;
            using var f = new SettingsForm();
            if (f.ShowDialog() == DialogResult.OK)
            {
                LoadSettings();
                autoClearTimer.Enabled = autoClearEnabled;
                inactivityTimer.Enabled = autoLockEnabled;

                if (screenshotDetectionEnabled) screenshotDetector.Start();
                else screenshotDetector.Stop();
            }
        }

        private void OnScreenshotDetected(object s, EventArgs e)
        {
            if (!sessionActive) return;
            trayIcon.ShowBalloonTip(5000, "Screenshot Detected",
                "A screenshot was taken – be mindful of sensitive data on screen.",
                ToolTipIcon.Warning);
        }

        private void OnClearClipboard(object s, EventArgs e)
        {
            if (MessageBox.Show("Clear clipboard & ALL history?", "Confirm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                ClearClipboardSecurely();
                DatabaseManager.Instance.DeleteAllItems();
                trayIcon.ShowBalloonTip(3000, "Cleared",
                    "Clipboard & history wiped.", ToolTipIcon.Info);
            }
        }

        // =============================================================
        //  Inactivity lock / auto clear
        // =============================================================
        private void CheckInactivity()
        {
            if (!autoLockEnabled || !isAuthenticated) return;
            if ((DateTime.Now - lastActivity).TotalMinutes >= autoLockMinutes)
            {
                isAuthenticated = false;
                trayIcon.ShowBalloonTip(3000, "Security Lock",
                    "History locked due to inactivity.", ToolTipIcon.Info);
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

                    // ✔ tidak menulis spasi—pakai API no-history, jadi tidak terekam Win + V
                    Thread.Sleep(10);
                    ClipboardHelper.SetTextNoHistory(string.Empty);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ClearClip: {ex.Message}");
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
                screenshotDetector.Stop();
            }
            catch (Exception ex) { Debug.WriteLine($"Cleanup: {ex.Message}"); }
        }

        // =============================================================
        //  Core message loop – intercept WM_CLIPBOARDUPDATE
        // =============================================================
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE && sessionActive)
            {
                _ = Task.Run(async () => {
                    await clipboardSem.WaitAsync();
                    try
                    {
                        await Task.Delay(50);
                        this.Invoke((Action)(() =>
                        {
                            if (autoClearEnabled) { autoClearTimer.Stop(); autoClearTimer.Start(); }
                            lastActivity = DateTime.Now;
                            if (!suppressClipboardEncryption && !isProcessingClipboard)
                                ProcessClipboardContent();
                        }));
                    }
                    finally { clipboardSem.Release(); }
                });
                return;
            }
            base.WndProc(ref m);
        }

        // =============================================================
        //  Clipboard processing (encrypt on copy)
        // =============================================================
        private void ProcessClipboardContent()
        {
            lock (clipboardLock)
            {
                if (isProcessingClipboard) return;
                isProcessingClipboard = true;
            }

            try
            {
                // CRITICAL: Remove listener immediately to prevent re-entry
                RemoveClipboardFormatListener(Handle);

                // Step 1: Quickly capture what's in clipboard
                string capturedText = null;
                Image capturedImage = null;
                StringCollection capturedFiles = null;

                bool hasText = Clipboard.ContainsText();
                bool hasImage = Clipboard.ContainsImage();
                bool hasFiles = Clipboard.ContainsFileDropList();

                // Quick capture based on content type
                if (hasText)
                {
                    capturedText = Clipboard.GetText();
                    // Skip if already encrypted
                    if (capturedText.StartsWith(encryptionMarker) || string.IsNullOrWhiteSpace(capturedText))
                    {
                        return;
                    }
                }
                else if (hasImage && !hasText)
                {
                    capturedImage = Clipboard.GetImage();
                }
                else if (hasFiles)
                {
                    capturedFiles = Clipboard.GetFileDropList();
                }
                else
                {
                    return; // Nothing to process
                }

                // Step 2: IMMEDIATELY clear clipboard to minimize exposure window
                Clipboard.Clear();
                Thread.Sleep(5); // Very short delay to ensure clear

                // Step 3: Set temporary placeholder to block snooping
                Clipboard.SetText("[Protected by ClipboardInterceptor]");
                Thread.Sleep(5);

                // Step 4: Now process the captured content safely
                string contentId = Guid.NewGuid().ToString();
                var item = new ClipboardItem { Timestamp = DateTime.Now, ContentId = contentId };

                // Process based on what we captured
                if (capturedFiles != null)
                {
                    // Convert StringCollection to string array
                    string[] paths = new string[capturedFiles.Count];
                    capturedFiles.CopyTo(paths, 0);

                    string enc = Crypto.EncryptFilePaths(paths, contentId);
                    Clipboard.SetText($"{encryptionMarker}{contentId}|{enc}");

                    item.ItemType = ClipboardItemType.File;
                    item.EncryptedData = enc;
                    item.Preview = $"[{paths.Length} file(s)]";
                    item.IsSensitive = false;
                    item.ExpiresAt = DateTime.Now.AddHours(normalDataRetentionHours);
                }
                else if (capturedImage != null)
                {
                    string enc = Crypto.EncryptImage(capturedImage, contentId);
                    item.ItemType = ClipboardItemType.Image;
                    item.EncryptedData = enc;
                    item.Preview = "[Encrypted Image]";
                    item.IsSensitive = false;
                    item.ExpiresAt = DateTime.Now.AddHours(normalDataRetentionHours);

                    // Create encrypted placeholder image
                    using (Bitmap ph = new Bitmap(200, 100))
                    using (Graphics g = Graphics.FromImage(ph))
                    {
                        g.FillRectangle(Brushes.LightGray, 0, 0, 200, 100);
                        g.DrawString("Encrypted Image", new Font("Arial", 10), Brushes.Black, 10, 40);
                        Clipboard.SetImage(ph);
                    }
                }
                else if (capturedText != null)
                {
                    // Check if sensitive
                    if (detectSensitiveData)
                    {
                        item.IsSensitive = IsSensitiveData(capturedText);
                        item.ExpiresAt = DateTime.Now.AddHours(
                            item.IsSensitive ? sensitiveDataRetentionHours : normalDataRetentionHours);

                        if (item.IsSensitive)
                        {
                            ShowSensitiveDataNotification(capturedText);
                        }
                    }
                    else
                    {
                        item.ExpiresAt = DateTime.Now.AddHours(normalDataRetentionHours);
                    }

                    // Encrypt the text
                    byte[] encData = Crypto.Encrypt(capturedText, contentId);
                    string encB64 = Convert.ToBase64String(encData);
                    string payload = $"{encryptionMarker}{contentId}|{encB64}";

                    // Set encrypted content
                    Clipboard.SetText(payload);

                    item.ItemType = ClipboardItemType.Text;
                    item.EncryptedData = encB64;
                    item.Preview = Crypto.CreateEncryptedPreview(capturedText);
                }

                // Save to database if we processed something
                if (item.EncryptedData != null)
                {
                    DatabaseManager.Instance.SaveClipboardItem(item);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ProcClip: {ex.Message}");
            }
            finally
            {
                // Always re-enable listener
                AddClipboardFormatListener(Handle);
                lock (clipboardLock)
                {
                    isProcessingClipboard = false;
                }
            }
        }

        //// ---------- FILES -------------------------------------------
        //private void ProcessFileContent(ClipboardItem item, string cid)
        //{
        //    try
        //    {
        //        var files = Clipboard.GetFileDropList();
        //        string[] paths = new string[files.Count]; files.CopyTo(paths, 0);

        //        RemoveClipboardFormatListener(Handle);

        //        string enc = Crypto.EncryptFilePaths(paths, cid);
        //        Clipboard.Clear(); Thread.Sleep(10);
        //        Clipboard.SetText($"{encryptionMarker}{cid}|{enc}");

        //        item.ItemType = ClipboardItemType.File;
        //        item.EncryptedData = enc;
        //        item.Preview = $"[{paths.Length} file(s)]";
        //        item.IsSensitive = false;

        //        // NEW: Set retention for files
        //        item.ExpiresAt = DateTime.Now.AddHours(normalDataRetentionHours);
        //    }
        //    catch (Exception ex) { Debug.WriteLine($"FileProc: {ex.Message}"); }
        //    finally { AddClipboardFormatListener(Handle); }
        //}

        //// ---------- IMAGE -------------------------------------------
        //private void ProcessImageContent(ClipboardItem item, string cid)
        //{
        //    try
        //    {
        //        Image img = Clipboard.GetImage();
        //        RemoveClipboardFormatListener(Handle);

        //        string enc = Crypto.EncryptImage(img, cid);
        //        item.ItemType = ClipboardItemType.Image;
        //        item.EncryptedData = enc;
        //        item.Preview = "[Encrypted Image]";
        //        item.IsSensitive = false;

        //        // NEW: Set retention for images
        //        item.ExpiresAt = DateTime.Now.AddHours(normalDataRetentionHours);

        //        using Bitmap ph = new(200, 100);
        //        using Graphics g = Graphics.FromImage(ph);
        //        g.FillRectangle(Brushes.LightGray, 0, 0, 200, 100);
        //        g.DrawString("Encrypted Image", new Font("Arial", 10), Brushes.Black, 10, 40);

        //        Clipboard.Clear(); Thread.Sleep(10);
        //        Clipboard.SetImage(ph);
        //    }
        //    catch (Exception ex) { Debug.WriteLine($"ImgProc: {ex.Message}"); }
        //    finally { AddClipboardFormatListener(Handle); }
        //}

        //// ---------- TEXT -------------------------------------------
        //private void ProcessTextContent(ClipboardItem item, string cid)
        //{
        //    try
        //    {
        //        string txt = Clipboard.GetText();
        //        if (detectSensitiveData)
        //        {
        //            item.IsSensitive = IsSensitiveData(txt);
        //            if (item.IsSensitive)
        //            {
        //                // NEW: Use configurable retention for sensitive data
        //                item.ExpiresAt = DateTime.Now.AddHours(sensitiveDataRetentionHours);
        //                ShowSensitiveDataNotification(txt);
        //            }
        //            else
        //            {
        //                // NEW: Use normal retention for non-sensitive data
        //                item.ExpiresAt = DateTime.Now.AddHours(normalDataRetentionHours);
        //            }
        //        }
        //        else
        //        {
        //            item.ExpiresAt = DateTime.Now.AddHours(normalDataRetentionHours);
        //        }

        //        RemoveClipboardFormatListener(Handle);

        //        byte[] encData = Crypto.Encrypt(txt, cid);
        //        string encB64 = Convert.ToBase64String(encData);
        //        string payload = $"{encryptionMarker}{cid}|{encB64}";

        //        Clipboard.Clear(); Thread.Sleep(50);
        //        Clipboard.SetText(payload);

        //        item.ItemType = ClipboardItemType.Text;
        //        item.EncryptedData = encB64;
        //        item.Preview = Crypto.CreateEncryptedPreview(txt);
        //    }
        //    catch (Exception ex) { Debug.WriteLine($"TxtProc: {ex.Message}"); }
        //    finally { AddClipboardFormatListener(Handle); }
        //}

        // =============================================================
        //  Helpers
        // =============================================================
        private bool IsSensitiveData(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            if (Regex.IsMatch(t, @"(?i)pass(word)?|adm(inistrator)?")) return true;
            if (Regex.IsMatch(t, @"[a-zA-Z0-9_\-]{20,}")) return true;
            if (Regex.IsMatch(t, @"^((25[0-5]|2[0-4]\d|1\d{2}|[1-9]?\d)\.){3}" +
                   @"(25[0-5]|2[0-4]\d|1\d{2}|[1-9]?\d)$")) return true;
            if (Regex.IsMatch(t, @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}")) return true;

            return false;
        }

        // NEW: Enhanced notification with duplicate prevention
        private void ShowSensitiveDataNotification(string dataContent = null)
        {
            if (!enableSensitiveNotifications) return;

            // Prevent duplicate notifications
            if (dataContent != null)
            {
                string hash = ComputeHash(dataContent);
                if (notifiedSensitiveData.Contains(hash))
                    return;

                notifiedSensitiveData.Add(hash);

                // Clear old hashes after 1 hour
                _ = Task.Delay(TimeSpan.FromHours(1)).ContinueWith(_ =>
                {
                    lock (notifiedSensitiveData)
                    {
                        notifiedSensitiveData.Remove(hash);
                    }
                });
            }

            // Non-intrusive notification with shorter duration
            trayIcon.BalloonTipIcon = ToolTipIcon.Info;
            trayIcon.BalloonTipTitle = "Protected";
            trayIcon.BalloonTipText = $"Sensitive data encrypted (expires in {sensitiveDataRetentionHours}h)";
            trayIcon.ShowBalloonTip(2000); // Reduced from 5000ms to 2000ms
        }

        // NEW: Hash computation for duplicate detection
        private string ComputeHash(string input)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
                return Convert.ToBase64String(bytes);
            }
        }

        // =============================================================
        //  PASTE – decrypt on Ctrl-V
        // =============================================================
        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN && sessionActive)
            {
                int vk = Marshal.ReadInt32(lParam);
                bool ctrl = (Control.ModifierKeys & Keys.Control) == Keys.Control;
                if (ctrl && vk == (int)Keys.V)
                {
                    lastActivity = DateTime.Now;
                    this.Invoke((Action)(() =>
                    {
                        try
                        {
                            if (Clipboard.ContainsText()) HandleDecryptedTextPaste();
                            else if (Clipboard.ContainsImage()) HandleDecryptedImagePaste();
                        }
                        catch (Exception ex) { Debug.WriteLine($"PasteHook: {ex.Message}"); }
                    }));
                }
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private void HandleDecryptedTextPaste()
        {
            string txt = Clipboard.GetText();
            if (!txt.StartsWith(encryptionMarker)) return;

            string payload = txt.Substring(encryptionMarker.Length);
            int sep = payload.IndexOf('|');
            if (sep < 0) return;

            string cid = payload[..sep];
            string enc = payload[(sep + 1)..];

            var related = FindItemByContentId(cid);
            if (related != null && related.ItemType == ClipboardItemType.File)
                ProcessFilePaste(cid, enc);
            else ProcessTextPaste(cid, enc);
        }

        private void ProcessTextPaste(string cid, string encB64)
        {
            try
            {
                string plain = Crypto.Decrypt(Convert.FromBase64String(encB64), cid);

                lastEncryptedContent = $"{encryptionMarker}{cid}|{encB64}";
                RemoveClipboardFormatListener(Handle);
                suppressClipboardEncryption = true;

                Clipboard.Clear(); Thread.Sleep(10);
                ClipboardHelper.SetTextNoHistory(plain);   // **** no-history API ****

                System.Threading.Timer t = new(_ => {
                    RestoreEncryptedContent(lastEncryptedContent);
                }, null, decryptionTimeout, Timeout.Infinite);
            }
            catch (Exception ex) { Debug.WriteLine($"TxtPaste: {ex.Message}"); }
            finally { AddClipboardFormatListener(Handle); }
        }

        private void ProcessFilePaste(string cid, string encB64)
        {
            try
            {
                string[] paths = Crypto.DecryptFilePaths(encB64, cid);

                lastEncryptedContent = $"{encryptionMarker}{cid}|{encB64}";
                RemoveClipboardFormatListener(Handle);
                suppressClipboardEncryption = true;

                Clipboard.Clear(); Thread.Sleep(10);
                StringCollection sc = new(); sc.AddRange(paths);
                Clipboard.SetFileDropList(sc);

                System.Threading.Timer t = new(_ => {
                    RestoreEncryptedContent(lastEncryptedContent);
                }, null, decryptionTimeout, Timeout.Infinite);
            }
            catch (Exception ex) { Debug.WriteLine($"FilePaste: {ex.Message}"); }
            finally { AddClipboardFormatListener(Handle); }
        }

        private void HandleDecryptedImagePaste()
        {
            // Locate most recent encrypted image & restore; code similar
            try
            {
                var imgItem = DatabaseManager.Instance.GetRecentItems(20)
                               .FirstOrDefault(i => i.ItemType == ClipboardItemType.Image);
                if (imgItem == null) return;

                RemoveClipboardFormatListener(Handle);
                suppressClipboardEncryption = true;

                Image img = Crypto.DecryptImage(imgItem.EncryptedData, imgItem.ContentId);
                Clipboard.Clear(); Thread.Sleep(10);
                Clipboard.SetImage(img);

                System.Threading.Timer t = new(_ => {
                    RestoreImagePlaceholder();
                }, null, decryptionTimeout, Timeout.Infinite);
            }
            catch (Exception ex) { Debug.WriteLine($"ImgPaste: {ex.Message}"); }
            finally { AddClipboardFormatListener(Handle); }
        }

        private void RestoreEncryptedContent(string content)
        {
            this.Invoke((Action)(() =>
            {
                try
                {
                    RemoveClipboardFormatListener(Handle);
                    Clipboard.Clear(); Thread.Sleep(10);
                    Clipboard.SetText(content);
                }
                finally
                {
                    suppressClipboardEncryption = false;
                    AddClipboardFormatListener(Handle);
                }
            }));
        }

        private void RestoreImagePlaceholder()
        {
            this.Invoke((Action)(() =>
            {
                try
                {
                    RemoveClipboardFormatListener(Handle);
                    Clipboard.Clear(); Thread.Sleep(10);

                    using Bitmap ph = new(200, 100);
                    using Graphics g = Graphics.FromImage(ph);
                    g.FillRectangle(Brushes.White, 0, 0, 200, 100);
                    g.DrawString("Encrypted Image", new Font("Arial", 10),
                                 Brushes.Black, 10, 40);
                    Clipboard.SetImage(ph);
                }
                finally
                {
                    suppressClipboardEncryption = false;
                    AddClipboardFormatListener(Handle);
                }
            }));
        }

        private ClipboardItem FindItemByContentId(string cid)
        {
            try
            {
                return DatabaseManager.Instance.GetRecentItems(50)
                       .FirstOrDefault(i => i.ContentId == cid);
            }
            catch { return null; }
        }

        // =============================================================
        //  Shutdown
        // =============================================================
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing &&
                bool.Parse(DatabaseManager.Instance.GetSetting("MinimizeToTray", "true")))
            {
                e.Cancel = true; Hide();
                trayIcon.ShowBalloonTip(3000, appName,
                    "Still running in system tray.", ToolTipIcon.Info);
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
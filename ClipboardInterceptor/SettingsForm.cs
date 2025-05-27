using System;
using System.Windows.Forms;
using System.Drawing;

namespace ClipboardInterceptor
{
    public partial class SettingsForm : Form
    {
        public SettingsForm()
        {
            InitializeComponent();
            LoadSettings();
        }

        private void InitializeComponent()
        {
            this.Text = "ClipboardInterceptor Settings";
            this.Size = new Size(450, 350);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            TabControl tabControl = new TabControl
            {
                Dock = DockStyle.Fill
            };

            // General Settings Tab
            TabPage generalTab = new TabPage("General");
            generalTab.Padding = new Padding(10);

            var generalPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                AutoScroll = true,
                WrapContents = false
            };

            var startupCheckbox = new CheckBox
            {
                Text = "Start with Windows",
                Name = "checkBoxStartup",
                AutoSize = true,
                Margin = new Padding(0, 5, 0, 10)
            };

            var minimizeToTrayCheckbox = new CheckBox
            {
                Text = "Minimize to tray on close",
                Name = "checkBoxMinimizeTray",
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 10)
            };

            var autostartProtectionCheckbox = new CheckBox
            {
                Text = "Start protection automatically",
                Name = "checkBoxAutoProtect",
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 10)
            };

            // Security Settings Section
            var securityLabel = new Label
            {
                Text = "Security Settings",
                Font = new Font(Font.FontFamily, 10, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 10, 0, 10)
            };

            // Change PIN button
            var changePinButton = new Button
            {
                Text = "Change Security PIN",
                Size = new Size(150, 30),
                Margin = new Padding(0, 10, 0, 10)
            };

            changePinButton.Click += (s, e) => ChangePIN();

            var autoLockCheckbox = new CheckBox
            {
                Text = "Auto-lock history after inactive period",
                Name = "checkBoxAutoLock",
                AutoSize = true,
                Margin = new Padding(0, 10, 0, 5)
            };

            var autoLockMinutesLabel = new Label
            {
                Text = "Auto-lock after (minutes):",
                AutoSize = true,
                Margin = new Padding(20, 0, 0, 5)
            };

            var autoLockMinutesNumeric = new NumericUpDown
            {
                Name = "numericAutoLockMinutes",
                Minimum = 1,
                Maximum = 60,
                Value = 5,
                Width = 80,
                Margin = new Padding(20, 0, 0, 10)
            };

            var sensitiveDataDetectionCheckbox = new CheckBox
            {
                Text = "Detect sensitive data patterns",
                Name = "checkBoxSensitiveData",
                AutoSize = true,
                Margin = new Padding(0, 10, 0, 10)
            };

            // History Settings Section
            var historyLabel = new Label
            {
                Text = "History Settings",
                Font = new Font(Font.FontFamily, 10, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 10, 0, 10)
            };

            var encryptionMarkerLabel = new Label
            {
                Text = "Custom encryption marker:",
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 5)
            };

            var encryptionMarkerTextBox = new TextBox
            {
                Name = "textBoxEncryptionMarker",
                Width = 300,
                Margin = new Padding(0, 0, 0, 5)
            };

            var encryptionMarkerNote = new Label
            {
                Text = "Note: Changing the marker requires restarting the application",
                Font = new Font(Font.FontFamily, 8, FontStyle.Italic),
                ForeColor = Color.DarkRed,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 10)
            };

            // Button panel at bottom
            var buttonPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 50
            };

            var saveButton = new Button
            {
                Text = "Save",
                DialogResult = DialogResult.OK,
                Location = new Point(this.Width - 180, 10),
                Size = new Size(80, 30)
            };

            var cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(this.Width - 90, 10),
                Size = new Size(80, 30)
            };

            // Event handlers
            autoLockCheckbox.CheckedChanged += (s, e) =>
            {
                autoLockMinutesLabel.Enabled = autoLockCheckbox.Checked;
                autoLockMinutesNumeric.Enabled = autoLockCheckbox.Checked;
            };

            saveButton.Click += (s, e) => SaveSettings();

            // Add controls to panels
            generalPanel.Controls.Add(startupCheckbox);
            generalPanel.Controls.Add(minimizeToTrayCheckbox);
            generalPanel.Controls.Add(autostartProtectionCheckbox);
            generalPanel.Controls.Add(securityLabel);
            generalPanel.Controls.Add(changePinButton);
            generalPanel.Controls.Add(autoLockCheckbox);
            generalPanel.Controls.Add(autoLockMinutesLabel);
            generalPanel.Controls.Add(autoLockMinutesNumeric);
            generalPanel.Controls.Add(sensitiveDataDetectionCheckbox);
            generalPanel.Controls.Add(historyLabel);
            generalPanel.Controls.Add(encryptionMarkerLabel);
            generalPanel.Controls.Add(encryptionMarkerTextBox);
            generalPanel.Controls.Add(encryptionMarkerNote);

            generalTab.Controls.Add(generalPanel);

            buttonPanel.Controls.Add(saveButton);
            buttonPanel.Controls.Add(cancelButton);

            tabControl.Controls.Add(generalTab);

            this.Controls.Add(tabControl);
            this.Controls.Add(buttonPanel);

            this.AcceptButton = saveButton;
            this.CancelButton = cancelButton;
        }

        private void LoadSettings()
        {
            var db = DatabaseManager.Instance;

            // General settings
            ((CheckBox)Controls.Find("checkBoxStartup", true)[0]).Checked =
                bool.Parse(db.GetSetting("StartWithWindows", "false"));

            ((CheckBox)Controls.Find("checkBoxMinimizeTray", true)[0]).Checked =
                bool.Parse(db.GetSetting("MinimizeToTray", "true"));

            ((CheckBox)Controls.Find("checkBoxAutoProtect", true)[0]).Checked =
                bool.Parse(db.GetSetting("AutoStartProtection", "false"));

            // Security settings
            ((CheckBox)Controls.Find("checkBoxAutoLock", true)[0]).Checked =
                bool.Parse(db.GetSetting("AutoLock", "true"));

            ((NumericUpDown)Controls.Find("numericAutoLockMinutes", true)[0]).Value =
                int.Parse(db.GetSetting("AutoLockMinutes", "5"));

            ((CheckBox)Controls.Find("checkBoxSensitiveData", true)[0]).Checked =
                bool.Parse(db.GetSetting("DetectSensitiveData", "true"));

            ((TextBox)Controls.Find("textBoxEncryptionMarker", true)[0]).Text =
                db.GetSetting("EncryptionMarker", "ENC:");

            // Enable/disable dependent controls
            bool autoLock = ((CheckBox)Controls.Find("checkBoxAutoLock", true)[0]).Checked;
            Control[] lockLabels = Controls.Find("autoLockMinutesLabel", true);
            if (lockLabels.Length > 0)
                lockLabels[0].Enabled = autoLock;

            Control[] lockNumerics = Controls.Find("numericAutoLockMinutes", true);
            if (lockNumerics.Length > 0)
                lockNumerics[0].Enabled = autoLock;
        }

        private void SaveSettings()
        {
            var db = DatabaseManager.Instance;

            // General settings
            db.SaveSetting("StartWithWindows",
                ((CheckBox)Controls.Find("checkBoxStartup", true)[0]).Checked.ToString());

            db.SaveSetting("MinimizeToTray",
                ((CheckBox)Controls.Find("checkBoxMinimizeTray", true)[0]).Checked.ToString());

            db.SaveSetting("AutoStartProtection",
                ((CheckBox)Controls.Find("checkBoxAutoProtect", true)[0]).Checked.ToString());

            // Security settings
            db.SaveSetting("AutoLock",
                ((CheckBox)Controls.Find("checkBoxAutoLock", true)[0]).Checked.ToString());

            db.SaveSetting("AutoLockMinutes",
                ((NumericUpDown)Controls.Find("numericAutoLockMinutes", true)[0]).Value.ToString());

            db.SaveSetting("DetectSensitiveData",
                ((CheckBox)Controls.Find("checkBoxSensitiveData", true)[0]).Checked.ToString());

            // Only save encryption marker if changed, since it requires restart
            string newMarker = ((TextBox)Controls.Find("textBoxEncryptionMarker", true)[0]).Text;
            string currentMarker = db.GetSetting("EncryptionMarker", "ENC:");

            if (newMarker != currentMarker && !string.IsNullOrEmpty(newMarker))
            {
                db.SaveSetting("EncryptionMarker", newMarker);
                MessageBox.Show(
                    "Encryption marker has been changed. The application will need to restart for changes to take effect.",
                    "Restart Required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            // Set startup with Windows if enabled
            SetStartupWithWindows(((CheckBox)Controls.Find("checkBoxStartup", true)[0]).Checked);
        }

        private void ChangePIN()
        {
            using (var pinForm = new PinForm(true))
            {
                if (pinForm.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        Crypto.SetPIN(pinForm.PIN);
                        MessageBox.Show(
                            "Security PIN changed successfully.",
                            "PIN Changed",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            $"Error changing PIN: {ex.Message}",
                            "Error",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void SetStartupWithWindows(bool enable)
        {
            try
            {
                // Use Registry approach for startup
                Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);

                if (enable)
                {
                    string appPath = Application.ExecutablePath;
                    key.SetValue("ClipboardInterceptor", appPath);
                }
                else
                {
                    key.DeleteValue("ClipboardInterceptor", false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Could not set startup with Windows: {ex.Message}",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }
}
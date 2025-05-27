using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.Linq;
using System.Collections.Specialized;

namespace ClipboardInterceptor
{
    public partial class HistoryViewerForm : Form
    {
        private readonly List<ClipboardItem> clipboardItems;
        private ClipboardItem selectedItem;
        private bool isAuthenticated = false;

        public HistoryViewerForm()
        {
            InitializeComponent();

            // Load clipboard items
            clipboardItems = DatabaseManager.Instance.GetRecentItems(100);
            UpdateListView();

            // Register shortcut for quick copy (Ctrl+C when item selected)
            KeyPreview = true;
            KeyDown += HistoryViewerForm_KeyDown;
        }

        private void InitializeComponent()
        {
            this.Text = "Clipboard History";
            this.Size = new Size(800, 600);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Icon = SystemIcons.Application;

            // Main layout - split into list and detail panels
            var splitContainer = new SplitContainer
            {
                Dock = DockStyle.Fill,
                SplitterDistance = 350,
                Orientation = Orientation.Vertical
            };

            // List panel
            var listView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Name = "listViewHistory"
            };
            listView.Columns.Add("Time", 120);
            listView.Columns.Add("Type", 80);
            listView.Columns.Add("Preview", 400);
            listView.Columns.Add("Sensitive", 80);

            // Detail panel
            var previewPanel = new Panel
            {
                Dock = DockStyle.Fill
            };

            var previewLabel = new Label
            {
                Text = "Select an item to view details",
                Dock = DockStyle.Top,
                Font = new Font(Font.FontFamily, 10, FontStyle.Bold),
                Height = 30
            };

            var previewContent = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                Name = "textBoxPreview"
            };

            var imagePreview = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                Visible = false,
                Name = "pictureBoxPreview"
            };

            var buttonPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 40
            };

            var copyButton = new Button
            {
                Text = "Copy to Clipboard",
                Location = new Point(10, 5),
                Size = new Size(150, 30)
            };

            var decryptButton = new Button
            {
                Text = "Authenticate & View",
                Location = new Point(170, 5),
                Size = new Size(150, 30)
            };

            var deleteButton = new Button
            {
                Text = "Delete Item",
                Location = new Point(330, 5),
                Size = new Size(150, 30)
            };

            // Event handlers
            listView.ItemSelectionChanged += (s, e) =>
            {
                if (listView.SelectedItems.Count > 0)
                {
                    int index = listView.SelectedItems[0].Index;
                    selectedItem = clipboardItems[index];
                    UpdatePreview();
                }
                else
                {
                    selectedItem = null;
                    previewContent.Text = "";
                    imagePreview.Image = null;
                    imagePreview.Visible = false;
                    previewContent.Visible = true;
                }
            };

            copyButton.Click += (s, e) => CopySelectedItem();
            decryptButton.Click += (s, e) => AuthenticateAndView();
            deleteButton.Click += (s, e) => DeleteSelectedItem();

            // Add controls to form
            buttonPanel.Controls.Add(copyButton);
            buttonPanel.Controls.Add(decryptButton);
            buttonPanel.Controls.Add(deleteButton);

            previewPanel.Controls.Add(previewContent);
            previewPanel.Controls.Add(imagePreview);
            previewPanel.Controls.Add(previewLabel);
            previewPanel.Controls.Add(buttonPanel);

            splitContainer.Panel1.Controls.Add(listView);
            splitContainer.Panel2.Controls.Add(previewPanel);

            this.Controls.Add(splitContainer);

            // Add main menu
            var mainMenu = new MenuStrip();
            var fileMenu = new ToolStripMenuItem("File");
            var refreshMenuItem = new ToolStripMenuItem("Refresh", null, (s, e) => RefreshItems());
            var clearAllMenuItem = new ToolStripMenuItem("Clear All History", null, (s, e) => ClearAllHistory());
            var closeMenuItem = new ToolStripMenuItem("Close", null, (s, e) => Close());

            fileMenu.DropDownItems.Add(refreshMenuItem);
            fileMenu.DropDownItems.Add(clearAllMenuItem);
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(closeMenuItem);

            mainMenu.Items.Add(fileMenu);
            this.MainMenuStrip = mainMenu;
            this.Controls.Add(mainMenu);
        }

        private void HistoryViewerForm_KeyDown(object sender, KeyEventArgs e)
        {
            // Quick copy with Ctrl+C
            if (e.Control && e.KeyCode == Keys.C && selectedItem != null)
            {
                CopySelectedItem();
                e.Handled = true;
            }
        }

        private void UpdateListView()
        {
            var listView = Controls[0].Controls[0].Controls[0] as ListView;
            listView.Items.Clear();

            foreach (var item in clipboardItems)
            {
                var typeText = item.ItemType.ToString();
                string previewText = item.Preview;

                ListViewItem lvi = new ListViewItem(new[]
                {
                    item.Timestamp.ToString("g"),
                    typeText,
                    previewText,
                    item.IsSensitive ? "Yes" : "No"
                });

                // Add icons based on type
                listView.Items.Add(lvi);
            }
        }

        private void UpdatePreview()
        {
            if (selectedItem == null)
                return;

            var previewContent = Controls.Find("textBoxPreview", true)[0] as TextBox;
            var imagePreview = Controls.Find("pictureBoxPreview", true)[0] as PictureBox;

            // Show placeholder text
            if (!isAuthenticated)
            {
                previewContent.Text = "Encrypted content. Authenticate to view.";
                previewContent.Visible = true;
                imagePreview.Visible = false;
                return;
            }

            try
            {
                switch (selectedItem.ItemType)
                {
                    case ClipboardItemType.Text:
                        byte[] encryptedData = Convert.FromBase64String(selectedItem.EncryptedData);
                        string decrypted = Crypto.Decrypt(encryptedData, selectedItem.ContentId);

                        previewContent.Text = decrypted;
                        previewContent.Visible = true;
                        imagePreview.Visible = false;
                        break;

                    case ClipboardItemType.Image:
                        // Dispose previous image to free memory
                        if (imagePreview.Image != null)
                        {
                            imagePreview.Image.Dispose();
                        }

                        imagePreview.Image = Crypto.DecryptImage(selectedItem.EncryptedData, selectedItem.ContentId);
                        imagePreview.Visible = true;
                        previewContent.Visible = false;
                        break;

                    case ClipboardItemType.File:
                        string[] filePaths = Crypto.DecryptFilePaths(selectedItem.EncryptedData, selectedItem.ContentId);
                        previewContent.Text = "Files:\r\n" + string.Join("\r\n", filePaths);
                        previewContent.Visible = true;
                        imagePreview.Visible = false;
                        break;

                    default:
                        previewContent.Text = "Unsupported content type";
                        previewContent.Visible = true;
                        imagePreview.Visible = false;
                        break;
                }
            }
            catch (Exception ex)
            {
                previewContent.Text = $"Error decrypting content: {ex.Message}";
                previewContent.Visible = true;
                imagePreview.Visible = false;
            }
        }

        private void AuthenticateAndView()
        {
            if (isAuthenticated)
            {
                // Already authenticated
                UpdatePreview();
                return;
            }

            // Check for lockout
            var (isLocked, remainingMinutes) = Crypto.GetLockoutStatus();
            if (isLocked)
            {
                MessageBox.Show(
                    $"Too many failed PIN attempts. Please try again in {remainingMinutes} minutes.",
                    "Authentication Locked",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            // Show PIN entry dialog
            using (var pinForm = new PinForm())
            {
                if (pinForm.ShowDialog() == DialogResult.OK)
                {
                    string pin = pinForm.PIN;

                    if (Crypto.VerifyPIN(pin))
                    {
                        isAuthenticated = true;
                        UpdatePreview();
                    }
                    else
                    {
                        MessageBox.Show(
                            "Incorrect PIN. Please try again.",
                            "Authentication Failed",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void CopySelectedItem()
        {
            if (selectedItem == null)
                return;

            if (!isAuthenticated)
            {
                DialogResult result = MessageBox.Show(
                    "You need to authenticate first to copy this item. Authenticate now?",
                    "Authentication Required",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    AuthenticateAndView();
                    if (!isAuthenticated) // If authentication failed, return
                        return;
                }
                else
                {
                    return;
                }
            }

            try
            {
                switch (selectedItem.ItemType)
                {
                    case ClipboardItemType.Text:
                        byte[] encryptedData = Convert.FromBase64String(selectedItem.EncryptedData);
                        string decrypted = Crypto.Decrypt(encryptedData, selectedItem.ContentId);
                        Clipboard.SetText(decrypted);
                        break;

                    case ClipboardItemType.Image:
                        Image img = Crypto.DecryptImage(selectedItem.EncryptedData, selectedItem.ContentId);
                        Clipboard.SetImage(img);
                        // Don't dispose img here since it's now owned by clipboard
                        break;

                    case ClipboardItemType.File:
                        string[] filePaths = Crypto.DecryptFilePaths(selectedItem.EncryptedData, selectedItem.ContentId);
                        var paths = new StringCollection();
                        paths.AddRange(filePaths);
                        Clipboard.SetFileDropList(paths);
                        break;
                }

                MessageBox.Show(
                    "Item copied to clipboard successfully.",
                    "Success",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error copying item: {ex.Message}",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void DeleteSelectedItem()
        {
            if (selectedItem == null)
                return;

            DialogResult result = MessageBox.Show(
                "Are you sure you want to delete this item?",
                "Confirm Delete",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                DatabaseManager.Instance.DeleteItem(selectedItem.Id);
                clipboardItems.Remove(selectedItem);
                selectedItem = null;
                UpdateListView();

                var previewContent = Controls.Find("textBoxPreview", true)[0] as TextBox;
                var imagePreview = Controls.Find("pictureBoxPreview", true)[0] as PictureBox;

                previewContent.Text = "Select an item to view details";
                previewContent.Visible = true;

                // Dispose image to free memory
                if (imagePreview.Image != null)
                {
                    imagePreview.Image.Dispose();
                    imagePreview.Image = null;
                }
                imagePreview.Visible = false;
            }
        }

        private void RefreshItems()
        {
            clipboardItems.Clear();
            clipboardItems.AddRange(DatabaseManager.Instance.GetRecentItems(100));
            UpdateListView();

            // Clear selection and preview
            selectedItem = null;
            var previewContent = Controls.Find("textBoxPreview", true)[0] as TextBox;
            var imagePreview = Controls.Find("pictureBoxPreview", true)[0] as PictureBox;

            previewContent.Text = "Select an item to view details";
            previewContent.Visible = true;

            if (imagePreview.Image != null)
            {
                imagePreview.Image.Dispose();
                imagePreview.Image = null;
            }
            imagePreview.Visible = false;
        }

        private void ClearAllHistory()
        {
            DialogResult result = MessageBox.Show(
                "Are you sure you want to clear all clipboard history? This cannot be undone.",
                "Confirm Clear All",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                DatabaseManager.Instance.DeleteAllItems();
                clipboardItems.Clear();
                UpdateListView();

                var previewContent = Controls.Find("textBoxPreview", true)[0] as TextBox;
                var imagePreview = Controls.Find("pictureBoxPreview", true)[0] as PictureBox;

                previewContent.Text = "No items in history";
                previewContent.Visible = true;

                if (imagePreview.Image != null)
                {
                    imagePreview.Image.Dispose();
                    imagePreview.Image = null;
                }
                imagePreview.Visible = false;

                selectedItem = null;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Clean up any sensitive data in memory
            isAuthenticated = false;
            selectedItem = null;

            var previewContent = Controls.Find("textBoxPreview", true)[0] as TextBox;
            if (previewContent != null)
                previewContent.Text = "";

            var imagePreview = Controls.Find("pictureBoxPreview", true)[0] as PictureBox;
            if (imagePreview?.Image != null)
            {
                imagePreview.Image.Dispose();
                imagePreview.Image = null;
            }

            base.OnFormClosing(e);
        }
    }
}
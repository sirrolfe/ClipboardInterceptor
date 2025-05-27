using System;
using System.Windows.Forms;
using System.Drawing;

namespace ClipboardInterceptor
{
    public partial class PinForm : Form
    {
        private TextBox pinTextBox;
        private TextBox confirmPinTextBox;
        private Label promptLabel;
        private Label confirmLabel;

        public string PIN { get; private set; }

        public PinForm(bool isChangingPin = false)
        {
            InitializeComponent(isChangingPin);
        }

        private void InitializeComponent(bool isChangingPin)
        {
            Text = isChangingPin ? "Change Security PIN" : "Enter Security PIN";
            Size = new Size(300, isChangingPin ? 200 : 150);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            promptLabel = new Label
            {
                Text = isChangingPin ? "Enter a new 4-digit PIN:" : "Enter your 4-digit PIN:",
                Location = new Point(20, 20),
                Size = new Size(260, 20)
            };

            pinTextBox = new TextBox
            {
                Location = new Point(20, 50),
                Size = new Size(260, 20),
                PasswordChar = '*',
                MaxLength = 4
            };

            if (isChangingPin)
            {
                confirmLabel = new Label
                {
                    Text = "Confirm your new PIN:",
                    Location = new Point(20, 80),
                    Size = new Size(260, 20)
                };

                confirmPinTextBox = new TextBox
                {
                    Location = new Point(20, 110),
                    Size = new Size(260, 20),
                    PasswordChar = '*',
                    MaxLength = 4
                };
            }

            var okButton = new Button
            {
                Text = "OK",
                Location = new Point(100, isChangingPin ? 150 : 80),
                Size = new Size(75, 23),
                DialogResult = DialogResult.OK
            };

            var cancelButton = new Button
            {
                Text = "Cancel",
                Location = new Point(190, isChangingPin ? 150 : 80),
                Size = new Size(75, 23),
                DialogResult = DialogResult.Cancel
            };

            okButton.Click += (s, e) =>
            {
                if (isChangingPin)
                {
                    PIN = pinTextBox.Text;
                    string confirmPin = confirmPinTextBox.Text;

                    if (PIN.Length != 4 || !int.TryParse(PIN, out _))
                    {
                        MessageBox.Show("Please enter a valid 4-digit PIN", "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        DialogResult = DialogResult.None;
                        return;
                    }

                    if (PIN != confirmPin)
                    {
                        MessageBox.Show("PINs do not match. Please try again.", "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        DialogResult = DialogResult.None;
                        return;
                    }
                }
                else
                {
                    PIN = pinTextBox.Text;
                    if (PIN.Length != 4 || !int.TryParse(PIN, out _))
                    {
                        MessageBox.Show("Please enter a valid 4-digit PIN", "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        DialogResult = DialogResult.None;
                    }
                }
            };

            Controls.Add(promptLabel);
            Controls.Add(pinTextBox);

            if (isChangingPin)
            {
                Controls.Add(confirmLabel);
                Controls.Add(confirmPinTextBox);
            }

            Controls.Add(okButton);
            Controls.Add(cancelButton);

            AcceptButton = okButton;
            CancelButton = cancelButton;
        }
    }
}
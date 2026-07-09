using System;
using System.Drawing;
using System.Windows.Forms;

namespace OAuthSample
{
    /// <summary>
    /// Opening screen: pick which implementation of the same OAuth login to run —
    /// the hand-rolled <see cref="MainForm"/> or the library-based
    /// <see cref="OidcClientForm"/>. Each is shown modally; closing it returns here.
    /// </summary>
    public sealed class LauncherForm : Form
    {
        private const int Square = 200;

        public LauncherForm()
        {
            Text = "OAuth 2.0 Sample";
            ClientSize = new Size(500, 330);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9f);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3,
                Padding = new Padding(24),
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));           // heading
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Square + 16)); // buttons
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));       // spacer/hint
            Controls.Add(layout);

            var heading = new Label
            {
                Text = "Choose an implementation",
                AutoSize = true,
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                Margin = new Padding(3, 0, 3, 16),
            };
            layout.Controls.Add(heading, 0, 0);
            layout.SetColumnSpan(heading, 2);

            layout.Controls.Add(MakeCard(
                "Hand-rolled",
                "No OAuth library.\nSee every step of the\nflow by hand.",
                (s, e) => Open(new MainForm())), 0, 1);

            layout.Controls.Add(MakeCard(
                "OidcClient\n(Duende)",
                "One LoginAsync() call.\nThe library does the\nheavy lifting.",
                (s, e) => Open(new OidcClientForm())), 1, 1);

            var hint = new Label
            {
                Text = "Both share the same loopback callback listener (http + in-process TLS).",
                AutoSize = true,
                MaximumSize = new Size(452, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(3, 14, 3, 3),
            };
            layout.Controls.Add(hint, 0, 2);
            layout.SetColumnSpan(hint, 2);
        }

        private void Open(Form form)
        {
            Hide();
            using (form)
                form.ShowDialog(this);
            Show();
        }

        private static Button MakeCard(string title, string subtitle, EventHandler onClick)
        {
            var btn = new Button
            {
                Text = title + "\n\n" + subtitle,
                Size = new Size(Square, Square),
                Anchor = AnchorStyles.None,
                FlatStyle = FlatStyle.System,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                UseCompatibleTextRendering = false,
            };
            btn.Click += onClick;
            return btn;
        }
    }
}

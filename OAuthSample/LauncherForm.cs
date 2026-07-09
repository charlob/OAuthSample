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
        public LauncherForm()
        {
            Text = "OAuth 2.0 Sample";
            Width = 560;
            Height = 400;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9f);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(24),
                AutoSize = false,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            AddHeading(layout, "Choose an implementation");

            AddButton(layout, "Hand-rolled  (no OAuth library)", (s, e) => Open(new MainForm()));
            AddDescription(layout, "Builds the Authorization Code + PKCE flow by hand so you can see every " +
                                   "step: the request, the callback capture, and the token exchange.");

            AddButton(layout, "OidcClient library  (Duende)", (s, e) => Open(new OidcClientForm()));
            AddDescription(layout, "The same login via Duende.IdentityModel.OidcClient — one LoginAsync() " +
                                   "call handles discovery, PKCE, state and the token exchange.");

            AddDescription(layout, "Both share the same loopback callback listener (http + in-process TLS).");
        }

        private void Open(Form form)
        {
            Hide();
            using (form)
                form.ShowDialog(this);
            Show();
        }

        private static void AddHeading(TableLayoutPanel layout, string text)
        {
            layout.Controls.Add(new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                Margin = new Padding(3, 3, 3, 14),
            });
        }

        private static void AddButton(TableLayoutPanel layout, string text, EventHandler onClick)
        {
            var btn = new Button
            {
                Text = text,
                Width = 480,
                Height = 40,
                Margin = new Padding(0, 6, 0, 2),
                Anchor = AnchorStyles.Left,
                FlatStyle = FlatStyle.System,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            };
            btn.Click += onClick;
            layout.Controls.Add(btn);
        }

        private static void AddDescription(TableLayoutPanel layout, string text)
        {
            layout.Controls.Add(new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(480, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(3, 0, 3, 10),
            });
        }
    }
}

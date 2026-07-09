using System;
using System.Drawing;
using System.IO;
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
            ClientSize = new Size(500, 400);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9f);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                Padding = new Padding(24),
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));            // logos
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));            // heading
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Square + 16)); // buttons
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));        // hint
            Controls.Add(layout);

            var logos = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, WrapContents = false, Margin = new Padding(0, 0, 0, 12) };
            logos.Controls.Add(LogoControl("emydex", "Emydex"));
            logos.Controls.Add(LogoControl("mla", "MLA"));
            layout.Controls.Add(logos, 0, 0);
            layout.SetColumnSpan(logos, 2);

            var heading = new Label
            {
                Text = "Choose an implementation",
                AutoSize = true,
                Anchor = AnchorStyles.None,
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                Margin = new Padding(3, 0, 3, 14),
            };
            layout.Controls.Add(heading, 0, 1);
            layout.SetColumnSpan(heading, 2);

            layout.Controls.Add(MakeCard(
                "Hand-rolled",
                "No OAuth library.\nSee every step of the\nflow by hand.",
                (s, e) => Open(new MainForm())), 0, 2);

            layout.Controls.Add(MakeCard(
                "OidcClient\n(Duende)",
                "One LoginAsync() call.\nThe library does the\nheavy lifting.",
                (s, e) => Open(new OidcClientForm())), 1, 2);

            var hint = new Label
            {
                Text = "Both share the same encrypted token store and loopback callback listener.",
                AutoSize = true,
                MaximumSize = new Size(452, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(3, 14, 3, 3),
            };
            layout.Controls.Add(hint, 0, 3);
            layout.SetColumnSpan(hint, 2);
        }

        private void Open(Form form)
        {
            Hide();
            using (form)
                form.ShowDialog(this);
            Show();
        }

        private static Control LogoControl(string baseName, string fallback)
        {
            string path = Assets.FindImage(baseName);
            if (path != null)
            {
                try
                {
                    // Load via a memory copy so the file isn't locked on disk.
                    var img = Image.FromStream(new MemoryStream(File.ReadAllBytes(path)));
                    int h = 46;
                    int w = Math.Min(180, (int)Math.Round((double)h * img.Width / img.Height));
                    return new PictureBox
                    {
                        Image = img,
                        SizeMode = PictureBoxSizeMode.Zoom,
                        Height = h,
                        Width = w,
                        Margin = new Padding(12, 0, 12, 0),
                    };
                }
                catch
                {
                    // fall through to a text placeholder
                }
            }

            return new Label
            {
                Text = fallback,
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                Margin = new Padding(12, 12, 12, 0),
            };
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

using System;
using System.Drawing;
using System.Windows.Forms;

namespace OAuthSample
{
    /// <summary>A separate, reusable output window for API responses (cleared per call).</summary>
    public sealed class ApiConsoleForm : Form
    {
        private readonly TextBox _out;

        public ApiConsoleForm()
        {
            Text = "API Console";
            ClientSize = new Size(760, 560);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);

            _out = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(18, 18, 18),
                ForeColor = Color.FromArgb(210, 220, 210),
                Font = new Font("Consolas", 9.5f),
            };
            Controls.Add(_out);
        }

        public void ClearConsole() => _out.Clear();

        public void Write(string text) => _out.AppendText((text ?? "") + Environment.NewLine);

        public void ShowConsole(IWin32Window owner)
        {
            if (!Visible)
                Show(owner);
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
        }
    }
}

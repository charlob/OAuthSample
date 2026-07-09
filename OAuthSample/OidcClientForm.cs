using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Duende.IdentityModel.OidcClient;

namespace OAuthSample
{
    /// <summary>
    /// The same OAuth 2.0 Authorization Code + PKCE login as <see cref="MainForm"/>, but
    /// driven by the <c>Duende.IdentityModel.OidcClient</c> library instead of hand-rolled code.
    /// The library does discovery, PKCE, the <c>state</c> check, and the token exchange;
    /// all we supply is a browser (<see cref="TlsLoopbackBrowser"/>) that can capture an
    /// https loopback callback.
    /// </summary>
    public sealed class OidcClientForm : Form
    {
        private readonly TextBox _txtAuthority;
        private readonly TextBox _txtClientId;
        private readonly TextBox _txtRedirect;
        private readonly TextBox _txtScope;
        private readonly Button _btnLogin;
        private readonly TextBox _txtLog;

        public OidcClientForm()
        {
            Text = "OAuth 2.0 Sample — via IdentityModel.OidcClient";
            Width = 900;
            Height = 620;
            MinimumSize = new Size(700, 460);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(12),
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            _txtAuthority = AddRow(layout, "Authority", "https://sso-s.mla.com.au");
            _txtClientId = AddRow(layout, "Client ID", "");
            _txtRedirect = AddRow(layout, "Callback URI", "https://localhost:5021/callback/envd/");
            _txtScope = AddRow(layout, "Scope", "openid profile email");

            _btnLogin = new Button
            {
                Text = "Login (OidcClient)",
                AutoSize = true,
                Padding = new Padding(16, 4, 16, 4),
                Anchor = AnchorStyles.Left,
            };
            _btnLogin.Click += OnLoginClick;
            layout.Controls.Add(new Label { Text = "", AutoSize = true }, 0, layout.RowCount);
            layout.Controls.Add(_btnLogin, 1, layout.RowCount);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowCount++;

            _txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(24, 24, 24),
                ForeColor = Color.FromArgb(220, 220, 220),
                Font = new Font("Consolas", 9f),
            };
            layout.Controls.Add(_txtLog, 0, layout.RowCount);
            layout.SetColumnSpan(_txtLog, 2);
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowCount++;

            Log("This form uses the OidcClient library. One LoginAsync() call does PKCE,");
            Log("state, OIDC discovery and the token exchange. We only supply a browser");
            Log("(TlsLoopbackBrowser) so it can capture MLA's https loopback callback.");
        }

        private static TextBox AddRow(TableLayoutPanel layout, string label, string value)
        {
            var lbl = new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 8, 3, 3),
            };
            var box = new TextBox { Text = value, Dock = DockStyle.Fill };
            int row = layout.RowCount;
            layout.Controls.Add(lbl, 0, row);
            layout.Controls.Add(box, 1, row);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowCount++;
            return box;
        }

        private async void OnLoginClick(object sender, EventArgs e)
        {
            _btnLogin.Enabled = false;
            try
            {
                await RunAsync();
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
            }
            finally
            {
                _btnLogin.Enabled = true;
            }
        }

        private async Task RunAsync()
        {
            string clientId = _txtClientId.Text.Trim();
            string redirectUri = _txtRedirect.Text.Trim();

            if (string.IsNullOrEmpty(clientId))
            {
                Log("Please enter a Client ID.");
                return;
            }

            var options = new OidcClientOptions
            {
                Authority = _txtAuthority.Text.Trim(),
                ClientId = clientId,
                RedirectUri = redirectUri,
                Scope = _txtScope.Text.Trim(),
                Browser = new TlsLoopbackBrowser(redirectUri, Log),
            };

            Log("");
            Log("Starting OidcClient login...");
            var client = new OidcClient(options);
            LoginResult result = await client.LoginAsync(new LoginRequest());

            if (result.IsError)
            {
                Log("Login failed: " + result.Error);
                return;
            }

            Log("");
            Log("=== Login result ===");
            Log("  access_token:  " + Truncate(result.AccessToken));
            Log("  id_token:      " + Truncate(result.IdentityToken));
            if (!string.IsNullOrEmpty(result.RefreshToken))
                Log("  refresh_token: " + Truncate(result.RefreshToken));
            Log("  expires:       " + result.AccessTokenExpiration.ToLocalTime());
            Log("  --- id_token claims ---");
            if (result.User != null)
            {
                foreach (var claim in result.User.Claims)
                    Log("  " + claim.Type + " = " + claim.Value);
            }
            Log("====================");
        }

        private static string Truncate(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "(none)";
            return value.Length > 24 ? value.Substring(0, 24) + "... (" + value.Length + " chars)" : value;
        }

        private void Log(string message)
        {
            if (_txtLog.InvokeRequired)
            {
                _txtLog.BeginInvoke(new Action<string>(Log), message);
                return;
            }
            _txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine);
        }
    }
}

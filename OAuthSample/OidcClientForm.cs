using System;
using System.Drawing;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Windows.Forms;
using Duende.IdentityModel.OidcClient;

namespace OAuthSample
{
    /// <summary>
    /// The same OAuth 2.0 Authorization Code + PKCE login as <see cref="MainForm"/>, but
    /// driven by the <c>Duende.IdentityModel.OidcClient</c> library — including its silent
    /// refresh (<c>OidcClient.RefreshTokenAsync</c>). Login/Refresh/Delete mirror MainForm
    /// and share the same encrypted <see cref="TokenStore"/>.
    /// </summary>
    public sealed class OidcClientForm : Form
    {
        private readonly TokenStore _store = new TokenStore();

        private readonly TextBox _txtAuthority;
        private readonly TextBox _txtClientId;
        private readonly TextBox _txtRedirect;
        private readonly TextBox _txtScope;
        private readonly TextBox _txtApi;
        private readonly TextBox _txtEnvd;
        private readonly Button _btnLogin;
        private readonly Button _btnRefresh;
        private readonly Button _btnDelete;
        private readonly Button _btnCallApi;
        private readonly Label _lblStatus;
        private readonly TextBox _txtLog;

        private ApiConsoleForm _console;
        private bool _busy;

        public OidcClientForm()
        {
            Text = "OAuth 2.0 Sample — via Duende.IdentityModel.OidcClient";
            Width = 900;
            Height = 640;
            MinimumSize = new Size(700, 480);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            _txtAuthority = AddRow(layout, "Authority", "https://sso-s.mla.com.au");
            _txtClientId = AddRow(layout, "Client ID", "");
            _txtRedirect = AddRow(layout, "Callback URI", "https://localhost:5021/callback/envd/");
            _txtScope = AddRow(layout, "Scope", "openid profile email offline_access");
            _txtApi = AddRow(layout, "GraphQL API", "");
            _txtEnvd = AddRow(layout, "envd Account Id", "");

            _btnLogin = MakeButton("Login (OidcClient)", OnLoginClick);
            _btnRefresh = MakeButton("Refresh token", OnRefreshClick);
            _btnDelete = MakeButton("Delete saved token", OnDeleteClick);
            _btnCallApi = MakeButton("Call test API", OnCallApiClick);

            var buttonRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            buttonRow.Controls.Add(_btnLogin);
            buttonRow.Controls.Add(_btnRefresh);
            buttonRow.Controls.Add(_btnDelete);
            buttonRow.Controls.Add(_btnCallApi);
            layout.Controls.Add(new Label { Text = "", AutoSize = true }, 0, layout.RowCount);
            layout.Controls.Add(buttonRow, 1, layout.RowCount);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowCount++;

            _lblStatus = new Label { AutoSize = true, Margin = new Padding(3, 6, 3, 6), ForeColor = SystemColors.GrayText };
            layout.Controls.Add(_lblStatus, 0, layout.RowCount);
            layout.SetColumnSpan(_lblStatus, 2);
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

            var saved = _store.Load();
            if (saved != null)
            {
                if (!string.IsNullOrEmpty(saved.Authority)) _txtAuthority.Text = saved.Authority;
                if (!string.IsNullOrEmpty(saved.ClientId)) _txtClientId.Text = saved.ClientId;
            }

            Log("This form uses the OidcClient library. LoginAsync() does discovery, PKCE,");
            Log("state and the token exchange; RefreshTokenAsync() renews without a browser.");
            UpdateButtons();
        }

        private static Button MakeButton(string text, EventHandler onClick)
        {
            var btn = new Button { Text = text, AutoSize = true, Padding = new Padding(14, 4, 14, 4), Margin = new Padding(0, 0, 8, 0), Anchor = AnchorStyles.Left };
            btn.Click += onClick;
            return btn;
        }

        private static TextBox AddRow(TableLayoutPanel layout, string label, string value)
        {
            var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) };
            var box = new TextBox { Text = value, Dock = DockStyle.Fill };
            int row = layout.RowCount;
            layout.Controls.Add(lbl, 0, row);
            layout.Controls.Add(box, 1, row);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowCount++;
            return box;
        }

        // --- button state -----------------------------------------------------------

        private void UpdateButtons()
        {
            if (InvokeRequired) { BeginInvoke(new Action(UpdateButtons)); return; }

            var rec = _store.Load();
            bool hasSession = rec != null && rec.HasRefreshToken;

            _btnLogin.Enabled = !_busy && !hasSession;
            _btnRefresh.Enabled = !_busy && hasSession;
            _btnDelete.Enabled = !_busy && rec != null;
            _btnCallApi.Enabled = !_busy && rec != null && !string.IsNullOrEmpty(rec.AccessToken);

            if (rec == null)
            {
                _lblStatus.Text = "No saved session.";
            }
            else
            {
                string expiry = rec.ExpiresAt == DateTimeOffset.MinValue ? "unknown" : rec.ExpiresAt.ToLocalTime().ToString("g");
                _lblStatus.Text = "Saved session: " + rec.UserName + "  ·  access expires " + expiry +
                    (rec.HasRefreshToken ? "  ·  refresh token stored" : "  ·  no refresh token (add offline_access)");
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            UpdateButtons();
        }

        // --- Login (interactive) ----------------------------------------------------

        private async void OnLoginClick(object sender, EventArgs e)
        {
            SetBusy(true);
            try { await LoginAsync(); }
            catch (Exception ex) { Log("ERROR: " + ex.Message); }
            finally { SetBusy(false); }
        }

        private async Task LoginAsync()
        {
            string clientId = _txtClientId.Text.Trim();
            string redirectUri = _txtRedirect.Text.Trim();

            if (string.IsNullOrEmpty(clientId)) { Log("Please enter a Client ID."); return; }

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

            var rec = new TokenRecord
            {
                Authority = options.Authority,
                ClientId = options.ClientId,
                RefreshToken = result.RefreshToken,
                AccessToken = result.AccessToken,
                IdToken = result.IdentityToken,
                UserName = NameFromClaims(result.User),
                ExpiresAtUnix = ToUnix(result.AccessTokenExpiration),
            };
            _store.Save(rec);

            LogResult(rec);
            if (!rec.HasRefreshToken)
                Log("  (No refresh token — add 'offline_access' to Scope and allow it on the IdP client.)");
        }

        // --- Refresh (silent) -------------------------------------------------------

        private async void OnRefreshClick(object sender, EventArgs e)
        {
            SetBusy(true);
            try { await RefreshAsync(); }
            catch (Exception ex) { Log("ERROR: " + ex.Message); }
            finally { SetBusy(false); }
        }

        private async Task RefreshAsync()
        {
            var rec = _store.Load();
            if (rec == null || !rec.HasRefreshToken) { Log("No saved refresh token to use."); return; }

            var options = new OidcClientOptions
            {
                Authority = rec.Authority,
                ClientId = rec.ClientId,
                Scope = _txtScope.Text.Trim(),
            };

            Log("");
            Log("Refreshing access token via OidcClient (no browser)...");
            var client = new OidcClient(options);
            var result = await client.RefreshTokenAsync(rec.RefreshToken);

            if (result.IsError)
            {
                Log("Refresh failed: " + result.Error);
                if (result.Error != null && result.Error.IndexOf("invalid_grant", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log("Refresh token is no longer valid — clearing the saved session.");
                    _store.Clear();
                }
                return;
            }

            var updated = new TokenRecord
            {
                Authority = rec.Authority,
                ClientId = rec.ClientId,
                RefreshToken = string.IsNullOrEmpty(result.RefreshToken) ? rec.RefreshToken : result.RefreshToken,
                AccessToken = result.AccessToken,
                IdToken = result.IdentityToken,
                UserName = rec.UserName,
                ExpiresAtUnix = ToUnix(result.AccessTokenExpiration),
            };
            _store.Save(updated);
            LogResult(updated);
        }

        // --- Delete -----------------------------------------------------------------

        private void OnDeleteClick(object sender, EventArgs e)
        {
            _store.Clear();
            Log("Saved session deleted.");
            UpdateButtons();
        }

        // --- Call test API ----------------------------------------------------------

        private async void OnCallApiClick(object sender, EventArgs e)
        {
            SetBusy(true);
            try { await CallApiAsync(); }
            catch (Exception ex) { Log("ERROR: " + ex.Message); }
            finally { SetBusy(false); }
        }

        private async Task CallApiAsync()
        {
            var rec = _store.Load();
            if (rec == null || string.IsNullOrEmpty(rec.AccessToken))
            {
                Log("No access token — Login or Refresh first.");
                return;
            }
            string endpoint = _txtApi.Text.Trim();
            if (string.IsNullOrEmpty(endpoint))
            {
                Log("Enter the GraphQL API URL first.");
                return;
            }

            string envdHeader = _txtEnvd.Text.Trim();

            var console = GetConsole();
            console.ShowConsole(this);
            console.ClearConsole();
            console.Write("POST " + endpoint);
            console.Write("Authorization: Bearer " + Truncate(rec.AccessToken));
            if (envdHeader.Length > 0)
                console.Write("envdAccountId: " + envdHeader);
            console.Write("query GetUserDetails");
            console.Write("");

            var result = await GraphQlClient.PostAsync(
                endpoint, rec.AccessToken, GraphQlClient.GetUserDetailsQuery,
                envdHeader.Length > 0 ? envdHeader : null);

            console.Write("HTTP " + result.Item1);
            console.Write("");
            console.Write(GraphQlClient.Pretty(result.Item2));

            string envd = GraphQlClient.ExtractEnvdAccountId(result.Item2);
            if (!string.IsNullOrEmpty(envd))
            {
                _txtEnvd.Text = envd;
                Log("envdAccountId: " + envd);
            }
            else
            {
                Log("Called API (HTTP " + result.Item1 + "). No envdAccountId found — see the API Console.");
            }
        }

        private ApiConsoleForm GetConsole()
        {
            if (_console == null || _console.IsDisposed)
                _console = new ApiConsoleForm();
            return _console;
        }

        // --- helpers ----------------------------------------------------------------

        private void LogResult(TokenRecord rec)
        {
            Log("");
            Log("=== Session ===");
            Log("  user:          " + rec.UserName);
            Log("  access_token:  " + Truncate(rec.AccessToken));
            Log("  id_token:      " + Truncate(rec.IdToken));
            Log("  refresh_token: " + (rec.HasRefreshToken ? "(stored, encrypted)" : "(none)"));
            Log("  expires:       " + (rec.ExpiresAt == DateTimeOffset.MinValue ? "unknown" : rec.ExpiresAt.ToLocalTime().ToString("g")));
            Log("===============");
        }

        private static string NameFromClaims(ClaimsPrincipal user)
        {
            if (user == null) return "(unknown user)";
            foreach (var type in new[] { "name", "preferred_username", "email", "sub" })
            {
                var c = user.FindFirst(type);
                if (c != null && !string.IsNullOrEmpty(c.Value))
                    return c.Value;
            }
            return user.Identity != null && !string.IsNullOrEmpty(user.Identity.Name) ? user.Identity.Name : "(unknown user)";
        }

        private static long ToUnix(DateTimeOffset when)
        {
            return when.Year >= 2000 ? when.ToUnixTimeSeconds() : 0;
        }

        private static string Truncate(string value)
        {
            if (string.IsNullOrEmpty(value)) return "(none)";
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

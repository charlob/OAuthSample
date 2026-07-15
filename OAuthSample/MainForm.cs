using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace OAuthSample
{
    /// <summary>
    /// Hand-rolled OAuth 2.0 Authorization Code + PKCE flow, with a persisted, silently
    /// refreshable session:
    ///   Connect  — interactive login (browser), enabled only when there's no saved session.
    ///   Refresh  — renew the access token from the stored refresh token (no browser).
    ///   Delete   — forget the saved session.
    /// The refresh token is stored encrypted (see <see cref="TokenStore"/>). Callback capture
    /// lives in <see cref="LoopbackCallbackListener"/>.
    /// </summary>
    public sealed class MainForm : Form
    {
        private static readonly HttpClient Http = new HttpClient();
        private readonly TokenStore _store = new TokenStore();

        private readonly TextBox _txtAuthority;
        private readonly TextBox _txtClientId;
        private readonly TextBox _txtRedirect;
        private readonly TextBox _txtScope;
        private readonly TextBox _txtAudience;
        private readonly Button _btnConnect;
        private readonly Button _btnRefresh;
        private readonly Button _btnDelete;
        private readonly Button _btnConsole;
        private readonly Label _lblStatus;
        private readonly TextBox _txtLog;

        private ApiConsoleForm _console;
        private bool _busy;

        public MainForm()
        {
            Text = "OAuth 2.0 Sample (Authorization Code + PKCE)";
            Width = 900;
            Height = 660;
            MinimumSize = new Size(700, 500);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(12),
                AutoSize = false,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            _txtAuthority = AddRow(layout, "Authority", "https://sso-s.mla.com.au");
            _txtClientId = AddRow(layout, "Client ID", "Ssn0VWmqXMxDrpuxWzpWHZ1qgzr4AWRG");
            _txtRedirect = AddRow(layout, "Callback URI", "https://localhost:5021/callback/envd/");
            _txtScope = AddRow(layout, "Scope", "openid profile email offline_access");
            _txtAudience = AddRow(layout, "Audience (opt.)", "https://testlpav4.nlis.com.au");

            _btnConnect = MakeButton("Connect (browser)", OnConnectClick);
            _btnRefresh = MakeButton("Refresh token", OnRefreshClick);
            _btnDelete = MakeButton("Delete saved token", OnDeleteClick);
            _btnConsole = MakeButton("API Console", OnOpenConsoleClick);

            var buttonRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            buttonRow.Controls.Add(_btnConnect);
            buttonRow.Controls.Add(_btnRefresh);
            buttonRow.Controls.Add(_btnDelete);
            buttonRow.Controls.Add(_btnConsole);
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

            // Pre-fill from a saved session so the shown client matches the stored token.
            var saved = _store.Load();
            if (saved != null)
            {
                if (!string.IsNullOrEmpty(saved.Authority)) _txtAuthority.Text = saved.Authority;
                if (!string.IsNullOrEmpty(saved.ClientId)) _txtClientId.Text = saved.ClientId;
            }

            Log("Ready. Fill in Client ID (and adjust Callback URI to match what's");
            Log("registered at the IdP), then click Connect.");
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

            _btnConnect.Enabled = !_busy && !hasSession;
            _btnRefresh.Enabled = !_busy && hasSession;
            _btnDelete.Enabled = !_busy && rec != null;
            _btnConsole.Enabled = !_busy && rec != null && !string.IsNullOrEmpty(rec.AccessToken);

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

        // --- Connect (interactive) --------------------------------------------------

        private async void OnConnectClick(object sender, EventArgs e)
        {
            SetBusy(true);
            try { await RunFlowAsync(); }
            catch (Exception ex) { Log("ERROR: " + ex.Message); }
            finally { SetBusy(false); }
        }

        private async Task RunFlowAsync()
        {
            string authority = _txtAuthority.Text.Trim().TrimEnd('/');
            string clientId = _txtClientId.Text.Trim();
            string redirectUri = _txtRedirect.Text.Trim();
            string scope = _txtScope.Text.Trim();
            string audience = _txtAudience.Text.Trim();

            if (string.IsNullOrEmpty(clientId)) { Log("Please enter a Client ID."); return; }
            if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var redirect)) { Log("Callback URI is not a valid absolute URL."); return; }

            string state = RandomUrlToken(16);
            string codeVerifier = RandomUrlToken(32);
            string codeChallenge = Base64Url(Sha256(codeVerifier));

            var authorizeUrl = new StringBuilder();
            authorizeUrl.Append(authority).Append("/authorize?");
            authorizeUrl.Append("response_type=code");
            authorizeUrl.Append("&client_id=").Append(Uri.EscapeDataString(clientId));
            authorizeUrl.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
            authorizeUrl.Append("&scope=").Append(Uri.EscapeDataString(scope));
            authorizeUrl.Append("&state=").Append(Uri.EscapeDataString(state));
            authorizeUrl.Append("&code_challenge=").Append(codeChallenge);
            authorizeUrl.Append("&code_challenge_method=S256");
            if (!string.IsNullOrEmpty(audience))
                authorizeUrl.Append("&audience=").Append(Uri.EscapeDataString(audience));

            Log("");
            Log("  authorize: " + authority + "/authorize");
            Log("  state:     " + state);
            Log("  challenge: " + codeChallenge + " (S256)");

            async Task<string> RenderResult(CallbackInfo ci)
            {
                if (ci.Error != null)
                {
                    Log("  Authorization error: " + ci.Error + " - " + ci.ErrorDescription);
                    return LandingPage.Error("Authorization failed: " + ci.Error);
                }
                if (ci.State != state)
                {
                    Log("  WARNING: state mismatch (possible CSRF). Expected " + state + " got " + ci.State);
                    return LandingPage.Error("State mismatch — aborting.");
                }
                if (string.IsNullOrEmpty(ci.Code))
                {
                    Log("  No authorization code found in callback.");
                    return LandingPage.Error("No authorization code in the callback.");
                }

                Log("");
                Log("Authorization code captured. Exchanging for tokens...");
                var record = await ExchangeCodeAsync(authority, clientId, redirectUri, ci.Code, codeVerifier);
                if (record == null)
                    return LandingPage.Error("Token exchange failed — see the app log.");

                _store.Save(record);
                if (!record.HasRefreshToken)
                    Log("  (No refresh token returned — add 'offline_access' to Scope and allow it on the IdP client.)");
                return LandingPage.Success(record.UserName, record.ExpiresAt);
            }

            var listener = new LoopbackCallbackListener(Log);
            Task<CallbackInfo> waitForCallback = listener.WaitForCallbackAsync(redirect, CancellationToken.None, RenderResult);

            Log("Opening browser for authorization...");
            LaunchBrowser(authorizeUrl.ToString());

            try { await waitForCallback; }
            catch (Exception ex) { Log("Listener error: " + ex.Message); }
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

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = rec.ClientId,
                ["refresh_token"] = rec.RefreshToken,
            });

            string tokenUrl = rec.Authority.TrimEnd('/') + "/oauth/token";
            Log("");
            Log("Refreshing access token (no browser)...");
            Log("  POST " + tokenUrl);

            HttpResponseMessage resp = await Http.PostAsync(tokenUrl, form);
            string body = await resp.Content.ReadAsStringAsync();
            Log("  HTTP " + (int)resp.StatusCode + " " + resp.StatusCode);

            if (!resp.IsSuccessStatusCode)
            {
                Log("  Refresh failed:");
                Log("  " + body);
                if (body.IndexOf("invalid_grant", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log("  Refresh token is no longer valid — clearing the saved session.");
                    _store.Clear();
                }
                return;
            }

            var updated = BuildRecord(rec.Authority, rec.ClientId, body, rec.RefreshToken, rec.UserName);
            _store.Save(updated);
            Log("  Access token refreshed. Expires " +
                (updated.ExpiresAt == DateTimeOffset.MinValue ? "unknown" : updated.ExpiresAt.ToLocalTime().ToString("g")));
        }

        // --- Delete -----------------------------------------------------------------

        private void OnDeleteClick(object sender, EventArgs e)
        {
            _store.Clear();
            Log("Saved session deleted.");
            UpdateButtons();
        }

        // --- API Console ------------------------------------------------------------

        private void OnOpenConsoleClick(object sender, EventArgs e)
        {
            var console = GetConsole();
            console.ShowConsole(this);
            Log("API Console opened. Run 'Get User Details' there to capture the envd Account Id.");
        }

        private ApiConsoleForm GetConsole()
        {
            if (_console == null || _console.IsDisposed)
                _console = new ApiConsoleForm();
            return _console;
        }

        // --- token endpoint ---------------------------------------------------------

        /// <summary>Exchanges an authorization code for tokens; returns a record to persist, or null.</summary>
        private async Task<TokenRecord> ExchangeCodeAsync(
            string authority, string clientId, string redirectUri, string code, string codeVerifier)
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = codeVerifier,
            });

            string tokenUrl = authority + "/oauth/token";
            Log("  POST " + tokenUrl);

            HttpResponseMessage resp = await Http.PostAsync(tokenUrl, form);
            string body = await resp.Content.ReadAsStringAsync();
            Log("  HTTP " + (int)resp.StatusCode + " " + resp.StatusCode);

            if (!resp.IsSuccessStatusCode)
            {
                Log("  Token endpoint returned an error:");
                Log("  " + body);
                return null;
            }

            return BuildRecord(authority, clientId, body, null, null);
        }

        /// <summary>Parses a token-endpoint JSON body, logs it, and builds a persistable record.
        /// <paramref name="existingRefresh"/>/<paramref name="existingUser"/> are kept when the
        /// response omits them (refresh responses often don't re-send the id_token or a new refresh token).</summary>
        private TokenRecord BuildRecord(string authority, string clientId, string body, string existingRefresh, string existingUser)
        {
            Dictionary<string, object> map = null;
            try { map = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body); }
            catch { /* fall back to raw body below */ }

            Log("");
            Log("=== Token response ===");
            if (map != null)
            {
                foreach (var kvp in map)
                {
                    string val = kvp.Value?.ToString() ?? "";
                    if ((kvp.Key.EndsWith("_token") || kvp.Key == "id_token") && val.Length > 24)
                        val = val.Substring(0, 24) + "... (" + val.Length + " chars)";
                    Log("  " + kvp.Key + ": " + val);
                }
            }
            else
            {
                Log(body);
            }
            Log("======================");

            string refresh = existingRefresh;
            string access = null, id = null;
            if (map != null)
            {
                if (map.TryGetValue("refresh_token", out var rt) && rt != null && rt.ToString().Length > 0)
                    refresh = rt.ToString();
                if (map.TryGetValue("access_token", out var at)) access = at?.ToString();
                if (map.TryGetValue("id_token", out var it)) id = it?.ToString();
            }

            string name = DisplayNameFrom(map);
            if (name == "(unknown user)" && !string.IsNullOrEmpty(existingUser))
                name = existingUser;

            DateTimeOffset expiry = ExpiryFrom(map);

            return new TokenRecord
            {
                Authority = authority,
                ClientId = clientId,
                RefreshToken = refresh,
                AccessToken = access,
                IdToken = id,
                UserName = name,
                ExpiresAtUnix = expiry == DateTimeOffset.MinValue ? 0 : expiry.ToUnixTimeSeconds(),
            };
        }

        // --- claims / expiry extraction --------------------------------------------

        private static string DisplayNameFrom(Dictionary<string, object> tokenResponse)
        {
            if (tokenResponse == null || !tokenResponse.TryGetValue("id_token", out var idt) || idt == null)
                return "(unknown user)";

            var claims = DecodeJwtPayload(idt.ToString());
            if (claims == null)
                return "(unknown user)";

            foreach (var key in new[] { "name", "preferred_username", "email", "sub" })
                if (claims.TryGetValue(key, out var v) && v != null && v.ToString().Length > 0)
                    return v.ToString();

            return "(unknown user)";
        }

        private static DateTimeOffset ExpiryFrom(Dictionary<string, object> tokenResponse)
        {
            if (tokenResponse == null)
                return DateTimeOffset.MinValue;

            if (tokenResponse.TryGetValue("expires_in", out var ei) && ei != null &&
                int.TryParse(ei.ToString(), out var seconds))
                return DateTimeOffset.Now.AddSeconds(seconds);

            if (tokenResponse.TryGetValue("id_token", out var idt) && idt != null)
            {
                var claims = DecodeJwtPayload(idt.ToString());
                if (claims != null && claims.TryGetValue("exp", out var exp) && exp != null &&
                    long.TryParse(exp.ToString(), out var unix))
                    return DateTimeOffset.FromUnixTimeSeconds(unix);
            }

            return DateTimeOffset.MinValue;
        }

        private static Dictionary<string, object> DecodeJwtPayload(string jwt)
        {
            try
            {
                var parts = jwt.Split('.');
                if (parts.Length < 2)
                    return null;

                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            }
            catch
            {
                return null;
            }
        }

        // --- helpers ---------------------------------------------------------------

        private void LaunchBrowser(string url)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        private static byte[] Sha256(string input)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(Encoding.ASCII.GetBytes(input));
        }

        private static string RandomUrlToken(int bytes)
        {
            var buffer = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(buffer);
            return Base64Url(buffer);
        }

        private static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
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

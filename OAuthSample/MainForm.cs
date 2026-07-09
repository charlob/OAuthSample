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
    /// Minimal WinForms harness that walks the OAuth 2.0 Authorization Code + PKCE
    /// flow end to end and logs every step, so you can watch exactly where a
    /// callback is (or isn't) being captured.
    ///
    /// The callback capture itself lives in <see cref="LoopbackCallbackListener"/>
    /// (shared with the OidcClient-based <see cref="OidcClientForm"/>): http via
    /// <see cref="System.Net.HttpListener"/>, https via in-process TLS. This form owns
    /// the parts that are the actual lesson: building the request, PKCE, the state
    /// check, the token exchange, and rendering the signed-in landing page.
    /// </summary>
    public sealed class MainForm : Form
    {
        // Shared HttpClient for the token exchange.
        private static readonly HttpClient Http = new HttpClient();

        private readonly TextBox _txtAuthority;
        private readonly TextBox _txtClientId;
        private readonly TextBox _txtRedirect;
        private readonly TextBox _txtScope;
        private readonly TextBox _txtAudience;
        private readonly Button _btnConnect;
        private readonly TextBox _txtLog;

        public MainForm()
        {
            Text = "OAuth 2.0 Sample (Authorization Code + PKCE)";
            Width = 900;
            Height = 640;
            MinimumSize = new Size(700, 480);
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
            _txtClientId = AddRow(layout, "Client ID", "");
            _txtRedirect = AddRow(layout, "Callback URI", "https://localhost:5021/callback/envd/");
            _txtScope = AddRow(layout, "Scope", "openid profile email");
            _txtAudience = AddRow(layout, "Audience (opt.)", "");

            _btnConnect = new Button
            {
                Text = "Connect",
                AutoSize = true,
                Padding = new Padding(16, 4, 16, 4),
                Anchor = AnchorStyles.Left,
            };
            _btnConnect.Click += OnConnectClick;

            layout.Controls.Add(new Label { Text = "", AutoSize = true }, 0, layout.RowCount);
            layout.Controls.Add(_btnConnect, 1, layout.RowCount);
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

            Log("Ready. Fill in Client ID (and adjust Callback URI to match what's");
            Log("registered at the IdP), then click Connect.");
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

        private async void OnConnectClick(object sender, EventArgs e)
        {
            _btnConnect.Enabled = false;
            try
            {
                await RunFlowAsync();
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
            }
            finally
            {
                _btnConnect.Enabled = true;
            }
        }

        private async Task RunFlowAsync()
        {
            string authority = _txtAuthority.Text.Trim().TrimEnd('/');
            string clientId = _txtClientId.Text.Trim();
            string redirectUri = _txtRedirect.Text.Trim();
            string scope = _txtScope.Text.Trim();
            string audience = _txtAudience.Text.Trim();

            if (string.IsNullOrEmpty(clientId))
            {
                Log("Please enter a Client ID.");
                return;
            }
            if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var redirect))
            {
                Log("Callback URI is not a valid absolute URL.");
                return;
            }

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

            // renderResult runs when the real callback arrives, with the browser response
            // still open — so we validate, exchange, and hand back the finished page.
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
                var (name, expiry) = await ExchangeCodeAsync(authority, clientId, redirectUri, ci.Code, codeVerifier);
                if (name == null)
                    return LandingPage.Error("Token exchange failed — see the app log.");
                return LandingPage.Success(name, expiry);
            }

            var listener = new LoopbackCallbackListener(Log);
            Task<CallbackInfo> waitForCallback =
                listener.WaitForCallbackAsync(redirect, CancellationToken.None, RenderResult);

            Log("Opening browser for authorization...");
            LaunchBrowser(authorizeUrl.ToString());

            try
            {
                await waitForCallback;
            }
            catch (Exception ex)
            {
                Log("Listener error: " + ex.Message);
            }
        }

        // --- token exchange --------------------------------------------------------

        /// <summary>Exchanges the code for tokens, logs them, and returns the display name
        /// and access-token expiry for the landing page. Returns (null, default) on failure.</summary>
        private async Task<(string name, DateTimeOffset expiry)> ExchangeCodeAsync(
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
                return (null, default);
            }

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

            return (DisplayNameFrom(map), ExpiryFrom(map));
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
            return Convert.ToBase64String(data)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
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

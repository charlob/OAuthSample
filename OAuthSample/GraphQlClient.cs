using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace OAuthSample
{
    /// <summary>Minimal GraphQL-over-HTTP caller used by the "Call test API" button.</summary>
    internal static class GraphQlClient
    {
        private static readonly HttpClient Http = new HttpClient();

        public const string GetUserDetailsQuery =
            "query GetUserDetails { userDetails { firstName lastName email isNewUser " +
            "defaultAccount accounts { envdAccountId accountLabel accountId accountType roles " +
            "properties { propertyName accreditations { lPA eU mSA nFAS eUGFHQB hRG } } } } }";

        /// <summary>POSTs a GraphQL query with a Bearer token (and optional envdAccountId header).</summary>
        public static async Task<Tuple<int, string>> PostAsync(
            string endpoint, string accessToken, string query, string envdAccountId)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                string payload = new JavaScriptSerializer().Serialize(new Dictionary<string, object> { ["query"] = query });
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                if (!string.IsNullOrEmpty(envdAccountId))
                    req.Headers.TryAddWithoutValidation("envdAccountId", envdAccountId);

                using (var resp = await Http.SendAsync(req))
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    return Tuple.Create((int)resp.StatusCode, body);
                }
            }
        }

        /// <summary>Pulls the envdAccountId out of a GetUserDetails response. Per the MLA
        /// reference, <c>userDetails.defaultAccount</c> holds it directly; falls back to the
        /// first account's envdAccountId.</summary>
        public static string ExtractEnvdAccountId(string body)
        {
            try
            {
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                if (root == null || !root.TryGetValue("data", out var d) || !(d is Dictionary<string, object> data))
                    return null;
                if (!data.TryGetValue("userDetails", out var u) || !(u is Dictionary<string, object> ud))
                    return null;

                // Primary: defaultAccount is the envdAccountId.
                if (ud.TryGetValue("defaultAccount", out var da) && da != null && da.ToString().Length > 0)
                    return da.ToString();

                // Fallback: first account's envdAccountId.
                if (ud.TryGetValue("accounts", out var a) && a is IEnumerable accounts)
                {
                    foreach (var item in accounts)
                    {
                        if (item is Dictionary<string, object> acc &&
                            acc.TryGetValue("envdAccountId", out var e) && e != null && e.ToString().Length > 0)
                            return e.ToString();
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Indents JSON for readable console output; returns non-JSON bodies unchanged.</summary>
        public static string Pretty(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return json ?? "";
            string trimmed = json.TrimStart();
            if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
                return json;

            var sb = new StringBuilder();
            int indent = 0;
            bool inString = false, escape = false;
            foreach (char c in json)
            {
                if (inString)
                {
                    sb.Append(c);
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inString = true; sb.Append(c); break;
                    case '{':
                    case '[': sb.Append(c); indent++; NewLine(sb, indent); break;
                    case '}':
                    case ']': indent--; NewLine(sb, indent); sb.Append(c); break;
                    case ',': sb.Append(c); NewLine(sb, indent); break;
                    case ':': sb.Append(": "); break;
                    default:
                        if (!char.IsWhiteSpace(c)) sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static void NewLine(StringBuilder sb, int indent)
        {
            sb.Append('\n').Append(new string(' ', Math.Max(0, indent) * 2));
        }
    }
}

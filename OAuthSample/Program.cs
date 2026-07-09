using System;
using System.Net;
using System.Windows.Forms;

namespace OAuthSample
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // Modern IdPs (Auth0 / MLA SSO) require TLS 1.2+. .NET Framework 4.8
            // can still default to older protocols depending on the OS, so pin it.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}

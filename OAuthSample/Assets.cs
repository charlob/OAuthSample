using System;
using System.IO;

namespace OAuthSample
{
    /// <summary>Locates logo/asset files in an <c>assets</c> folder next to the exe.</summary>
    internal static class Assets
    {
        private static string[] Dirs()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return new[] { Path.Combine(baseDir, "assets"), baseDir };
        }

        public static string Find(string baseName, params string[] extensions)
        {
            try
            {
                foreach (var dir in Dirs())
                    foreach (var ext in extensions)
                    {
                        string path = Path.Combine(dir, baseName + ext);
                        if (File.Exists(path))
                            return path;
                    }
            }
            catch
            {
                // ignore — treated as "not found"
            }
            return null;
        }

        public static string FindImage(string baseName) => Find(baseName, ".png", ".jpg", ".jpeg", ".gif");

        public static string FindAny(string baseName) => Find(baseName, ".png", ".svg", ".jpg", ".jpeg", ".gif");
    }
}

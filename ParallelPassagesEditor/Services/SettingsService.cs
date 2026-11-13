using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ParallelPassagesEditor.Services
{
    /// <summary>Simple key=value preferences in %APPDATA%\ParallelPassagesEditor\settings.txt.</summary>
    public static class SettingsService
    {
        public static string Get(string key)
        {
            Dictionary<string, string> all = ReadAll();
            string val;
            return all.TryGetValue(key, out val) ? val : null;
        }

        public static void Set(string key, string value)
        {
            try
            {
                Dictionary<string, string> all = ReadAll();
                all[key] = value ?? "";
                var lines = new List<string>();
                foreach (KeyValuePair<string, string> kv in all)
                    lines.Add(kv.Key + "=" + kv.Value);
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllLines(AppPaths.SettingsPath, lines.ToArray(), new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static Dictionary<string, string> ReadAll()
        {
            var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(AppPaths.SettingsPath)) return all;
                foreach (string line in File.ReadAllLines(AppPaths.SettingsPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                        all[line.Substring(0, eq).Trim()] = line.Substring(eq + 1);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return all;
        }
    }
}

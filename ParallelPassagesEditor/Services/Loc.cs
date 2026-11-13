using System;
using System.IO;
using System.Windows;

namespace ParallelPassagesEditor.Services
{
    public static class Loc
    {
        /// <summary>Looks up a localized string; returns the key itself when no WPF app is running (tests).</summary>
        public static string Get(string key)
        {
            if (Application.Current == null) return key;
            string val = Application.Current.Resources[key] as string;
            return val ?? key;
        }
    }

    public static class AppPaths
    {
        private static string _dataDir;

        /// <summary>%APPDATA%\ParallelPassagesEditor; settable so tests can redirect it.</summary>
        public static string DataDir
        {
            get
            {
                if (_dataDir == null)
                    _dataDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "ParallelPassagesEditor");
                return _dataDir;
            }
            set { _dataDir = value; }
        }

        public static string HiddenListPath
        {
            get { return Path.Combine(DataDir, "hidden.txt"); }
        }

        public static string AddedListPath
        {
            get { return Path.Combine(DataDir, "added.txt"); }
        }

        public static string SettingsPath
        {
            get { return Path.Combine(DataDir, "settings.txt"); }
        }
    }
}

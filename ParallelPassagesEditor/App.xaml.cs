using System;
using System.IO;
using System.Windows;
using ParallelPassagesEditor.Services;

namespace ParallelPassagesEditor
{
    public partial class App : Application
    {
        public static readonly string[] Cultures = { "en", "es", "fr", "pt", "zh-Hans" };

        // Exit codes read by the plugin, which writes or removes the per-project hidden list
        // itself (this exe runs as administrator and must not write into the project folder).
        public const int ExitKeepProjectList = 10;
        public const int ExitRemoveProjectList = 11;
        public static int PluginExitCode;

        protected override void OnExit(ExitEventArgs e)
        {
            e.ApplicationExitCode = PluginExitCode;
            base.OnExit(e);
        }

        // Arguments: [ParallelPassages.xml path] [--pending <file>] [--project <project folder>]
        // With no path, MainWindow looks in the Paratext 9 install folder, then the last file used.
        // --project (from the plugin or the Checks-menu script) enables the per-project hidden list.
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            string filePath = null;
            string pendingPath = null;
            string projectDir = null;
            for (int i = 0; i < e.Args.Length; i++)
            {
                string arg = e.Args[i];
                if (string.Compare(arg, "--pending", StringComparison.OrdinalIgnoreCase) == 0)
                {
                    if (i + 1 < e.Args.Length)
                        pendingPath = e.Args[++i];
                }
                else if (string.Compare(arg, "--project", StringComparison.OrdinalIgnoreCase) == 0)
                {
                    if (i + 1 < e.Args.Length)
                        projectDir = e.Args[++i].TrimEnd('\\');
                }
                else if (filePath == null)
                {
                    filePath = arg;
                }
            }

            string culture = SettingsService.Get("Language");
            if (culture != null && Array.IndexOf(Cultures, culture) > 0)
                SwitchLanguage(culture);

            var window = new MainWindow();
            MainWindow = window;
            window.Show();

            string capturedPath = filePath;
            string capturedPending = pendingPath;
            string capturedProject = projectDir != null && Directory.Exists(projectDir) ? projectDir : null;
            window.Dispatcher.BeginInvoke(new Action(delegate()
            {
                window.Startup(capturedPath, capturedPending, capturedProject);
            }));
        }

        public static void SwitchLanguage(string culture)
        {
            var dicts = Current.Resources.MergedDictionaries;
            ResourceDictionary toRemove = null;
            foreach (ResourceDictionary d in dicts)
            {
                if (d.Source != null && d.Source.OriginalString.Contains("Strings."))
                {
                    toRemove = d;
                    break;
                }
            }
            if (toRemove != null)
                dicts.Remove(toRemove);

            var newDict = new ResourceDictionary();
            newDict.Source = new Uri("Resources/Strings." + culture + ".xaml", UriKind.Relative);
            dicts.Add(newDict);
        }
    }
}

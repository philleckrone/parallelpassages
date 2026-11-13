using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;
using Paratext.PluginInterfaces;
using ParallelPassagesEditor.Services;

namespace ParallelPassagesPlugin
{
    /// <summary>
    /// Paratext plugin that adds Tools > Parallel Passages Editor... to Scripture windows.
    /// Plugins run inside Paratext without administrator rights, so this only launches the
    /// separate ParallelPassagesEditor.exe (manifest requireAdministrator), which edits
    /// ParallelPassages.xml in the Paratext install folder. The exe lives in
    /// &lt;Program Files&gt;\ParallelPassagesEditor, not beside this plugin: Paratext makes its
    /// plugins folder writable for every user, so an exe there could be swapped by anyone and
    /// then run as administrator. The plugin starts it only if that folder is admin-only.
    /// </summary>
    public class ParallelPassagesEditorPlugin : IParatextStandalonePlugin
    {
        public const string PluginName = "Parallel Passages Editor";
        public const string PublisherName = "Phil Leckrone (SIL)";
        public const string PluginFolderName = "ParallelPassagesEditor";
        public const string EditorExeName = "ParallelPassagesEditor.exe";
        public const string MenuText = "Parallel Passages Editor...";

        private const string DefaultProjectsDir = @"C:\My Paratext 9 Projects";
        private const int ErrorCancelled = 1223;   // the user said No at the Windows permission prompt

        public string Name
        {
            get { return PluginName; }
        }

        public Version Version
        {
            get { return new Version(1, 3, 0, 0); }
        }

        public string VersionString
        {
            get { return "1.3"; }
        }

        public string Publisher
        {
            get { return PublisherName; }
        }

        public IEnumerable<PluginMenuEntry> PluginMenuEntries
        {
            get
            {
                var entry = new PluginMenuEntry(MenuText, OnMenuClicked, PluginMenuLocation.ScrTextTools);
                entry.LocalizedTextNeeded += LocalizeMenuText;
                yield return entry;
            }
        }

        /// <summary>Menu text in Paratext's interface language (same languages as the editor); null keeps English.</summary>
        internal static string LocalizeMenuText(string defaultText, string locale)
        {
            string lang = string.IsNullOrEmpty(locale) ? "" : locale.ToLowerInvariant();
            if (lang.StartsWith("es")) return "Editor de pasajes paralelos...";
            if (lang.StartsWith("fr")) return "\u00c9diteur de passages parall\u00e8les...";
            if (lang.StartsWith("pt")) return "Editor de passagens paralelas...";
            if (lang.StartsWith("zh")) return "\u5e73\u884c\u7ecf\u6587\u7f16\u8f91\u5668...";
            return null;
        }

        public string GetDescription(string locale)
        {
            string lang = string.IsNullOrEmpty(locale) ? "en" : locale.ToLowerInvariant();
            if (lang.StartsWith("es"))
                return "Oculta o muestra libros y cap\u00edtulos en la lista de pasajes paralelos de Paratext (ParallelPassages.xml).";
            if (lang.StartsWith("fr"))
                return "Masque ou affiche des livres et des chapitres dans la liste des passages parall\u00e8les de Paratext (ParallelPassages.xml).";
            if (lang.StartsWith("pt"))
                return "Oculta ou mostra livros e cap\u00edtulos na lista de passagens paralelas do Paratext (ParallelPassages.xml).";
            if (lang.StartsWith("zh"))
                return "\u5728 Paratext \u7684\u5e73\u884c\u7ecf\u6587\u5217\u8868 (ParallelPassages.xml) \u4e2d\u9690\u85cf\u6216\u663e\u793a\u4e66\u5377\u548c\u7ae0\u3002";
            return "Hides or unhides books and chapters in Paratext's parallel passages list (ParallelPassages.xml).";
        }

        public IDataFileMerger GetMerger(IPluginHost host, string dataIdentifier)
        {
            return null;   // the plugin keeps no project data
        }

        private void OnMenuClicked(IPluginHost host, IParatextChildState state)
        {
            string exePath = null;
            try
            {
                string paratextDir = ParatextDirectory();
                exePath = ResolveExePath();
                if (exePath == null || !File.Exists(exePath))
                {
                    Report(host, "The Parallel Passages Editor program was not found:\n" + exePath +
                        "\n\nPlease reinstall the plugin (double-click Install-Plugin.cmd).", null);
                    return;
                }
                // It runs as administrator: refuse a copy that ordinary users could have replaced.
                string folder = Path.GetDirectoryName(exePath);
                if (!IsAdminOnly(Path.GetDirectoryName(folder), true) || !IsAdminOnly(folder, true) || !IsAdminOnly(exePath, false))
                {
                    Report(host, "The Parallel Passages Editor was not started: ordinary users can change\n" + exePath +
                        "\nso it may not be the real program. Please reinstall the plugin (double-click Install-Plugin.cmd).", null);
                    return;
                }

                var psi = new ProcessStartInfo(exePath);
                string args = "";
                string xmlPath = ResolveXmlPath(paratextDir);
                if (xmlPath != null)
                    args = "\"" + xmlPath + "\"";
                // The project's folder lets the editor keep a hidden list per project. Without one
                // (a resource window, or the folder not found) the editor still opens as before.
                string projectDir = ActiveProjectDir(state);
                if (projectDir != null)
                    args += " --project \"" + projectDir.TrimEnd('\\') + "\"";
                psi.Arguments = args.Trim();
                psi.UseShellExecute = true;
                psi.Verb = "runas";   // the editor changes a file in Program Files
                psi.WorkingDirectory = folder;
                Process editor = Process.Start(psi);
                // The editor runs as administrator, so it never writes into the project folder
                // (every user can change that folder). Its exit code says what the last save chose
                // for the project's hidden list, and this plugin, without administrator rights,
                // writes or removes the list.
                if (editor != null && projectDir != null && xmlPath != null)
                {
                    string proj = projectDir.TrimEnd('\\');
                    string xml = xmlPath;
                    editor.Exited += delegate { UpdateProjectList(host, editor, xml, proj); };
                    editor.EnableRaisingEvents = true;
                }
                else if (editor != null)
                {
                    editor.Dispose();
                }
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == ErrorCancelled) return;
                Report(host, "Could not start the Parallel Passages Editor:\n" + exePath + "\n\n" + ex.Message, ex);
            }
            catch (Exception ex)
            {
                // Never let an error escape into Paratext.
                Report(host, "Could not start the Parallel Passages Editor:\n" + exePath + "\n\n" + ex.Message, ex);
            }
        }

        // The editor's exit codes (ParallelPassagesEditor App.ExitKeepProjectList / ExitRemoveProjectList).
        private const int ExitKeepProjectList = 10;
        private const int ExitRemoveProjectList = 11;

        /// <summary>
        /// After the editor closes: writes the hidden list of the saved ParallelPassages.xml to
        /// &lt;project&gt;\local\ParallelPassagesEditor\hidden.txt, or removes that file.
        /// </summary>
        private void UpdateProjectList(IPluginHost host, Process editor, string xmlPath, string projectDir)
        {
            string listPath = HiddenListService.ProjectListPath(projectDir);
            try
            {
                int code;
                try { code = editor.ExitCode; }
                finally { editor.Dispose(); }
                if (code == ExitKeepProjectList)
                    HiddenListService.Save(HiddenListService.Build(ParallelPassagesFileService.Load(xmlPath)), listPath);
                else if (code == ExitRemoveProjectList && File.Exists(listPath))
                    File.Delete(listPath);
            }
            catch (Exception ex)
            {
                Report(host, "ParallelPassages.xml was saved, but the project's hidden list could not be updated:\n" +
                    listPath + "\n\n" + ex.Message, ex);
            }
        }

        private void Report(IPluginHost host, string message, Exception ex)
        {
            try
            {
                if (host != null)
                    host.Log(this, message + (ex != null ? "\n" + ex : ""));
            }
            catch (Exception) { }
            MessageBox.Show(message, PluginName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>Folder of the running Paratext.exe (also the base folder of the app domain).</summary>
        private static string ParatextDirectory()
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                if (!string.IsNullOrEmpty(exe))
                    return Path.GetDirectoryName(exe);
            }
            catch (Exception) { }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        /// <summary>Folder of the project in the active window, or null (resource, no project, not found).</summary>
        private static string ActiveProjectDir(IParatextChildState state)
        {
            try
            {
                IProject project = state != null ? state.Project : null;
                if (project == null || project.IsResource) return null;
                return ResolveProjectDir(ProjectsDirectory(), project.ShortName, project.ID);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // -- Project folder lookup (copied from AutoCorrect\AutoCorrectPlugin): the plugin API gives
        // no project folder, so it is found from the Paratext projects folder in the registry.

        /// <summary>
        /// The Paratext projects folder: Settings_Directory under SOFTWARE\Paratext\8 (Paratext 9
        /// still uses that key), checked in both registry views, then for the current user.
        /// </summary>
        internal static string ProjectsDirectory()
        {
            foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                string dir = ReadSettingsDirectory(RegistryHive.LocalMachine, view);
                if (dir != null) return dir;
            }
            string user = ReadSettingsDirectory(RegistryHive.CurrentUser, RegistryView.Default);
            return user ?? DefaultProjectsDir;
        }

        private static string ReadSettingsDirectory(RegistryHive hive, RegistryView view)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\Paratext\8"))
                {
                    if (key == null) return null;
                    string dir = key.GetValue("Settings_Directory") as string;
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        return dir.TrimEnd('\\');
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// The project's folder: projectsDir\shortName when its Settings.xml Guid matches the
        /// project ID (or has no readable Guid); otherwise the subfolder whose Guid matches;
        /// if no folder's Guid matches, projectsDir\shortName when it holds a Settings.xml.
        /// </summary>
        internal static string ResolveProjectDir(string projectsDir, string shortName, string projectId)
        {
            if (string.IsNullOrEmpty(projectsDir) || !Directory.Exists(projectsDir)) return null;

            string byName = null;
            if (!string.IsNullOrEmpty(shortName))
            {
                string candidate = Path.Combine(projectsDir, shortName);
                string settings = Path.Combine(candidate, "Settings.xml");
                if (File.Exists(settings))
                {
                    byName = candidate;
                    string guid = ReadProjectGuid(settings);
                    if (guid == null || string.IsNullOrEmpty(projectId) || SameId(guid, projectId))
                        return candidate;
                }
            }

            if (string.IsNullOrEmpty(projectId)) return byName;
            try
            {
                foreach (string dir in Directory.GetDirectories(projectsDir))
                {
                    string settings = Path.Combine(dir, "Settings.xml");
                    if (File.Exists(settings) && SameId(ReadProjectGuid(settings), projectId))
                        return dir;
                }
            }
            catch (Exception) { }
            return byName;
        }

        private static bool SameId(string a, string b)
        {
            return a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The ScriptureText/Guid value of a Settings.xml, or null.</summary>
        internal static string ReadProjectGuid(string settingsPath)
        {
            try
            {
                var xmlSettings = new XmlReaderSettings();
                xmlSettings.DtdProcessing = DtdProcessing.Ignore;
                using (XmlReader reader = XmlReader.Create(settingsPath, xmlSettings))
                {
                    if (reader.ReadToFollowing("Guid"))
                    {
                        string guid = reader.ReadElementContentAsString();
                        return string.IsNullOrEmpty(guid) ? null : guid.Trim();
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// &lt;Program Files&gt;\ParallelPassagesEditor\ParallelPassagesEditor.exe, where the installer
        /// puts it. Program Files comes from the machine registry (the 64-bit folder on 64-bit
        /// Windows), not from environment variables, which a user can set. Null if not found.
        /// </summary>
        internal static string ResolveExePath()
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion"))
                    {
                        if (key == null) continue;
                        foreach (string name in new[] { "ProgramW6432Dir", "ProgramFilesDir" })
                        {
                            string dir = key.GetValue(name) as string;
                            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                                return Path.Combine(Path.Combine(dir, PluginFolderName), EditorExeName);
                        }
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        // Administrators, SYSTEM and TrustedInstaller.
        private static readonly string[] TrustedSids =
        {
            "S-1-5-32-544", "S-1-5-18", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
        };

        /// <summary>
        /// True when only administrators can change the file or folder: owned by a trusted
        /// account, no write-type permission for anyone else, and not a link. The same test as
        /// Test-AdminOnlyFolder in the installer.
        /// </summary>
        internal static bool IsAdminOnly(string path, bool isFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return false;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
                System.Security.AccessControl.FileSystemSecurity sec = isFolder
                    ? (System.Security.AccessControl.FileSystemSecurity)Directory.GetAccessControl(path)
                    : File.GetAccessControl(path);
                string owner = sec.GetOwner(typeof(System.Security.Principal.SecurityIdentifier)).Value;
                if (Array.IndexOf(TrustedSids, owner) < 0) return false;
                // WriteData, AppendData, DeleteSubdirectoriesAndFiles, Delete, ChangePermissions,
                // TakeOwnership, GenericAll, GenericWrite.
                const long writeMask = 0x2 | 0x4 | 0x40 | 0x10000 | 0x40000 | 0x80000 | 0x10000000 | 0x40000000;
                foreach (System.Security.AccessControl.FileSystemAccessRule r in
                    sec.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
                {
                    if (r.AccessControlType != System.Security.AccessControl.AccessControlType.Allow) continue;
                    if ((r.PropagationFlags & System.Security.AccessControl.PropagationFlags.InheritOnly) != 0) continue;
                    if (((long)(int)r.FileSystemRights & writeMask) == 0) continue;
                    if (Array.IndexOf(TrustedSids, r.IdentityReference.Value) < 0) return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>ParallelPassages.xml of this Paratext installation, or null if it is not there.</summary>
        internal static string ResolveXmlPath(string paratextDir)
        {
            if (string.IsNullOrEmpty(paratextDir)) return null;
            string xml = Path.Combine(Path.Combine(paratextDir, "ParallelPassages"), "ParallelPassages.xml");
            return File.Exists(xml) ? xml : null;
        }
    }
}

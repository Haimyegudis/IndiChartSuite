using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;

namespace IndiChart.UI.Services
{
    public class UpdateService
    {
        // Server paths - change these if server location changes
        private const string VersionFileUrl = @"\\iihome.inr.rd.hpicorp.net\softwareqa$\QA-Utils\IndiChart\version.txt";
        private const string InstallerFolder = @"\\iihome.inr.rd.hpicorp.net\softwareqa$\QA-Utils\IndiChart";

        public async Task CheckForUpdatesAsync()
        {
            await Task.Run(() =>
            {
                try
                {
                    UpdateLogger.Log("Starting update check...");

                    // Get current version from assembly
                    Version? currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
                    if (currentVersion == null)
                    {
                        UpdateLogger.Log("Could not get current version");
                        return;
                    }
                    UpdateLogger.Log($"Current version: {currentVersion}");

                    // Check if server is accessible
                    if (!File.Exists(VersionFileUrl))
                    {
                        UpdateLogger.Log($"Version file not found at: {VersionFileUrl}");
                        return;
                    }

                    // Read version from server
                    string serverVersionText = File.ReadAllText(VersionFileUrl).Trim();
                    UpdateLogger.Log($"Server version text: {serverVersionText}");

                    if (!Version.TryParse(serverVersionText, out Version? serverVersion))
                    {
                        UpdateLogger.Log($"Failed to parse server version: {serverVersionText}");
                        return;
                    }

                    UpdateLogger.Log($"Server version: {serverVersion}");

                    // Compare versions
                    if (serverVersion > currentVersion)
                    {
                        UpdateLogger.Log($"New version available: {serverVersion}");

                        // Show dialog on UI thread
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            var result = MessageBox.Show(
                                $"A new version is available!\n\n" +
                                $"Current version: {currentVersion}\n" +
                                $"New version: {serverVersion}\n\n" +
                                "Do you want to open the update folder?",
                                "IndiChart Update Available",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Information);

                            if (result == MessageBoxResult.Yes)
                            {
                                try
                                {
                                    // Open the installer folder in Explorer
                                    Process.Start("explorer.exe", InstallerFolder);
                                    UpdateLogger.Log("Opened installer folder");
                                }
                                catch (Exception ex)
                                {
                                    UpdateLogger.Log("Failed to open folder", ex);
                                    MessageBox.Show($"Could not open folder:\n{InstallerFolder}\n\nError: {ex.Message}",
                                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                                }
                            }
                        });
                    }
                    else
                    {
                        UpdateLogger.Log("Already at latest version");
                    }
                }
                catch (Exception ex)
                {
                    UpdateLogger.Log("Update check failed", ex);
                }
            });
        }

        /// <summary>
        /// Gets the current application version
        /// </summary>
        public static Version? GetCurrentVersion()
        {
            return Assembly.GetExecutingAssembly().GetName().Version;
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace DrawingHelper.Setup;

public partial class App : Application
{
    public const string AppName = "DrawingHelper";
    public const string AppVersion = "1.0.0";
    public const string UninstallRegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DrawingHelper";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            RunUninstall();
            Shutdown();
            return;
        }

        var win = new SetupWindow();
        MainWindow = win;
        win.Show();
    }

    private static void RunUninstall()
    {
        var answer = MessageBox.Show(
            $"{AppName}를 컴퓨터에서 제거하시겠습니까?",
            $"{AppName} 제거",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        string? installDir = null;
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallRegKey))
        {
            installDir = key?.GetValue("InstallLocation") as string;
        }

        // Remove Desktop shortcut
        string desktopLnk = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"{AppName}.lnk");
        if (File.Exists(desktopLnk))
            try { File.Delete(desktopLnk); } catch { }

        // Remove Start Menu folder
        string startDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            AppName);
        if (Directory.Exists(startDir))
            try { Directory.Delete(startDir, true); } catch { }

        // Remove registry entry
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallRegKey, false); } catch { }

        // Schedule removal of install directory after this uninstaller exits
        if (!string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/C timeout /t 2 /nobreak >nul & rmdir /s /q \"{installDir}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            catch { }
        }

        MessageBox.Show($"{AppName} 제거가 완료되었습니다.", $"{AppName} 제거",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }
}

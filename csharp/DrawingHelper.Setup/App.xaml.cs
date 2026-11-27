using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace DrawingHelper.Setup;

public partial class App : Application
{
    public const string AppName = "DrawingHelper";
    public const string AppVersion = "1.1.0";
    public const string ManifestFileName = ".install_manifest.txt";
    public const string UninstallRegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DrawingHelper";

    private static readonly char[] ForbiddenPathChars = ['"', '&', '|', '%', '<', '>', '^', '\r', '\n'];

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

    /// <summary>
    /// Validates that a target install directory is an absolute, non-root, non-system user folder
    /// free of shell metacharacters so install/uninstall can never touch system or personal folders.
    /// </summary>
    public static bool IsSafeInstallDir(string? path, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "설치 폴더를 입력하세요.";
            return false;
        }
        if (path.IndexOfAny(ForbiddenPathChars) >= 0)
        {
            reason = "설치 경로에 허용되지 않는 특수문자가 포함되어 있습니다.";
            return false;
        }

        string full;
        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                reason = "절대 경로를 입력하세요.";
                return false;
            }
            full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            reason = "올바른 폴더 경로가 아닙니다.";
            return false;
        }

        string? root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            reason = "드라이브 루트 폴더에는 직접 설치할 수 없습니다.";
            return false;
        }

        Environment.SpecialFolder[] protectedFolders =
        [
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData
        ];

        foreach (var sf in protectedFolders)
        {
            string pf = Environment.GetFolderPath(sf).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.IsNullOrEmpty(pf) && string.Equals(full, pf, StringComparison.OrdinalIgnoreCase))
            {
                reason = "시스템 또는 사용자 기본 폴더에 직접 설치할 수 없습니다. 하위 폴더(예: ...\\DrawingHelper)를 지정하세요.";
                return false;
            }
        }

        return true;
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

        // Safely delete ONLY files belonging to DrawingHelper (never recursive wipe of arbitrary folders)
        if (IsSafeInstallDir(installDir, out _) && Directory.Exists(installDir))
        {
            string normalizedRoot = Path.GetFullPath(installDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string manifestPath = Path.Combine(installDir, ManifestFileName);
            string uninstallerPath = Path.Combine(installDir, "Uninstall.exe");

            if (File.Exists(manifestPath))
            {
                foreach (string relLine in File.ReadAllLines(manifestPath))
                {
                    string rel = relLine.Trim();
                    if (string.IsNullOrEmpty(rel)) continue;
                    try
                    {
                        string target = Path.GetFullPath(Path.Combine(installDir, rel));
                        if (!target.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(target, Path.GetFullPath(uninstallerPath), StringComparison.OrdinalIgnoreCase)) continue;
                        if (File.Exists(target)) File.Delete(target);
                    }
                    catch { }
                }
                try { File.Delete(manifestPath); } catch { }
            }
            else if (File.Exists(Path.Combine(installDir, "DrawingHelper.exe")))
            {
                // Fallback for pre-1.1.0 installs without manifest: delete only known app files
                string[] knownFiles =
                [
                    "DrawingHelper.exe", "DrawingHelper.dll", "DrawingHelper.deps.json",
                    "DrawingHelper.runtimeconfig.json", "DrawingHelper.Core.dll", "minecraft_palette_rgb.md"
                ];
                foreach (string name in knownFiles)
                {
                    string target = Path.Combine(installDir, name);
                    if (File.Exists(target))
                        try { File.Delete(target); } catch { }
                }
            }

            // Delete self (Uninstall.exe) and remove installDir ONLY if completely empty,
            // passing paths via environment variables + -LiteralPath (zero command injection risk).
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-WindowStyle");
                psi.ArgumentList.Add("Hidden");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(
                    "Start-Sleep -Seconds 2; " +
                    "if (Test-Path -LiteralPath $env:DH_UNINSTALL_EXE) { Remove-Item -LiteralPath $env:DH_UNINSTALL_EXE -Force -ErrorAction SilentlyContinue }; " +
                    "if ((Test-Path -LiteralPath $env:DH_INSTALL_DIR) -and ((Get-ChildItem -LiteralPath $env:DH_INSTALL_DIR -Force -ErrorAction SilentlyContinue | Measure-Object).Count -eq 0)) { Remove-Item -LiteralPath $env:DH_INSTALL_DIR -Force -ErrorAction SilentlyContinue }");
                psi.Environment["DH_UNINSTALL_EXE"] = uninstallerPath;
                psi.Environment["DH_INSTALL_DIR"] = installDir;
                Process.Start(psi);
            }
            catch { }
        }

        MessageBox.Show($"{AppName} 제거가 완료되었습니다.", $"{AppName} 제거",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }
}

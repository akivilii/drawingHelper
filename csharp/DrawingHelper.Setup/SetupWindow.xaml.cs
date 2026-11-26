using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DrawingHelper.Setup;

public partial class SetupWindow : Window
{
    private const string PayloadResourceName = "DrawingHelper.Setup.app_payload.zip";
    private int _step = 1;
    private string _installedExePath = "";

    public SetupWindow()
    {
        InitializeComponent();
        TxtInstallDir.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            App.AppName);
        UpdateStepUi();
    }

    private void UpdateStepUi()
    {
        Page1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Page2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Page3.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Page4.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

        var activeBrush = (Brush)FindResource("SidebarText");
        var mutedBrush = (Brush)FindResource("SidebarMuted");

        Step1Lbl.Foreground = _step == 1 ? activeBrush : mutedBrush;
        Step1Lbl.FontWeight = _step == 1 ? FontWeights.SemiBold : FontWeights.Normal;
        Step2Lbl.Foreground = _step == 2 ? activeBrush : mutedBrush;
        Step2Lbl.FontWeight = _step == 2 ? FontWeights.SemiBold : FontWeights.Normal;
        Step3Lbl.Foreground = _step == 3 ? activeBrush : mutedBrush;
        Step3Lbl.FontWeight = _step == 3 ? FontWeights.SemiBold : FontWeights.Normal;
        Step4Lbl.Foreground = _step == 4 ? activeBrush : mutedBrush;
        Step4Lbl.FontWeight = _step == 4 ? FontWeights.SemiBold : FontWeights.Normal;

        BtnBack.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        BtnCancel.Visibility = _step == 4 ? Visibility.Collapsed : Visibility.Visible;
        BtnNext.IsEnabled = _step != 3;
        BtnCancel.IsEnabled = _step != 3;

        BtnNext.Content = _step switch
        {
            1 => "다음 >",
            2 => "설치",
            4 => "마침",
            _ => "설치 중..."
        };
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "설치할 폴더 선택",
            InitialDirectory = Directory.Exists(TxtInstallDir.Text)
                ? TxtInstallDir.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        if (dlg.ShowDialog(this) == true)
        {
            string chosen = dlg.FolderName;
            if (!chosen.EndsWith(App.AppName, StringComparison.OrdinalIgnoreCase))
                chosen = Path.Combine(chosen, App.AppName);
            TxtInstallDir.Text = chosen;
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_step == 2)
        {
            _step = 1;
            UpdateStepUi();
        }
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_step == 1)
        {
            if (string.IsNullOrWhiteSpace(TxtInstallDir.Text))
            {
                MessageBox.Show(this, "설치 폴더를 입력하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _step = 2;
            UpdateStepUi();
            return;
        }

        if (_step == 2)
        {
            _step = 3;
            UpdateStepUi();
            await RunInstallAsync();
            return;
        }

        if (_step == 4)
        {
            if (ChkLaunchNow.IsChecked == true && File.Exists(_installedExePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _installedExePath,
                    WorkingDirectory = Path.GetDirectoryName(_installedExePath)!,
                    UseShellExecute = true
                });
            }
            Close();
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private async Task RunInstallAsync()
    {
        string installDir = TxtInstallDir.Text.Trim();
        bool createDesktop = ChkDesktopShortcut.IsChecked == true;
        bool createStartMenu = ChkStartMenu.IsChecked == true;
        bool registerUninstall = ChkRegisterUninstall.IsChecked == true;

        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(installDir);

                using Stream? zipStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName);
                if (zipStream is null)
                    throw new InvalidOperationException("내장된 설치 패키지(app_payload.zip)를 찾을 수 없습니다.");

                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                int total = Math.Max(1, entries.Count + 3);
                int done = 0;

                foreach (var entry in entries)
                {
                    string destPath = Path.GetFullPath(Path.Combine(installDir, entry.FullName));
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    Dispatcher.Invoke(() =>
                    {
                        LblCurrentFile.Text = entry.FullName;
                        ProgressBar.Value = (++done) * 100.0 / total;
                    });
                    entry.ExtractToFile(destPath, overwrite: true);
                }

                string exePath = Path.Combine(installDir, "DrawingHelper.exe");
                _installedExePath = exePath;

                // Copy self as uninstaller inside install folder
                string? currentSetupExe = Environment.ProcessPath;
                string uninstallerPath = Path.Combine(installDir, "Uninstall.exe");
                if (!string.IsNullOrEmpty(currentSetupExe) && File.Exists(currentSetupExe) &&
                    !string.Equals(Path.GetFullPath(currentSetupExe), Path.GetFullPath(uninstallerPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(currentSetupExe, uninstallerPath, overwrite: true);
                }

                Dispatcher.Invoke(() =>
                {
                    LblProgressStatus.Text = "바로가기를 생성하는 중...";
                    ProgressBar.Value = (++done) * 100.0 / total;
                });

                if (createDesktop)
                {
                    string desktopLnk = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                        $"{App.AppName}.lnk");
                    ShellLinkHelper.CreateShortcut(
                        desktopLnk, exePath, installDir, "Minecraft Drawing Helper", iconPath: exePath);
                }

                if (createStartMenu)
                {
                    string startDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                        App.AppName);
                    ShellLinkHelper.CreateShortcut(
                        Path.Combine(startDir, $"{App.AppName}.lnk"),
                        exePath, installDir, "Minecraft Drawing Helper", iconPath: exePath);
                    if (File.Exists(uninstallerPath))
                    {
                        ShellLinkHelper.CreateShortcut(
                            Path.Combine(startDir, $"{App.AppName} 제거.lnk"),
                            uninstallerPath, installDir, $"{App.AppName} 제거",
                            arguments: "--uninstall", iconPath: exePath);
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    LblProgressStatus.Text = "설치 정보를 등록하는 중...";
                    ProgressBar.Value = (++done) * 100.0 / total;
                });

                if (registerUninstall && File.Exists(uninstallerPath))
                {
                    using var key = Registry.CurrentUser.CreateSubKey(App.UninstallRegKey);
                    key.SetValue("DisplayName", App.AppName);
                    key.SetValue("DisplayVersion", App.AppVersion);
                    key.SetValue("Publisher", "akivilii");
                    key.SetValue("InstallLocation", installDir);
                    key.SetValue("DisplayIcon", exePath);
                    key.SetValue("UninstallString", $"\"{uninstallerPath}\" --uninstall");
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }

                Dispatcher.Invoke(() => ProgressBar.Value = 100);
            });

            LblInstalledPath.Text = $"설치 위치: {installDir}";
            _step = 4;
            UpdateStepUi();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"설치 중 오류가 발생했습니다:\n{ex.Message}", "설치 오류",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _step = 2;
            UpdateStepUi();
        }
    }
}

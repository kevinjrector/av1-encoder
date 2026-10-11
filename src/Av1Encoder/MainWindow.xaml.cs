using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace Av1Encoder;

public partial class MainWindow : Window
{
    readonly MainViewModel viewModel = new();
    string lastScanned = "";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    async void Window_Loaded(object sender, RoutedEventArgs args) => await RescanAsync();

    async void BrowseSource_Click(object sender, RoutedEventArgs args)
    {
        if (PickFolder("Choose the folder with videos to encode", viewModel.Source) is { } folder)
        {
            viewModel.Source = folder;
            await RescanAsync();
        }
    }

    async void BrowseDestination_Click(object sender, RoutedEventArgs args)
    {
        if (PickFolder("Choose where the AV1 files go", viewModel.Destination) is { } folder)
        {
            viewModel.Destination = folder;
            await RescanAsync();
        }
    }

    async void FolderBox_LostFocus(object sender, RoutedEventArgs args)
    {
        if (viewModel.IsIdle && $"{viewModel.Source}|{viewModel.Destination}" != lastScanned)
            await RescanAsync();
    }

    async void Start_Click(object sender, RoutedEventArgs args)
    {
        lastScanned = $"{viewModel.Source}|{viewModel.Destination}";
        await viewModel.StartAsync();
    }

    void Stop_Click(object sender, RoutedEventArgs args) => viewModel.Stop();

    async void InstallFfmpeg_Click(object sender, RoutedEventArgs args) => await viewModel.InstallFfmpegAsync();

    void Window_Closing(object? sender, CancelEventArgs args)
    {
        if (viewModel.IsRunning)
        {
            var answer = MessageBox.Show(this,
                "Encoding is still running. Stop it and close?\n\nFinished files are kept. Files in progress are deleted and will be redone next time.",
                "AV1 Encoder", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                args.Cancel = true;
                return;
            }
            viewModel.Shutdown();
        }
        viewModel.SaveSettings();
    }

    void OpenSourceFolder_Click(object sender, RoutedEventArgs args) => OpenFolder(viewModel.Source);

    void OpenDestinationFolder_Click(object sender, RoutedEventArgs args) => OpenFolder(viewModel.Destination);

    void Exit_Click(object sender, RoutedEventArgs args) => Close();

    void OpenLogsFolder_Click(object sender, RoutedEventArgs args)
    {
        Directory.CreateDirectory(AppLog.Folder);
        OpenFolder(AppLog.Folder);
    }

    void OpenAppLog_Click(object sender, RoutedEventArgs args) => OpenLogFile("app.log");

    void OpenEncodesLog_Click(object sender, RoutedEventArgs args) => OpenLogFile("encodes.jsonl");

    void About_Click(object sender, RoutedEventArgs args)
    {
        var version = typeof(App).Assembly.GetName().Version;
        var ffmpeg = Tools.Locate() ? Tools.Ffmpeg : "not found";
        MessageBox.Show(this,
            $"AV1 Encoder {version}\n\nffmpeg: {ffmpeg}\nSettings and logs: {Settings.Folder}",
            "About AV1 Encoder", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            MessageBox.Show(this, $"This folder does not exist yet:\n\n{folder}", "AV1 Encoder",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    void OpenLogFile(string fileName)
    {
        var path = Path.Combine(AppLog.Folder, fileName);
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"{fileName} has not been written yet. It appears after the first encode.", "AV1 Encoder",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // .jsonl usually has no file association
            Process.Start("notepad.exe", path);
        }
    }

    async Task RescanAsync()
    {
        lastScanned = $"{viewModel.Source}|{viewModel.Destination}";
        viewModel.SaveSettings();
        await viewModel.ScanAsync();
    }

    string? PickFolder(string title, string current)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (Directory.Exists(current))
            dialog.InitialDirectory = current;
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }
}

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PwVault.App.Services;
using PwVault.App.ViewModels;

namespace PwVault.App.Views;

public partial class MainWindow : Window, IFileDialogs
{
    public MainWindow()
    {
        InitializeComponent();

        // 無操作の判定用。子要素が処理済みのイベントも拾えるようトンネルで受ける
        AddHandler(KeyDownEvent, OnActivity, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnActivity, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnActivity, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, OnActivity, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnActivity(object? sender, RoutedEventArgs e) =>
        (DataContext as MainViewModel)?.AutoLock.NotifyActivity();

    public async Task<string?> SaveFileAsync(string title, string suggestedFileName, string filterName, string extension)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(filterName) { Patterns = [$"*.{extension}"] }],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> OpenFileAsync(string title, string filterName, params string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(filterName) { Patterns = patterns },
                new FilePickerFileType("すべてのファイル") { Patterns = ["*"] },
            ],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}

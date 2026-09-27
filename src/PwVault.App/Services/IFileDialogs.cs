namespace PwVault.App.Services;

/// <summary>ファイル選択ダイアログ。実装はウィンドウ（StorageProvider）側に置き、ViewModel からは抽象だけを見る。</summary>
public interface IFileDialogs
{
    Task<string?> SaveFileAsync(string title, string suggestedFileName, string filterName, string extension);

    Task<string?> OpenFileAsync(string title, string filterName, params string[] patterns);
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PwVault.Core;
using PwVault.Core.Tools;

namespace PwVault.App.ViewModels;

/// <summary>S5 エントリ編集（FR-04, FR-05）。</summary>
public partial class EntryEditorViewModel : ViewModelBase
{
    private readonly VaultViewModel _owner;
    private readonly Guid? _id;
    private readonly EntryData _original;

    public EntryEditorViewModel(VaultViewModel owner, VaultEntry? entry)
    {
        _owner = owner;
        _id = entry?.Id;
        _original = entry?.Data.Clone() ?? new EntryData();
        EntryTitle = _original.Title;
        Username = _original.Username;
        Password = _original.Password;
        Url = _original.Url;
        Notes = _original.Notes;
        TagsText = string.Join(", ", _original.Tags);
        Favorite = _original.Favorite;
    }

    public bool IsNew => _id is null;
    public string Heading => IsNew ? "新しいエントリ" : "エントリを編集";

    [ObservableProperty]
    public partial string EntryTitle { get; set; }

    [ObservableProperty]
    public partial string Username { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StrengthText), nameof(StrengthValue))]
    public partial string Password { get; set; }

    [ObservableProperty]
    public partial string Url { get; set; }

    [ObservableProperty]
    public partial string Notes { get; set; }

    [ObservableProperty]
    public partial string TagsText { get; set; }

    [ObservableProperty]
    public partial bool Favorite { get; set; }

    [ObservableProperty]
    public partial bool RevealPassword { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public int StrengthValue => Password.Length == 0 ? 0 : (int)PasswordStrength.Evaluate(Password).Level + 1;

    public string StrengthText => Password.Length == 0 ? "" : "強度: " + PasswordStrength.Evaluate(Password).Label;

    [RelayCommand]
    private void ToggleReveal() => RevealPassword = !RevealPassword;

    [RelayCommand]
    private void Generate() => _owner.OpenGeneratorFor(pw =>
    {
        Password = pw;
        RevealPassword = true;
    });

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(EntryTitle) && string.IsNullOrWhiteSpace(Url))
        {
            Error = "タイトルか URL のどちらかは入力してください。";
            return;
        }

        var data = _original.Clone();
        data.Title = EntryTitle.Trim();
        data.Username = Username.Trim();
        data.Password = Password;
        data.Url = Url.Trim();
        data.Notes = Notes;
        data.Tags = TagsText.Split([',', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        data.Favorite = Favorite;

        _owner.CommitEditor(_id, data);
    }

    [RelayCommand]
    private void Cancel() => _owner.CancelEditor();
}

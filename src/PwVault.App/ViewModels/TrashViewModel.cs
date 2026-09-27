using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PwVault.App.ViewModels;

/// <summary>S9 ゴミ箱（FR-04）。復元と完全削除。</summary>
public partial class TrashViewModel : ViewModelBase
{
    private readonly VaultViewModel _owner;

    public TrashViewModel(VaultViewModel owner)
    {
        _owner = owner;
        Reload();
    }

    public ObservableCollection<TrashItem> Items { get; } = [];

    [ObservableProperty] public partial string? Status { get; set; }

    public bool IsEmpty => Items.Count == 0;

    private void Reload()
    {
        Items.Clear();
        foreach (var e in _owner.Vault.GetEntries().Where(e => e.Data.IsTrashed).OrderByDescending(e => e.Data.TrashedAt))
            Items.Add(new TrashItem(e.Id, e.Data.Title.Length > 0 ? e.Data.Title : "（無題）", e.Data.Username,
                e.Data.TrashedAt!.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm")));
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void Restore(TrashItem? item)
    {
        if (item is null) return;
        _owner.Vault.RestoreFromTrash(item.Id);
        Status = _owner.Persist() ? $"「{item.Title}」を元に戻しました。" : _owner.Status;
        Reload();
    }

    [RelayCommand]
    private async Task PurgeAsync(TrashItem? item)
    {
        if (item is null) return;
        if (!await _owner.Main.ConfirmAsync("完全に削除", $"「{item.Title}」を完全に削除します。元に戻せません。", "完全に削除", isDanger: true))
            return;
        _owner.Vault.Purge(item.Id);
        Status = _owner.Persist() ? $"「{item.Title}」を完全に削除しました。" : _owner.Status;
        Reload();
    }

    [RelayCommand]
    private async Task EmptyTrashAsync()
    {
        if (Items.Count == 0) return;
        if (!await _owner.Main.ConfirmAsync("ゴミ箱を空にする", $"{Items.Count} 件を完全に削除します。元に戻せません。", "空にする", isDanger: true))
            return;
        _owner.Vault.EmptyTrash();
        Status = _owner.Persist() ? "ゴミ箱を空にしました。" : _owner.Status;
        Reload();
    }

    [RelayCommand]
    private void Back() => _owner.CloseSubPage();
}

public sealed record TrashItem(Guid Id, string Title, string Username, string TrashedAt);

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PwVault.Core.Tools;

namespace PwVault.App.ViewModels;

/// <summary>S6 パスワード生成（FR-07）。条件を変えるたびに生成し直す。前回の条件は設定に保存する。</summary>
public partial class GeneratorViewModel : ViewModelBase
{
    private readonly VaultViewModel _owner;
    private readonly Action<string>? _accept;
    private bool _loading = true;

    public GeneratorViewModel(VaultViewModel owner, Action<string>? accept)
    {
        _owner = owner;
        _accept = accept;
        var o = owner.Main.Settings.Generator;
        Length = Math.Clamp(o.Length, GeneratorOptions.MinLength, GeneratorOptions.MaxLength);
        Uppercase = o.Uppercase;
        Lowercase = o.Lowercase;
        Digits = o.Digits;
        Symbols = o.Symbols;
        ExcludeAmbiguous = o.ExcludeAmbiguous;
        _loading = false;
        Regenerate();
    }

    public int MinLength => GeneratorOptions.MinLength;
    public int MaxLength => GeneratorOptions.MaxLength;
    public bool CanAccept => _accept is not null;

    [ObservableProperty]
    public partial double Length { get; set; }

    [ObservableProperty]
    public partial bool Uppercase { get; set; }

    [ObservableProperty]
    public partial bool Lowercase { get; set; }

    [ObservableProperty]
    public partial bool Digits { get; set; }

    [ObservableProperty]
    public partial bool Symbols { get; set; }

    [ObservableProperty]
    public partial bool ExcludeAmbiguous { get; set; }

    [ObservableProperty]
    public partial string Generated { get; set; } = "";

    [ObservableProperty]
    public partial string InfoText { get; set; } = "";

    partial void OnLengthChanged(double value) => Regenerate();
    partial void OnUppercaseChanged(bool value) => Regenerate();
    partial void OnLowercaseChanged(bool value) => Regenerate();
    partial void OnDigitsChanged(bool value) => Regenerate();
    partial void OnSymbolsChanged(bool value) => Regenerate();
    partial void OnExcludeAmbiguousChanged(bool value) => Regenerate();

    private GeneratorOptions Options => new()
    {
        Length = (int)Math.Round(Length),
        Uppercase = Uppercase,
        Lowercase = Lowercase,
        Digits = Digits,
        Symbols = Symbols,
        ExcludeAmbiguous = ExcludeAmbiguous,
    };

    [RelayCommand]
    private void Regenerate()
    {
        if (_loading) return;
        var options = Options;
        if (!options.Uppercase && !options.Lowercase && !options.Digits && !options.Symbols)
        {
            Generated = "";
            InfoText = "文字種を 1 つ以上選んでください。";
            return;
        }

        Generated = PasswordGenerator.Generate(options);
        InfoText = $"{options.Length} 文字・約 {PasswordGenerator.EstimateEntropyBits(options):0} ビット";
        _owner.Main.Settings.Generator = options;
    }

    [RelayCommand]
    private void Accept()
    {
        if (Generated.Length == 0) return;
        _owner.Main.SaveSettings();
        _accept?.Invoke(Generated);
        _owner.CloseGenerator();
    }

    [RelayCommand]
    private void Copy()
    {
        if (Generated.Length == 0) return;
        _owner.CopyToClipboard(Generated, "生成したパスワード");
    }

    [RelayCommand]
    private void Close()
    {
        _owner.Main.SaveSettings();
        _owner.CloseGenerator();
    }
}

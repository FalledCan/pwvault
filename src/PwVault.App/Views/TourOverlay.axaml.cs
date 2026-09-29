using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PwVault.App.ViewModels;

namespace PwVault.App.Views;

/// <summary>
/// チュートリアルの重ね表示。今のページが指す部品（<see cref="Tour.Id"/>）を画面から探し、
/// そこだけ明るく残して周りを暗くし、部品の上下どちらか空いている側に吹き出しを出す。
/// 部品の位置はページの切り替え・画面の大きさの変化・レイアウトの変化のたびに測り直す。
/// </summary>
public partial class TourOverlay : UserControl
{
    private const double Gap = 12, Pad = 6, Edge = 12;

    private TutorialViewModel? _vm;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Control? _broughtIntoView;

    public TourOverlay()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UpdatePlacement();
        KeyDown += OnKeyDown;
        Focusable = true;
    }

    /// <summary>いま照らしている部品（無ければ null。テストで確かめる）。</summary>
    public Control? CurrentTarget { get; private set; }

    /// <summary>照らしている範囲（この重ね表示の座標）。</summary>
    public Rect? Spotlight { get; private set; }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as TutorialViewModel;
        _broughtIntoView = null;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
            _timer.Start();
            Schedule();
            Dispatcher.UIThread.Post(() => this.FindControl<Button>("NextButton")?.Focus(), DispatcherPriority.Background);
        }
        else
        {
            _timer.Stop();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Schedule();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TutorialViewModel.Index))
        {
            _broughtIntoView = null;
            Schedule();
        }
    }

    // ページの準備（設定を開くなど）でレイアウトが変わった後に測る
    private void Schedule() => Dispatcher.UIThread.Post(UpdatePlacement, DispatcherPriority.Background);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        switch (e.Key)
        {
            case Key.Escape: _vm.CloseCommand.Execute(null); e.Handled = true; break;
            case Key.Right: _vm.NextCommand.Execute(null); e.Handled = true; break;
            case Key.Left: _vm.BackCommand.Execute(null); e.Handled = true; break;
        }
    }

    /// <summary>部品の位置を測り、暗幕の穴・枠・吹き出しの位置を合わせる。</summary>
    public void UpdatePlacement()
    {
        if (_vm is null || !IsEffectivelyVisible || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var size = Bounds.Size;
        var target = FindTarget(_vm.Current.Target);

        Rect? hole = null;
        if (target is not null)
        {
            // スクロールの中にある部品（設定のカードなど）は見える位置まで動かす（ページごとに 1 回）
            if (!ReferenceEquals(_broughtIntoView, target))
            {
                _broughtIntoView = target;
                target.BringIntoView();
            }
            if (target.TranslatePoint(new Point(0, 0), this) is { } p)
                hole = ClipToArea(new Rect(p, target.Bounds.Size).Inflate(Pad), size);
        }
        CurrentTarget = hole is null ? null : target;
        Spotlight = hole;

        var full = new RectangleGeometry(new Rect(size));
        Dim.Data = hole is { } h
            ? new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { full, new RectangleGeometry(h, 10, 10) } }
            : full;

        Ring.IsVisible = hole is not null;
        if (hole is { } r)
        {
            Canvas.SetLeft(Ring, r.X);
            Canvas.SetTop(Ring, r.Y);
            Ring.Width = r.Width;
            Ring.Height = r.Height;
        }

        Bubble.Measure(new Size(Bubble.Width, double.PositiveInfinity));
        var b = Bubble.DesiredSize;
        var (x, y) = PlaceBubble(hole, b, size);
        Canvas.SetLeft(Bubble, x);
        Canvas.SetTop(Bubble, y);
    }

    private Control? FindTarget(string? id)
    {
        if (id is null || TopLevel.GetTopLevel(this) is not { } top) return null;
        return top.GetVisualDescendants()
            .OfType<Control>()
            .Where(c => Tour.GetId(c) == id && c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0)
            .FirstOrDefault(c => !this.IsVisualAncestorOf(c));
    }

    private static Rect ClipToArea(Rect r, Size size)
    {
        var x = Math.Max(0, r.X);
        var y = Math.Max(0, r.Y);
        return new Rect(x, y, Math.Max(0, Math.Min(r.Right, size.Width) - x), Math.Max(0, Math.Min(r.Bottom, size.Height) - y));
    }

    /// <summary>吹き出しの位置。部品の下 → 上 → 右 → 左 の順に、入る所に置く。部品が無ければ中央。</summary>
    internal static (double X, double Y) PlaceBubble(Rect? hole, Size bubble, Size area)
    {
        double ClampX(double x) => Math.Clamp(x, Edge, Math.Max(Edge, area.Width - bubble.Width - Edge));
        double ClampY(double y) => Math.Clamp(y, Edge, Math.Max(Edge, area.Height - bubble.Height - Edge));

        if (hole is not { } h)
            return ((area.Width - bubble.Width) / 2, (area.Height - bubble.Height) / 2);

        var alignedX = ClampX(h.X + (h.Width - bubble.Width) / 2); // 部品の中央にそろえる（画面からはみ出さない範囲で）
        if (h.Bottom + Gap + bubble.Height <= area.Height - Edge)
            return (alignedX, h.Bottom + Gap);
        if (h.Top - Gap - bubble.Height >= Edge)
            return (alignedX, h.Top - Gap - bubble.Height);
        if (h.Right + Gap + bubble.Width <= area.Width - Edge)
            return (h.Right + Gap, ClampY(h.Y));
        if (h.Left - Gap - bubble.Width >= Edge)
            return (h.Left - Gap - bubble.Width, ClampY(h.Y));
        return (ClampX(h.X), ClampY(h.Bottom - bubble.Height)); // 大きな部品: 中に重ねる
    }
}

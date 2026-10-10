using Rectangle = System.Drawing.Rectangle;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Shapes = System.Windows.Shapes;

namespace ScreenTranslator.Capture;

public static class RegionSelector
{
    public static async Task<Rectangle?> SelectAsync()
    {
        var done = new TaskCompletionSource<Rectangle?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var windows = System.Windows.Forms.Screen.AllScreens.Select(screen => new SelectionWindow(screen.Bounds, done)).ToArray();
        foreach (var window in windows) window.Show();
        try { return await done.Task; }
        finally { foreach (var window in windows) window.Close(); }
    }

    /// <summary>One full-screen overlay per monitor: dims everything except the dragged box and shows its pixel size.</summary>
    internal sealed class SelectionWindow : Window
    {
        private const string IdleHint = "拖动框选要翻译的文字 · 同一块显示器内 · 右键或 Esc 取消";
        private static readonly SolidColorBrush Pink = Frozen(Color.FromRgb(244, 114, 182));
        private static readonly SolidColorBrush Deep = Frozen(Color.FromRgb(219, 39, 119));
        private static readonly SolidColorBrush Shade = Frozen(Color.FromArgb(120, 28, 10, 22));
        private static readonly SolidColorBrush Amber = Frozen(Color.FromRgb(252, 211, 77));
        private readonly Canvas _canvas = new();
        private readonly RectangleGeometry _frame = new();
        private readonly RectangleGeometry _hole = new(Rect.Empty);
        private readonly Shapes.Rectangle _box = new() { Stroke = Pink, StrokeThickness = 2, Fill = Frozen(Color.FromArgb(18, 244, 114, 182)), Visibility = Visibility.Collapsed };
        private readonly Shapes.Rectangle[] _handles = Enumerable.Range(0, 4).Select(_ => new Shapes.Rectangle { Width = 8, Height = 8, RadiusX = 2, RadiusY = 2, Fill = Brushes.White, Stroke = Deep, StrokeThickness = 1.5, Visibility = Visibility.Collapsed }).ToArray();
        private readonly TextBlock _size = new() { Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold };
        private readonly Border _sizeTag;
        private readonly TextBlock _hintGlyph = new() { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 17, Foreground = Pink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        private readonly TextBlock _hint = new() { Text = IdleHint, Foreground = Brushes.White, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        private readonly Border _hintCard;
        private readonly Rectangle _screen;
        private readonly TaskCompletionSource<Rectangle?> _done;
        private System.Windows.Point? _start;

        public SelectionWindow(Rectangle screen, TaskCompletionSource<Rectangle?> done)
        {
            _screen = screen;
            _done = done;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            // Alpha 1 keeps the undimmed hole clickable; fully transparent pixels of a layered window pass clicks through.
            Background = Frozen(Color.FromArgb(1, 0, 0, 0));
            Topmost = true;
            ShowInTaskbar = false;
            UseLayoutRounding = true;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            Cursor = Cursors.Cross;

            _sizeTag = new Border { Background = Deep, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 4), Child = _size, Visibility = Visibility.Collapsed };
            _hintCard = new Border
            {
                Background = Frozen(Color.FromArgb(235, 45, 22, 38)), BorderBrush = Frozen(Color.FromArgb(90, 244, 114, 182)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(20), Padding = new Thickness(20, 10, 22, 11), Margin = new Thickness(0, 36, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _hintGlyph, _hint } },
            };
            var dim = new Shapes.Path { Fill = Shade, IsHitTestVisible = false, Data = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { _frame, _hole } } };
            _canvas.Children.Add(_box);
            foreach (var handle in _handles) _canvas.Children.Add(handle);
            _canvas.Children.Add(_sizeTag);
            Content = new Grid { Children = { dim, _canvas, _hintCard } };
            SizeChanged += (_, _) => _frame.Rect = new Rect(RenderSize);

            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                SetWindowPos(hwnd, new IntPtr(-1), screen.X, screen.Y, screen.Width, screen.Height, 0x0040);
            };
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) _done.TrySetResult(null); };
            Closing += (_, _) => _done.TrySetResult(null);
            MouseRightButtonUp += (_, _) => _done.TrySetResult(null);
            MouseLeftButtonDown += (_, e) => { _start = e.GetPosition(_canvas); CaptureMouse(); _hintCard.Opacity = 0.35; };
            MouseMove += (_, e) =>
            {
                if (_start is not { } start) return;
                var end = e.GetPosition(_canvas);
                ShowBox(new Rect(start, end), start, end);
            };
            MouseLeftButtonUp += (_, e) =>
            {
                if (_start is not { } start) return;
                ReleaseMouseCapture();
                var end = e.GetPosition(_canvas);
                var firstPixel = PointToScreen(start);
                var lastPixel = PointToScreen(end);
                var region = Rectangle.FromLTRB((int)Math.Floor(Math.Min(firstPixel.X, lastPixel.X)), (int)Math.Floor(Math.Min(firstPixel.Y, lastPixel.Y)), (int)Math.Ceiling(Math.Max(firstPixel.X, lastPixel.X)), (int)Math.Ceiling(Math.Max(firstPixel.Y, lastPixel.Y)));
                if (region.Width >= 8 && region.Height >= 8 && _screen.Contains(region)) _done.TrySetResult(region);
                else
                {
                    _start = null; ShowBox(Rect.Empty, start, end); _hintCard.Opacity = 1;
                    _hint.Text = region.Width < 8 || region.Height < 8 ? "选区太小，请重新拖动框选 · 右键或 Esc 取消" : "第一版不支持跨屏选区，请在同一块显示器内重选 · 右键或 Esc 取消";
                    _hintGlyph.Text = ""; _hintGlyph.Foreground = Amber;
                }
            };
        }

        /// <summary>Draws a fixed selection without the mouse, for the rendered UI verification preview.</summary>
        internal void ShowSample(Rect box) => ShowBox(box, box.TopLeft, box.BottomRight);

        private void ShowBox(Rect box, System.Windows.Point start, System.Windows.Point end)
        {
            _hole.Rect = box;
            var visible = box.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
            _box.Visibility = _sizeTag.Visibility = visible;
            foreach (var handle in _handles) handle.Visibility = visible;
            if (box.IsEmpty) return;
            Canvas.SetLeft(_box, box.X); Canvas.SetTop(_box, box.Y);
            _box.Width = box.Width; _box.Height = box.Height;
            var corners = new[] { box.TopLeft, box.TopRight, box.BottomRight, box.BottomLeft };
            for (var i = 0; i < 4; i++) { Canvas.SetLeft(_handles[i], corners[i].X - 4); Canvas.SetTop(_handles[i], corners[i].Y - 4); }
            // The label reports physical pixels, the same unit as the main window and the 16-megapixel limit.
            var first = PointToScreen(start); var last = PointToScreen(end);
            _size.Text = $"{Math.Abs(Math.Round(last.X) - Math.Round(first.X)):0} × {Math.Abs(Math.Round(last.Y) - Math.Round(first.Y)):0}";
            _sizeTag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var above = box.Y - _sizeTag.DesiredSize.Height - 8;
            Canvas.SetLeft(_sizeTag, box.X);
            Canvas.SetTop(_sizeTag, above >= 4 ? above : box.Bottom + 8);
        }

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    }
}

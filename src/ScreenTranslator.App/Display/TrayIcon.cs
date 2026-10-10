using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ScreenTranslator.Display;

internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new();
    public TrayIcon(Dispatcher dispatcher, Action showMain, Action showFloating, Action toggle, Action exit)
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/ScreenTranslator;component/Assets/AppIcon.ico"))!.Stream;
        _icon = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(stream), Text = "屏幕 AI 翻译 · 小窗可独立运行", ContextMenuStrip = _menu, Visible = true };
        Add("打开主界面", showMain); Add("显示翻译小窗", showFloating); Add("开始 / 暂停翻译", toggle);
        _menu.Items.Add(new Forms.ToolStripSeparator()); Add("退出软件", exit);
        _icon.DoubleClick += (_, _) => dispatcher.InvokeAsync(showMain);
        void Add(string text, Action action) => _menu.Items.Add(text, null, (_, _) => dispatcher.InvokeAsync(action));
    }
    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Icon?.Dispose(); _icon.Dispose(); _menu.Dispose();
    }
}

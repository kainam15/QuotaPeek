using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace QuotaPeek.UI;

public partial class TaskbarCapsuleView : UserControl
{
    public event Action? ToggleRequested;
    public event Action? UndockRequested;
    public event Action? SettingsRequested;
    public event Action? RefreshRequested;
    public event Action? ExitRequested;

    public TaskbarCapsuleView() => InitializeComponent();

    public void Update(string name, string value, Brush status, string tooltip)
    {
        ProviderText.Text = name;
        BalanceText.Text = value;
        StatusDot.Fill = status;
        ToolTip = tooltip + "\n滚轮切换钱包 / 额度 · 单击展开 / 收起 · 右键打开菜单";
    }

    private void Expand_Click(object sender, RoutedEventArgs e) => ToggleRequested?.Invoke();
    private void Undock_Click(object sender, RoutedEventArgs e) => UndockRequested?.Invoke();
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
    private void Exit_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();
    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        var menu = ((Border)Content).ContextMenu;
        menu.PlacementTarget = TaskbarMenuButton;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }
}

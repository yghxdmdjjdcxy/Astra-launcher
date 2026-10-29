using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AstraLauncher.Game.Diagnostics;

namespace AstraLauncher.App.Services;

public enum CrashRepairChoice
{
    Dismiss,
    AutoRepair,
    Manual,
}

/// <summary>Shows the crash analysis result and asks whether to auto-repair or handle it manually.</summary>
public interface ICrashDialogService
{
    CrashRepairChoice Show(CrashAnalysisResult analysis);
}

/// <summary>
/// Small code-built WPF dialog: title, detected reasons, suggestions and three actions. Auto-repair is only offered when
/// the analysis contains reversible fixes.
/// </summary>
public sealed class CrashAnalysisDialogService : ICrashDialogService
{
    public CrashRepairChoice Show(CrashAnalysisResult analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var choice = CrashRepairChoice.Dismiss;
        var window = new Window
        {
            Title = "游戏崩溃分析 - Astra Launcher",
            Width = 780,
            Height = 600,
            MinWidth = 620,
            MinHeight = 420,
            Owner = Application.Current?.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
        };

        var root = new DockPanel { Margin = new Thickness(20) };
        var header = new TextBlock { Text = "游戏崩溃分析", FontSize = 22, FontWeight = FontWeights.Bold };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var summary = new TextBlock
        {
            Text = analysis.Summary,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        };
        DockPanel.SetDock(summary, Dock.Top);
        root.Children.Add(summary);

        var note = new TextBlock
        {
            Text = "自动修复只会执行可逆操作：下载缺失的前置 MOD、禁用可疑/重复的 MOD、调大内存。修复前不会删除任何文件。",
            Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        DockPanel.SetDock(note, Dock.Top);
        root.Children.Add(note);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        Button CreateButton(string text, bool primary = false)
        {
            var button = new Button
            {
                Content = text,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(14, 6, 14, 6),
                Background = primary ? new SolidColorBrush(Color.FromRgb(37, 99, 235)) : Brushes.White,
                Foreground = primary ? Brushes.White : new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
            };
            buttons.Children.Add(button);
            return button;
        }

        Button openButton = CreateButton("查看日志/崩溃报告");
        openButton.Click += (_, _) => OpenEvidence(analysis);
        Button manualButton = CreateButton("手动处理");
        manualButton.Click += (_, _) =>
        {
            choice = CrashRepairChoice.Manual;
            window.Close();
        };
        Button repairButton = CreateButton("自动修复", primary: true);
        repairButton.IsEnabled = analysis.CanAutoRepair;
        repairButton.Click += (_, _) =>
        {
            choice = CrashRepairChoice.AutoRepair;
            window.Close();
        };
        Button closeButton = CreateButton("关闭");
        closeButton.Click += (_, _) =>
        {
            choice = CrashRepairChoice.Dismiss;
            window.Close();
        };

        var issuesPanel = new StackPanel();
        foreach (CrashIssue issue in analysis.Issues)
        {
            var border = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 8),
            };
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = issue.Title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            content.Children.Add(new TextBlock { Text = issue.Details, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            content.Children.Add(new TextBlock
            {
                Text = "建议：" + issue.Suggestion,
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
            border.Child = content;
            issuesPanel.Children.Add(border);
        }

        var scroll = new ScrollViewer
        {
            Content = issuesPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        root.Children.Add(scroll);
        window.Content = root;
        window.ShowDialog();
        return choice;
    }

    private static void OpenEvidence(CrashAnalysisResult analysis)
    {
        try
        {
            string? path = analysis.CrashReportPath ?? analysis.LatestLogPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else if (!string.IsNullOrWhiteSpace(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"") { UseShellExecute = true });
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Opening the folder is a convenience only; the dialog must not fail because of it.
        }
    }
}

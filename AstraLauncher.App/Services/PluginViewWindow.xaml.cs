using System.Windows;

namespace AstraLauncher.App.Services;

/// <summary>Hosts a view created by an <c>IPluginPageContribution</c>.</summary>
public partial class PluginViewWindow : Window
{
    public PluginViewWindow(string title, object content)
    {
        InitializeComponent();
        Title = title;
        HeaderText.Text = title;
        PageContent.Content = content;
    }
}

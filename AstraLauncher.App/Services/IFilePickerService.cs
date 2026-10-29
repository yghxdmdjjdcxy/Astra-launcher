using Microsoft.Win32;

namespace AstraLauncher.App.Services;

/// <summary>Lets the user pick local mod jars. Kept behind an interface so view models stay testable.</summary>
public interface IFilePickerService
{
    IReadOnlyList<string> PickModFiles();
}

public sealed class OpenFileDialogService : IFilePickerService
{
    public IReadOnlyList<string> PickModFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要导入的 MOD 文件",
            Filter = "Minecraft MOD (*.jar)|*.jar|所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }
}

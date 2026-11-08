# Astra Launcher 插件开发与使用

插件系统用于在不发布新启动器版本的情况下扩展功能。

## 安装

1. 启动器左侧进入「插件」。
2. 点「安装插件 ZIP」，选择包含 `plugin.json` 的压缩包。
3. 重启启动器使插件生效。
4. 可以随时启用 / 禁用或删除；这些操作同样在重启后生效。

插件会安装到：

```text
<启动器数据目录>\plugins\<plugin-id>\
```

开发构建的数据目录通常是：

```text
...\AstraLauncher.App\bin\Release\net10.0-windows\.minecraft\AstraLauncher\plugins
```

发布版位于 `%LOCALAPPDATA%\AstraLauncher\plugins`（具体以「插件」页面显示的路径为准）。

## 插件包格式

```text
my-plugin.zip
├─ plugin.json
├─ MyPlugin.dll
└─ 其他依赖 DLL
```

`plugin.json`：

```json
{
  "id": "com.example.myplugin",
  "name": "我的插件",
  "version": "1.0.0",
  "description": "这个插件做什么",
  "author": "你的名字",
  "entryAssembly": "MyPlugin.dll",
  "apiVersion": 1,
  "enabled": true,
  "sha256": "入口 DLL 的可选 SHA-256"
}
```

- `id`：只允许字母、数字、`.`、`_`、`-`，最长 64 字符。
- `apiVersion`：当前必须为 `1`。
- `entryAssembly`：相对插件目录的入口 DLL。
- `sha256`：填写后加载器会校验入口 DLL。

## 最小插件

项目引用：

```xml
<ProjectReference Include="..\AstraLauncher.Plugin.Abstractions\AstraLauncher.Plugin.Abstractions.csproj" />
```

```csharp
using AstraLauncher.Plugin.Abstractions;

public sealed class MyPlugin : ILauncherPlugin
{
    public string Id => "com.example.myplugin";

    public void Configure(IPluginContext context)
    {
        context.Services.AddSingleton<ILaunchHook, MyLaunchHook>();
    }
}

public sealed class MyLaunchHook : ILaunchHook
{
    public Task<PluginLaunchRequest> BeforeBuildAsync(
        PluginLaunchRequest request,
        CancellationToken cancellationToken)
    {
        // 可以修改 Java 路径、内存、额外参数或服务器地址。
        return Task.FromResult(request);
    }
}
```

## 扩展点（API v1）

| 扩展点 | 用途 |
|---|---|
| `ILauncherPlugin` | 插件入口；在容器构建前注册服务 |
| `ILaunchHook` | 启动游戏前修改 JVM/游戏参数、内存、服务器 |
| `ILauncherEvents` | 进程内类型化事件总线 |
| `IPluginContext.Services` | 正常注册宿主 DI 服务 |
| `IPluginPageContribution` | 在「插件」页提供插件自己的页面 |

### 插件页面示例

```csharp
public sealed class MyPage : IPluginPageContribution
{
    public string Id => "dashboard";
    public string Title => "我的插件";
    public string Description => "显示插件自己的状态";
    public object CreateView() => new MyPageView(); // WPF UserControl
}
```

然后在 `Configure` 中注册：

```csharp
context.Services.AddSingleton<IPluginPageContribution, MyPage>();
```

## 安全边界

- 插件是 **受信任的原生 .NET 程序**，不是沙箱脚本。
- 插件以启动器进程权限运行，可以访问文件、网络和启动参数。
- 只安装来源可信的插件；发布者应提供 `sha256`。
- 启动器会拒绝路径越界的入口 DLL、API 版本不匹配的插件，并在单个插件失败时跳过它，不影响启动器启动。

## 构建示例插件

```powershell
dotnet build .\AstraLauncher.SamplePlugin\AstraLauncher.SamplePlugin.csproj -c Release
```

把 `AstraLauncher.SamplePlugin\plugin.json` 和 `AstraLauncher.SamplePlugin\bin\Release\net10.0\AstraLauncher.SamplePlugin.dll` 放进同一目录，压缩为 ZIP 后即可通过「插件」页安装。

## 限制

- 插件加载发生在启动器启动时，安装 / 启停 / 删除后需要重启。
- 插件 API 版本不匹配会被拒绝；升级 API 需要新的启动器版本。
- 当前没有在线插件市场；社区可以通过 Releases、群文件或自己的网站分发 ZIP。

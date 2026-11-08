# AstraLauncher.Multiplayer

基于 [EasyTier](https://github.com/EasyTier/EasyTier) 的 Minecraft Java 版联机。

## 当前方案

- **默认且当前版本仅启用 NoTun 模式**：不创建虚拟网卡、不需要管理员权限。
- 房主固定虚拟 IP（默认 `10.77.0.1`），并在本机启动 Minecraft 服务端（默认 25565）。
- 访客通过 EasyTier `--port-forward` 把本机 `127.0.0.1:25566` 转发到房主虚拟 IP 的 25565。
- 启动器通过 `EasyTierLaunchHook` 在启动游戏时自动带上 `--server/--port`，因此选中实例后可直接加入房间。
- 邀请码以 `ASTRA1.` 开头，包含房间名、密钥、房主虚拟 IP 和共享节点，方便复制发送。

## 为什么暂不开放完整 TUN

完整 TUN 需要以管理员权限启动 `easytier-core`。当前启动器是普通权限进程，退出时无法可靠终止这个高权限子进程，可能留下孤儿进程。因此在正式版中：

- UI 中的「完整 TUN 模式」被禁用；
- `EasyTierService.StartAsync` 会拒绝 `EasyTierMode.FullTun`；
- 后续会通过独立提权 helper / Windows 服务方式重新开放。

## 依赖与安全

- EasyTier 官方 v2.6.4 Windows x86_64 ZIP；
- 自动安装后会校验 ZIP 文件大小，避免解压被截断的下载；
- 建议部署前再增加官方 SHA256 固定校验。

## 测试

`AstraLauncher.Tests/MultiplayerTests.cs` 覆盖：

- 邀请码编解码与非法输入；
- 房主/访客 TOML 生成；
- 端口转发 CLI 参数；
- peer JSON 解析；
- 启动 Hook 的条件行为；
- ZIP Slip 防护。

注意：这些是纯逻辑测试，**真实双机 EasyTier + Minecraft 服务端链路仍需人工验证**。

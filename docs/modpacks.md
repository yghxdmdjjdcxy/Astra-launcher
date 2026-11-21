# 整合包导入（.mrpack）

Astra Launcher 1.1.0 起支持导入 Modrinth 格式的 `.mrpack` 整合包。

## 使用

1. 打开「游戏实例」。
2. 点「导入整合包」，选择 `.mrpack` 文件。
3. 启动器会自动：
   - 读取 `modrinth.index.json`
   - 安装对应 Minecraft 版本
   - 安装 Fabric / Quilt / Forge / NeoForge（如果整合包声明）
   - 创建独立实例
   - 下载 `files` 中的模组与资源（带 SHA-1 校验）
   - 解压 `overrides` / `client-overrides` 到实例目录
4. 导入完成后直接启动实例。

## 支持范围

- `dependencies.minecraft`：必需
- `dependencies.fabric-loader`
- `dependencies.quilt-loader`
- `dependencies.forge`
- `dependencies.neoforge`
- `files[].downloads`：使用第一个 HTTP(S) 地址
- `files[].hashes.sha1`：可选；存在时由下载器校验
- `files[].env.client == "unsupported"`：跳过
- `overrides/` 和 `client-overrides/`

## 安全

- ZIP 解压拒绝路径越界条目。
- `files[].path` 拒绝绝对路径、`.`、`..`。
- 下载文件路径必须位于实例目录内。
- 导入失败时会删除本次创建的半成品实例。

## 限制

- 只支持 Modrinth `.mrpack`，暂不支持 CurseForge `.zip` 整合包。
- 整合包声明的加载器版本必须能被现有加载器安装器处理。
- 下载地址必须可访问；未提供下载地址的条目会被跳过。
- 大整合包首次导入需要较长时间，进度显示在实例页状态栏。

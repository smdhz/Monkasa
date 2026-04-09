# Monkasa

Monkasa 是一个仿造 Picasa 的轻量看图程序，提供目录树浏览、缩略图网格和全屏查看等基础功能。

## 声明

- 本程序是为了对 Picasa 贼心不死的朋友们。
- 代码基本纯通过 AI 生成。
- 作者比较懒，很多地方没有再手工精修，以能用为原则。
- 本程序具备强大的跨操作系统能力，至少理论上是这样（Windows / macOS / Linux）。

## 运行前准备

- 普通运行：安装 `.NET 10 Runtime`。
- 开发构建（`dotnet build` / `dotnet run`）：安装 `.NET 10 SDK`。
- 安装后可用 `dotnet --info` 确认环境。

Windows（winget）：

```bash
winget install Microsoft.DotNet.Runtime.10
```

macOS（Homebrew）：

```bash
brew install --cask dotnet-runtime
```

Linux（Ubuntu，APT）：

```bash
sudo apt-get update
sudo apt-get install -y dotnet-runtime-10.0
```

开发者额外安装 SDK：

```bash
# Windows
winget install Microsoft.DotNet.SDK.10

# macOS
brew install --cask dotnet-sdk

# Ubuntu
sudo apt-get install -y dotnet-sdk-10.0
```

提示：Ubuntu 上可用版本和仓库配置可能因系统版本而异，请以微软官方文档为准。

## License

- MIT, Copyright (c) 2026 Monkeysoft

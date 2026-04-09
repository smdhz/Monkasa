# AGENTS

## 项目机能（简要）

- 仿 Picasa 的本地看图工具。
- 默认打开用户主目录（`~/`）。
- 左侧目录树浏览文件夹（支持展开/收起）。
- 中间区域展示图片缩略图网格。
- 顶部提供 `⇵` 排序菜单，可按名称/时间排序，并用 `✔` 标记当前排序方式。
- 双击缩略图进入全窗口查看。
- 查看模式支持键盘 `← / →` 切换上一张/下一张。
- `Esc` 或点击黑色遮罩空白处可退出查看模式。
- 使用 SQLite 缓存缩略图，减少重复解码耗时。

## 主要模块

- `ViewModels/MainWindowViewModel.cs`：核心状态与交互逻辑（目录、图片、排序、查看器导航）。
- `Services/FileSystemService.cs`：目录/图片扫描。
- `Services/ThumbnailService.cs`：缩略图与预览图生成。
- `Services/DbStorageService.cs`：SQLite 缩略图缓存读写。
- `Views/MainWindow.axaml`：主界面布局与绑定。

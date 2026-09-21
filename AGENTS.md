# AGENTS

## 项目机能（简要）

- 仿 Picasa 的本地看图工具。
- 默认打开用户主目录（`~/`）。
- 左侧目录树浏览文件夹（支持展开/收起和收藏目录）。
- 中间区域展示图片缩略图网格。
- 顶部提供排序选择，可按名称/时间排序。
- 双击缩略图进入全窗口查看。
- 查看模式支持键盘 `← / →` 切换上一张/下一张。
- `Esc` 或点击黑色遮罩空白处可退出查看模式。
- 查看模式支持鼠标滚轮缩放和拖动图片。
- 使用 SQLite 缓存缩略图，减少重复解码耗时。
- 监听当前目录的文件变化，对图片集合进行增量刷新，避免每次清空并重建整个列表。
- 发行包采用自包含方式，不要在用户文档中要求额外安装 .NET Runtime。

## 主要模块

- `ViewModels/MainWindowViewModel.cs`：窗口公共状态、初始化、顶层命令和生命周期。
- `ViewModels/MainWindowViewModel.Directories.cs`：目录树、收藏目录、目录删除和路径状态。
- `ViewModels/MainWindowViewModel.Gallery.cs`：图片集合、增量刷新、排序和缩略图加载。
- `ViewModels/MainWindowViewModel.Viewer.cs`：查看器打开、关闭、导航和预览图加载。
- `Services/ImageCatalogPlanner.cs`：纯逻辑的图片差异计算与排序，应保持可独立测试。
- `Services/FileSystemService.cs`：目录/图片扫描和当前目录文件变化监听。
- `Services/ThumbnailService.cs`：缩略图与预览图生成。
- `Services/DbStorageService.cs`：SQLite 缩略图缓存读写。
- `Views/MainWindow.axaml`：主界面布局与绑定。
- `Views/ImageViewer.axaml.cs`：查看器缩放、定位和拖动交互。
- `Monkasa.Tests`：增量差异计算与排序的单元测试。

## 开发约定

- 目录监听刷新必须保留未变化的 `ImageItemViewModel`，尽量保留当前选中项和查看器状态。
- 不要把目录、图库和查看器逻辑重新集中到 `MainWindowViewModel.cs`；按现有 partial 文件的职责放置代码。
- 新增或修改图片差异、排序等纯逻辑时，同步补充 `Monkasa.Tests`。
- 交付前运行 `dotnet build Monkasa.sln --no-restore -m:1` 和 `dotnet test Monkasa.Tests/Monkasa.Tests.csproj --no-build --no-restore`。

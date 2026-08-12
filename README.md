# CF Rez Manager

English documentation is available in [README.en.md](README.en.md).

![主界面](assets/readme/image.png)
![预览界面](assets/readme/image-1.png)

CF Rez Manager 是一个 Windows WPF 工具，用来浏览、搜索、预览、解包和重新打包 LithTech / CrossFire 的 `.rez` 资源包，也能直接查看已解包目录里的散文件资源。

## 能做什么

- 浏览 `.rez` 包、REZ 内部目录和普通资源文件夹。
- 搜索文件、目录和 REZ 内部路径，支持多关键词筛选。
- 批量导出全部资源，也可以只导出选中的文件、目录或 REZ 项。
- 将普通 Windows 文件夹重新打包为 `.rez`。
- 预览图片、纹理、音频、模型、地图、脚本配置和多种 CrossFire/LithTech 资源。
- 按格式选择将 BIN、DTX、TGA、DDS 等可解码图片资源保留为源文件或导出为标准 `.png`。
- 提供 OBJ/MTL 模型导出、CFG 扫描和 CFG 解码等命令行批处理入口。

## 环境要求

- Windows
- .NET 8 SDK 或 .NET 8 Runtime

## 构建和运行

```powershell
dotnet build .\CFRezManager.csproj
```

可以从 Visual Studio 运行，也可以启动构建后的程序：

```text
bin\Debug\net8.0-windows7.0\CFRezManager.exe
```

## 目录结构

```text
CFRezManager/
|-- App.xaml
|-- App/
|-- Archives/
|-- Commands/
|-- Decoders/
|   |-- Audio/
|   |-- Compression/
|   |-- Config/
|   |-- CrossFire/
|   |-- Fmod/
|   |-- Images/
|   |-- LithTech/
|   |   `-- Models/
|   `-- Text/
|-- Explorer/
|-- Preview/
|   |-- Audio/
|   |-- Image/
|   |-- Model/
|   `-- Text/
|-- UI/
|-- assets/
|-- CFRezManager.csproj
`-- CFRezManager.sln
```

- `App/`：应用启动辅助、设置、本地化和程序集信息。
- `Archives/`：REZ 读取、写入和目录表加密逻辑。
- `Commands/`：OBJ 导出、CFG 扫描/解码、独立预览等命令行入口。
- `Decoders/`：各类资源解码器，按音频、图片、CrossFire、LithTech、文本等类型分组。
- `Explorer/`：资源浏览项目模型和缩略图缓存。
- `Preview/`：音频、图片、模型、文本的独立预览窗口。
- `UI/`：主窗口和界面控件。
- `assets/`：应用图标和随程序复制的图片资源。

## 基本使用

1. 启动程序。
2. 选择包含 `.rez` 文件或散文件资源的文件夹。
3. 双击文件夹、REZ 包、REZ 内部目录或支持预览的文件。
4. 用顶部面包屑返回上级或跳转到任意父级位置。

点击顶部 `设置...` 可打开设置窗口，在其中切换 `中文` / `English`、`亮色` / `暗色` 主题，配置图片导出格式，并清理缩略图缓存。程序会记住语言、主题、视图大小、扫描目录、打包目录、导出目录、保存位置和图片导出选项。

搜索框首次输入时会建立内存索引，之后可快速筛选已扫描到的文件、目录和 REZ 内部路径。多个关键词用空格分隔时，需要全部命中才会显示结果。

右下角 `大小` 滑条用于切换列表视图和平铺图标视图。鼠标悬停在项目上时，会显示类型、路径、大小、来源、MD5、偏移等信息。

右键菜单常用操作：

- `定位到文件`：从搜索结果跳回文件所在目录并选中它。
- `复制名称`：复制单个或多个选中项名称。
- `导出此项...` / `导出 N 个选中项...`：导出选中的文件、目录或 REZ 项。
- `解码 BANK...`：导出 decoded bank 和原始 FSB5 音频块。

## 预览能力

- 图片和纹理：PNG、JPG、BMP、GIF、TIFF、DDS、TGA、DTX、CrossFire 图片 BIN，支持原始尺寸预览和上一张/下一张切换。
- 压缩资源：支持常见 LZMA 外壳资源，缩略图角标会标出 `RAW`、`LZMA`、`DXT`、`TXT` 等状态。
- 音频：WAV、OGG、MP3 和 FMOD `.bank`，支持波形缩略图、曲目列表、播放控制、进度拖动和动态频谱；OGG/MP3 预览会解码为 PCM 后生成频谱，让普通音频和 FMOD BANK 的波形表现更一致。
- 模型和地图：LTC、LTB、LTA、DAT、SPR，可生成缩略图并打开独立预览窗口；SPR 可自动播放动画帧。
- 文本和配置：CFT、FCF、FXF、FXO、NAV、APF、REF、TXT、部分 WAVE 资源、CrossFire UI 脚本 `.bin`、CFG。
- CFG 批处理：可扫描贴图引用，支持普通/LZMA/ENC/REZ phase 文本 CFG 解码，分类失败解码结果，并为二进制 RGB 条带型 CFG 生成预览。

生成过的缩略图会缓存在程序目录下的 `ThumbnailCache` 文件夹中，避免持续占用当前 Windows 用户目录所在磁盘。程序启动时会尝试删除旧版用户目录缩略图缓存；资源变化后可在 `设置` 中用 `清缩略图` 清理当前缓存。

## 模型预览操作

- 鼠标左键点击模型窗口：进入自由视角。
- 鼠标移动：调整视角方向。
- `W` / `A` / `S` / `D`：前后左右移动。
- `Shift`：加速移动。
- 鼠标滚轮：沿当前视线方向前进或后退。
- 鼠标右键或 `Esc`：退出自由视角。
- `Reset View`：重置相机位置和方向。

## 解包资源

点击 `全部导出...` 可导出当前扫描范围内所有 REZ 包中的文件。

只导出指定项目：

1. 选中文件、文件夹、REZ 包或 REZ 内部目录。
2. 需要多选时按住 `Ctrl` 或 `Shift`。
3. 右键选中项。
4. 选择 `导出此项...` 或 `导出 N 个选中项...`。
5. 在导出格式窗口中为 BIN、DTX、TGA 和 DDS 分别选择保留源文件或导出解码后的 PNG。
6. 选择输出文件夹。

导出格式选择会自动保存。勾选 `下次导出不再显示此窗口` 后，后续导出会直接使用已保存的配置；需要修改时可从 `设置` → `导出格式设置...` 重新打开。导出的文件会保留 REZ 内部目录结构；无法识别为图片的 BIN（例如脚本或配置表）以及解码失败的文件会保留原扩展名和源数据。

## 打包为 REZ

点击 `打包文件夹...` 可把普通 Windows 文件夹打包为新的 `.rez` 文件。

1. 准备一个包含目标文件和子目录的文件夹。
2. 点击 `打包文件夹...`。
3. 选择源文件夹。
4. 选择输出 `.rez` 路径。

说明：

- 文件数据在 REZ 中直接存放。
- REZ 目录表会被加密，文件 MD5 会重新计算。
- 文件名和目录名目前要求为 ASCII。
- 文件扩展名需要为 1 到 4 个字符。
- 新包会保留内容和目录结构，但不会复制原包的字节级布局、偏移、时间戳或整包 MD5。

## 命令行工具

```powershell
dotnet run --project .\CFRezManager.csproj -- --export-obj --root "F:\Game\CrossFire" --model "PV-AK47_Balance" --output ".\out\PV-AK47_Balance.obj"
dotnet run --project .\CFRezManager.csproj -- --scan-cfg --root "C:\Extracted\cfg"
dotnet run --project .\CFRezManager.csproj -- --decode-cfg --root "C:\Extracted\cfg"
```

- `--export-obj`：导出 LithTech 模型为 OBJ/MTL，同步写出 `_textures` 贴图目录并在 MTL 中引用贴图；仅在缺失贴图时生成诊断报告。
- `--scan-cfg`：扫描 CFG，提取贴图引用，输出 TXT/CSV 报告。
- `--decode-cfg`：重试失败 CFG，导出可还原文本或二进制预览图，并分类高熵配置。

## v1.2.4 更新

- 新增图片导出格式设置窗口，可分别为 CrossFire 图片 BIN、DTX、TGA 和 DDS 选择保留源文件或导出解码后的 PNG。
- 导出格式选择会保存到用户设置，并支持 `下次导出不再显示此窗口`；需要修改时可从 `设置` → `导出格式设置...` 重新打开。
- 批量导出时，如果文件不适用图片解码或解码失败，会自动回退保留原扩展名和源数据。
- 导出完成状态会显示成功解码的图片数量，以及回退保留源文件的数量。
- 更新中英文说明书和 GitHub Release 文案，版本号提升到 `v1.2.4`。

## v1.2.3 更新

- 在“内容”标题栏右侧新增类型过滤下拉，可筛选文件夹、图片、模型、音频、文本和其他资源；过滤同时适用于当前目录与全局搜索结果。
- 新增 REZ 目录索引磁盘缓存，并通过源路径、文件大小和修改时间自动校验；重复打开未变化的资源包时无需重新解析完整目录树。
- OBJ 导出新增 DAT 贴图引用索引，可从普通或 LZMA 数据中提取贴图路径，并与现有 CFG 配置索引合并解析。
- 改进 `SGFX_*` 模型家族和分件识别，支持 `MASK`、`LEFT`、`RIGHT`、`CIRCLE`、`LINE`、`PLANE*` 等末端分件名称。
- SGFX 模型导出时会继续查找并导出 `_GLOW`、`_GLOW_01`、`_GLOW_02` 辅助贴图。
- 图形界面和 `--export-obj` 命令行导出均已接入新的组合贴图解析流程。

## 感谢以下伙伴提供项目反馈和帮助

![项目贡献者：TigerShota、风吹、ka9ura](assets/readme/contributors.gif)

## 感谢以下朋友的支持

![项目支持者：黑猫不是警长 20、KissJoJo 100、Saya 66、¹ 1](assets/readme/supporters-smooth.gif)

### 如果这个工具帮到了你，可以请我喝杯咖啡

<p align="center"><img src="assets/readme/afc08a3298aeb1fa378e9d89ca34e35a.jpg" alt="支持项目二维码"></p>

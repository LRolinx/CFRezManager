# CF Rez Manager

English documentation is available in [README.en.md](README.en.md).

![主界面](assets/readme/image.png)
![预览界面](assets/readme/image-1.png)
![blender](assets/readme/image-3.png)

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
dotnet run --project .\CFRezManager.csproj -- --audit-texture-map --root "F:\Game\CrossFire" --output ".\out\crossfire_texture_mapping_audit.tsv"
dotnet run --project .\CFRezManager.csproj -- --audit-table-decode --root "F:\Game\CrossFire" --output ".\out\table_decode.tsv"
dotnet run --project .\CFRezManager.csproj -- --inspect-ltb-header --root "F:\Game\CrossFire" --model "Models/CHARACTER/NINJATRANSLUCENT"
dotnet run --project .\CFRezManager.csproj -- --inspect-ltb-header --root "F:\Game\CrossFire" --all-renderstyles
dotnet run --project .\CFRezManager.csproj -- --scan-cfg --root "C:\Extracted\cfg"
dotnet run --project .\CFRezManager.csproj -- --decode-cfg --root "C:\Extracted\cfg"
```

- `--export-obj`：导出 LithTech 模型为 OBJ/MTL，同步写出 `_textures` 贴图目录并在 MTL 中引用贴图；生成逐网格报告和 `_texture_slots.tsv`，记录本地材质阶段、全局皮肤槽、上下文解析出的贴图路径，以及 `unresolved`、`export-failed` 或 `exported` 状态。UCC 对象模型会在网格名与唯一同名 `UCC/ModelAssets/UCC_Textures/Object/*.DTX` 一致时按网格分别绑定贴图；`UCC_Model` 仍按其专用模型贴图索引解析。TSV 覆盖引擎多阶段槽位，但 OBJ/MTL 当前仍只呈现单张漫反射贴图，不代表已重建完整 RenderStyle。非有限顶点/UV 的网格会跳过并单独列出。
- `--audit-texture-map`：遍历资源目录和 REZ，输出全局槽、上下文槽、推导槽、逐网格解析及 LTB 清单。`mesh-export` 记录按导出器顺序首个能由实际纹理解析器解码的候选；`mesh-semantic` 关闭全局配置兜底，只保留槽位、网格/路径语义候选。能解码不代表已证实为游戏最终皮肤。TSV 标出歧义、物理副本冲突、贴图缺失、模型解码失败和无解析证据项；物理副本冲突行会列出每份模型的归档、资源路径、MD5 和各自候选贴图。`candidate-resource-present-unresolved` 表示模型贴图路径范围内存在候选资源，但纹理解析器没有返回图像；`candidate-resource-present-unresolved-outside-model-texture-scope` 表示该资源不在模型贴图路径范围内，解析器同样没有返回图像。详情记录 `image_decode=unconfirmed`，不把路径未命中误报为图像解码失败；`texture-path-decode-probe` 会按精确路径逐个解码候选资源和未命中的直接槽位引用的所有物理副本，标记可解码副本数、全副本失败或精确路径不存在。贴图名称回退只在候选路径唯一时生效；缺失的 PV-QBZ95_Silencer_Volcano 预览贴图会使用与目标模型顶点和 UV 完全相同的 PV-QBZ95_Volcano 贴图。根级 `ModelTextures/` 与 `UCC/ModelAssets/UCC_Textures/` 均纳入模型贴图路径识别。RenderStyle 等非几何 LTB 单独列为资源，不计入模型解码失败；无证据不等于已解析。
- AI1_EvilDen 的 CutScene02 Dark/Fire 及动作副本只有在每份模型都解出 `stone01/02/03` 三个单槽 UV 网格、且 `AI_Stone.dtx` 所有物理副本哈希一致时才绑定该纹理；这些 UV 落在石门图集上。`Uma` 网格仍未绑定：它与 `AI_Umaking`、`AI_GhostUmaking` 身体网格的 UV 三角均无精确重合，仅凭附近 CFT 记录无法确定过场实际使用的皮肤。
- 原生 LTB 解码按通道保留模型数据 UV1–UV4，并分别记录来源流；同一通道若在多条流中冲突，该通道标为不可用。`mesh-render-stage` 详情记录 `uv_source_name`、零基 `uv_set_index`、可用通道数 `mesh_uv_set_count`、该 UV 通道的 `uv_source_stream_index` 和 `uv_data_status`。当纹理绑定和资源均有效、但请求的模型 UV 通道缺失或冲突时，阶段状态为 `renderstyle-stage-uv-set-unavailable`；未知 UV 来源会标为 `renderstyle-stage-uv-source-unsupported`。OBJ 的 `_texture_slots.tsv` 记录可用 UV 通道数和 UV1 来源流。OBJ 目前仍只输出 UV1 和单张漫反射贴图，不能展示使用 UV2–UV4 的阶段。
- `--probe-texture-map-paths --audit <审计TSV>`：对已有审计中 `model`、`context`、`mesh-slot` 和 `mesh-render-stage` 行里的所有唯一贴图路径逐个解码，并测试每个物理副本；输出单独的 TSV，区分全部副本成功、部分副本失败、全副本失败和精确路径不存在。缺失项另列文件名近似的资源路径作为排查线索，不会自动替换映射。需要同时传入 `--root` 与 `--output`。
- `--inspect-ltc-motion-links`：扫描可解码的 LTC 内容，输出同一 LTC 行中动画模型字段、普通模型字段、贴图字段和身份字段的关联。主审计会把同一行的 `MotionFileName` 按 `SkinFileName` 绑定并保留身份上下文；诊断报告不会推断表外关联。
- `--inspect-cft-motion-links`：输出 CFT 同一强类型记录中父模型、动作模型、贴图字段和身份字段的关系，帮助检查动作模型槽位继承是否有直接表记录支撑。
- `--audit-table-decode`：逐份读取目录及所有 REZ（含根目录 `engine.rez`）中的 CFT/LTC，不按归档 MD5 跳过物理副本。TSV 记录实际内容 SHA-256、解压字节数、CFT 行列数、已消费字节和未解释尾部；读取失败、大小限制和目录/归档扫描失败分别记录。CFT 的 `typed-decoded` 表示声明行及已识别的 ASCII `END` 尾标全部消费完毕；`typed-decoded-with-trailing-bytes` 表示还有未解释数据。LTC 的 `native-text-decoded` 表示内置解码路径已还原文本，配置类 LTC 不要求包含模型几何。另写 `_summary.txt` 汇总物理副本及唯一内容数。成功解码不表示模型—贴图映射已确认。
- `--inspect-cft-model-texture-links`：输出所有强类型 CFT 行的模型字段、关联模型、贴图字段和身份别名，用于区分角色、动作、手臂和 Socket 等引用角色。
- 两个 CFT 引用诊断命令的“已解码内容数”只统计成功读取全部声明行的表；部分表中已提取的行仍保留在引用报告中。逐文件失败和尾部情况用 `--audit-table-decode` 检查。
- 完整读取声明行且紧接标准 `END` 尾标时，CFT 的路径候选扫描在尾标前结束，防止旧数据残留混入其他模型的贴图候选；尾标后的内容仍由解码审计列为未解释数据。没有完整行/尾标证据的表保留原有部分解析路径。
- `--inspect-ltc-model-texture-links`：输出 LTC 强类型模型行、纹理字段、辅助贴图引用和身份别名，用于检查武器与玩家视角模型的对应关系。
- `--inspect-texture-resource-paths --contains <文本>`：列出路径包含指定文本的贴图逻辑路径、物理副本和 MD5，用于核对同名 DTX 是否存在冲突。
- CFT 动作引用即使未写扩展名，也只有在完整逻辑路径对应单一资源扩展名、父子模型均解码成功且实际槽位交集非空时才继承贴图。
- 动作贴图只从同一强类型记录的主模型字段继承；Socket、手/臂、玩家视角、武器和弹体字段不传给动作模型。
- 未编号 `SkinFileName` 只会在父子模型所有物理副本均解码并含槽 0，且贴图与父模型已有直接槽 0 映射完全一致时，传给动作模型槽 0；其余皮肤字段仍按明确编号匹配。
- AI 铁扇公主缺失的 `AI_PrincessIron_HAIR.dtx` 只在 `AI_Boss_PrincessIron` 主模型、关联动作模型和槽 2 均匹配时回退到唯一存在的 `PrincessIron_PC_HAIR.dtx`；动作发丝网格与 CFT 绑定的 `PrincessIron_PC_Hair` 模型共有 1,607/1,663 个 UV 三角形。
- `Parts.CFT` 的单贴图字段只在对应模型所有物理副本都能解码且使用同一个唯一材质槽时绑定；贴图按 CFT 精确路径解析，上下文映射还要求 DTX 副本 MD5 一致。
- EvilDen CutScene02 的 Dark/Fire 主体与动作模型，只有在每个物理副本的龙网格名、UV 三角布局、声明槽位和 RenderStyle 均匹配 CFT 绑定的 socket/主模型，且 DTX 副本 MD5 一致时，才将槽 0/1/2 对应到 Map01/02/03。Uma 和石块槽不沿用这组贴图。
- `--inspect-ltb-header`：列出匹配 LTB 副本的原始/解压头部和文件类型；模型会重试几何解码，D3D RenderStyle 会调用对应解码器并报告 style/pass 数量。`--all-renderstyles` 可一次检查并解码全部已知路径/命名的 RenderStyle。
- `--model` 可重复指定多个模型；它们会导出到同一 OBJ，并保留各网格与材质分组。
- `--texture-context` 可指定角色或道具 ID/名称：角色手臂贴图取自 CFT，道具第一人称贴图取自 LTC，并按 LTB 实际材质槽分别映射。同组 `WeaponModel` 能唯一关联时，`item:<道具编号>` 会沿 `WeaponPreview` → `WeaponModel` 查找 LTC `WeaponIndex` 或按旧去重规则还原且无冲突的武器绑定序号；编号跨域冲突时，可用 `preview:<模型组号>:<道具编号>` 明确选择预览表关联，避免误用同号 LTC `WeaponIndex`。若该道具/分组没有可用的 `WeaponModel.ModelId`，可回退到同号 LTC `WeaponIndex`；仅当其他分组提供了直接命中的 LTC `WeaponIndex` 目标时才跳过回退。绑定按具体 LTC 模型和贴图字段分别保留；旧版 LTC 路径只有在行类型、角色字段、模型/贴图文件名和身份别名均匹配，且目标 LTB 副本槽位一致时，才回链到物理资源。世界模型等非玩家视角字段仅在所有物理模型副本的槽位集合一致、且槽数与贴图字段数相同时映射。`CharacterType.CFT` 的阵营身体贴图字段绑定身体槽 0；若所有模型副本均成功解码且布局一致、槽 1 的使用网格全部命名为头/脸，且同目录 `BODY`→`FACE` DTX 确切存在，导出器会额外生成 `cft-body-face-derived` 推导绑定。缺少任一证据或候选冲突时保持未映射。动作模型所有副本都必须解码且槽位集合一致；父模型存在时，编号贴图字段通常只继承父子共有槽；若同一强类型 CFT 行明确关联父模型和动作模型，编号贴图路径精确存在且所有 DTX 副本 MD5 一致，也可映射到父模型所有副本都没有、而动作模型副本一致使用的槽。Princess Iron 发丝的缺失路径回退仍受单独的模型、动作模型、槽位和资源别名验证约束。父模型缺失时仅允许单槽 0 动作模型。共享动作模型可用带阵营后缀的上下文选择皮肤，例如 `A0097:BL`；同一模型的不同皮肤仍按身份上下文隔离。手/臂模型按各自角色解析，编号贴图字段在模型只有一个可用材质槽时绑定该唯一槽，多槽模型仍按序号匹配。LTC 玩家视角模型也识别 `Fview-am` 手臂名缩写。嵌入式 PV 模型的未命名手臂槽，只有在每个目标副本都能与同一资源层的 `ArmModel` 配对且槽布局签名唯一匹配时才从武器贴图序号中排除；对应副本缺失或签名冲突时保持未映射。也可直接指定 `weaponindex:<编号>`。存在槽位证据但未匹配的网格不会套用猜测贴图。
- `preview:<模型组号>:<道具编号>` 优先采用同组预览记录中唯一且能对应 LTC 的非零 `ModelId`；预览 `ModelId` 为 0 时，才从唯一的同组 `WeaponModel` 关系恢复。非零预览 `ModelId` 与 `WeaponModel.ModelId` 不同且没有直接 LTC 目标，或存在多个预览候选时，预览上下文保持未映射。`item:<道具编号>` 按唯一的同组 `WeaponModel` 关系解析。
- `ArmModel/Arm_*` 和 `Models/PLAYERVIEW/Arm_*` 下的第一人称臂模按手/臂网格名、模型家族名和 BL/GR/SP 变体匹配 `ModelTextures/PLAYERVIEW` DTX；优先使用精确变体，女性模型的 `_WOMAN_BL/GR` 可对应贴图的 `_BL/GR` 后缀。候选贴图必须在玩家视角目录内唯一，且同一路径的归档副本 MD5 一致；角色或路径有歧义时不绑定。同一模型被多个角色皮肤复用时，用 `--texture-context` 选择对应 CFT 身份，不跨上下文套用贴图。
- 对没有直接 CFT 手/臂绑定的旧版臂模，显式指定 `--texture-context` 后，可使用同阵营、同家族扩展版本的角色皮肤。只有源/目标所有物理模型副本均解码成功，且网格名、顶点、三角形索引、UV、文件声明材质槽和 RenderStyle 编号完全一致时才建立关联；嵌入贴图、缺失 UV、无效几何或副本冲突均不适用。该关联只存在于身份上下文中，不生成默认皮肤，报告标为 `cft-equivalent-arm-derived`，不视为游戏表直接引用。例如 `Arm_WildWest_BL` 用 `A9887` 选择 BLGR 皮肤，用 `A9350` 选择 Santa 皮肤；`Arm_Butcher_BL/GR` 可用 `A0202` 选择已在 CFT 中登记的 CN 皮肤。
- `Models/PLAYERVIEW/PV-*` 道具预览模型可按槽继承 CFT 登记的手/臂贴图：优先要求网格名、顶点数、UV、三角形索引、声明槽位和 RenderStyle 一致，允许顶点位置不同。若索引顺序不同，则要求网格名、顶点/三角数量、槽位和 RenderStyle 一致，且每个三角的顶点位置与 UV 对应完全相同；所有物理副本也必须通过同一规则。LTC `PViewModelFileName` 明确引用的玩家视角模型及其已存在的阵营/性别变体，也只有在所有物理副本均成功解码、对应槽签名与 CFT 手臂来源一致时才复用贴图。命中 CFT 身份上下文时使用该身份贴图；其他上下文只有在所有匹配身份都指向同一 DTX 时才使用唯一映射。冲突、缺少 UV、嵌入贴图或副本不一致时保持未映射，报告标为 `cft-equivalent-arm-derived`。例如 `PV-WAVE-CentryGun_WOMAN_GR` 可在 `A0005` 上解析手/臂槽 0/1；缺失的武器槽贴图仍保持未映射。
- LTC `PViewModelFileName` 的阵营/性别变体可推导同目录、同基名 DTX：仅当变体所有 LTB 副本的槽/UV 布局一致、DTX 所有副本均能解码且 SHA-256 相同，并且 LTC 贴图序号在变体中唯一落到非手臂槽、槽号与基模 LTC 映射相同时建立绑定。该推导标为 `ltc-preview-variant-same-name-derived`；直接 LTC/CFT 槽绑定优先，冲突或缺少证据时不绑定。
- 无上下文导出时，玩家视角基模可按同一相对目录中的唯一 DTX 建立忽略标点的基名关联。基模必须只有一个非手臂槽；所有 LTB 副本布局一致、DTX 副本均可解码且 SHA-256 相同；阵营/性别变体还必须使用相同槽号，且该槽没有直接表或身份映射。报告标为 `preview-normalized-basename-derived`；路径、槽位或上下文有歧义时保持未映射。
- 同基名跨目录模型可逐槽复用来源的直接贴图；来源和目标的同号槽都不能用于手/臂网格，来源槽的上下文贴图必须与直连 DTX 一致，源/目标所有 LTB 副本该槽的 UV 三角集合须完全一致，目标槽不能有直接或身份绑定，DTX 副本 SHA-256 也须一致。贴图名可以带部件后缀，例如 `QV-RI_FALCustom_turning_part.dtx`；多个来源指向不同贴图时保持未映射。报告标为 `preview-slot-uv-derived`。已验证例子包括 `PV-WAVE-Ampoule` 模型族槽 2、`PV-AK47_Beast_GameCup` 槽 2、`PV-Battle_Claymore` 四个阵营/性别模型槽 2（贴图为 `PV-Claymore.dtx`）、四个 `PV-9A91_WildEagle_Silencer_WD` 变体槽 3（贴图为 `PV-9A91_WildEagle_WD_01.dtx`；UV 不同的槽 2 保持未映射）、`QV-GoldBar` 槽 0、四个 `QV-ACC_SD` 道具模型槽 0、`QV-ViperDagger` Nano4Terminator 预览模型槽 0，以及背部 `QV-RI_FALCustom` 槽 0 和部件槽 1。
- `WeaponPreview/Skin` 变体只有在预览 BIN 唯一对应同名 PV 模型、基模有直接槽位贴图、目标武器网格名与基模名相同且所有模型副本的几何/UV 签名一致时，才会绑定唯一同名 DTX；贴图副本的 MD5 也必须一致。绑定使用 `preview:<模型组号>:<道具编号>`，不据此生成可能与 LTC `WeaponIndex` 冲突的 `item:` 身份。若某模型槽只有一个变体上下文，且该槽没有直接路径或身份上下文映射，CLI 和 GUI 无上下文导出也会按模型路径使用该 DTX。BCAxe 的 Chs/YOD 槽 2 是已验证样例，审计标记为 `weapon-preview-variant-derived`。
- `Arm_SwatElite_Renewal_GR` 的手/臂网格带有明确的角色和阵营后缀，可按 CFT A0017 中登记的 SWAT-ELITE 手/臂贴图名解析；仅当对应玩家视角 DTX 路径唯一且归档副本哈希一致时绑定。`Arm_WildWest_SP` 仍没有可靠的角色表贴图关联，继续保持未映射。
- LTC 已验证的 SixEyed Sword 条目使用相同 PView 模型和身份，但贴图名分别写作 `PV-SixEyedSword_Gold` 与 `PV-SixEyed_Sword_Gold`；后者只在无下划线 basename 对应唯一物理路径时回链。
- `WeaponAttachItemDoll.CFT` 多条记录共用 Joy QV 模型，PV 侧指向 `PV-ACC_Baihu`；Joy、TropicalOcean 和 PartyDoggy 的 PV/QV 模型 UV 布局一致。缺失的 `QV-ACC_Interaction_Joy` 与 `QV-ACC_Interaction_PartyDoggy` 贴图名仅在 `QV-ACC_Baihu` 对应唯一物理 DTX 时回链。
- 原生 LTB 解码保留每个网格的 RenderStyle 编号，并区分文件声明的材质槽与推测槽。OBJ 仍只导出漫反射贴图，不重建全部 RenderStyle 效果。
- 角色别名有冲突时，可用 `row:rez\RF100\Table\CharacterType.CFT#701:BL` 按零基 CFT 行和阵营精确选择皮肤；表内存在有效 `RecordId` 时可用 `recordid:rez\RF100\Table\CharacterType.CFT#<编号>`，两种编号域互不混用。
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

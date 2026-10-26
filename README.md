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

## 模型贴图记录

- [模型贴图参考清单](docs/asset-mapping-register.tsv) 随发布包提供，记录已成功导出的槽位，以及 CFT 已声明但槽位待验证的模型、贴图和动作模型路径；清单供复核，不作为运行时自动映射。
- [清单字段说明](docs/asset-mapping-register.md) 说明状态和空字段的含义。OBJ 导出目前不保存骨骼名称或动画轨道；未确认的骨骼挂点留空。

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

目录视图与导出扫描只列出带有效 REZ 头的归档；同目录的 `.REZ` 数据分片由归档读取器按索引读取，不会误当成独立归档。

```powershell
dotnet run --project .\CFRezManager.csproj -- --export-obj --root "F:\Game\CrossFire" --model "PV-AK47_Balance" --output ".\out\PV-AK47_Balance.obj"
dotnet run --project .\CFRezManager.csproj -- --audit-texture-map --root "F:\Game\CrossFire" --output ".\out\crossfire_texture_mapping_audit.tsv"
dotnet run --project .\CFRezManager.csproj -- --audit-table-decode --root "F:\Game\CrossFire" --output ".\out\table_decode.tsv"
dotnet run --project .\CFRezManager.csproj -- --inspect-ltb-header --root "F:\Game\CrossFire" --model "Models/CHARACTER/NINJATRANSLUCENT"
dotnet run --project .\CFRezManager.csproj -- --inspect-ltb-header --root "F:\Game\CrossFire" --all-renderstyles
dotnet run --project .\CFRezManager.csproj -- --scan-cfg --root "C:\Extracted\cfg"
dotnet run --project .\CFRezManager.csproj -- --decode-cfg --root "C:\Extracted\cfg"
```

- `TEX/` 下的贴图可按完整路径或目录后缀解析，不参与单独的文件名候选。`PV-ShieldCard_AI_NEW`、`_BL`、`_GR`、`_WOMAN_BL` 和 `_WOMAN_GR` 的槽 2 已验证映射到 `TEX/UI/AIMode/AI1PreCrater/CARD.DTX`；基模和 `_BL` 使用 `card` 网格，其余变体使用 `Box003`。所有模型副本的卡面 UV 三角签名必须一致，贴图副本必须成功解码且 SHA-256 一致。导出标为 `variant-slot-uv-derived`。
- `--export-obj`：导出 LithTech 模型为 OBJ/MTL，同步写出 `_textures` 贴图目录并在 MTL 中引用贴图；生成逐网格报告和 `_texture_slots.tsv`，记录本地材质阶段、全局皮肤槽、上下文解析出的贴图路径，以及 `unresolved`、`export-failed` 或 `exported` 状态。UCC 对象模型会在网格名与唯一同名 `UCC/ModelAssets/UCC_Textures/Object/*.DTX` 一致时按网格分别绑定贴图；`UCC_Model` 仍按其专用模型贴图索引解析。TSV 覆盖引擎多阶段槽位，但 OBJ/MTL 当前仍只呈现单张漫反射贴图，不代表已重建完整 RenderStyle。非有限顶点/UV 的网格会跳过并单独列出。
- 网格名与 `ModelTextures/` 对应目录中的 DTX 基名完全一致时，可将该 DTX 推导到同槽的其他网格；所有 LTB 副本必须给出相同槽位绑定，DTX 副本必须通过解码一致性检查，已有 CFT/LTC 和其他已验证绑定优先。审计来源标为 `mesh-name-slot-derived`。2026-10-26 的 `PV-MechanicXM214` 五模型聚焦导出在 `weaponindex:4851` 和无上下文两种模式下都解析 35/35 个网格槽，缺失贴图为 0；槽 0/1 分别映射到 `FVIEW_ARM_MechanicHero.DTX` 和 `FVIEW_ARM_MechanicXM214.DTX`。
- `--audit-texture-map`：遍历资源目录和 REZ，输出全局槽、上下文槽、推导槽、逐网格解析及 LTB 清单。全局映射索引最多保留 128 份已解码 LTB，超出后按需重新解码，以限制全目录扫描的内存占用。`mesh-export` 记录按导出器顺序首个能由实际纹理解析器解码的候选；`mesh-semantic` 关闭全局配置兜底，只保留槽位、网格/路径语义候选。能解码不代表已证实为游戏最终皮肤。TSV 标出歧义、物理副本冲突、贴图缺失、模型解码失败和无解析证据项；物理副本冲突行会列出每份模型的归档、资源路径、MD5 和各自候选贴图。LTC 已声明但未建立安全槽位绑定的引用也以 `ltc-declared-reference` 输出，保留上下文、槽位、原始路径和物理副本数；`reference-unresolved` 表示副本可解码且内容一致但尚无上下文绑定，`ambiguous` 表示副本冲突、无法解码，或声明与模型上下文绑定冲突。`candidate-resource-present-unresolved` 表示模型贴图路径范围内存在候选资源，但纹理解析器没有返回图像；`candidate-resource-present-unresolved-outside-model-texture-scope` 表示该资源不在模型贴图路径范围内，解析器同样没有返回图像。详情记录 `image_decode=unconfirmed`，不把路径未命中误报为图像解码失败；`texture-path-decode-probe` 会按精确路径逐个解码候选资源和未命中的直接槽位引用的所有物理副本，标记可解码副本数、全副本失败或精确路径不存在。贴图名称回退只在候选路径唯一时生效；缺失的 PV-QBZ95_Silencer_Volcano 预览贴图会使用与目标模型顶点和 UV 完全相同的 PV-QBZ95_Volcano 贴图。根级 `ModelTextures/` 与 `UCC/ModelAssets/UCC_Textures/` 均纳入模型贴图路径识别。RenderStyle 等非几何 LTB 单独列为资源，不计入模型解码失败；无证据不等于已解析。
- AI1_EvilDen 的 CutScene02 Dark/Fire 及动作副本只有在每份模型都解出 `stone01/02/03` 三个单槽 UV 网格、且 `AI_Stone.dtx` 所有物理副本哈希一致时才绑定该纹理；这些 UV 落在石门图集上。`Uma` 网格仍未绑定：它与 `AI_Umaking`、`AI_GhostUmaking` 身体网格的 UV 三角均无精确重合，仅凭附近 CFT 记录无法确定过场实际使用的皮肤。
- 原生 LTB 解码按通道保留模型数据 UV1–UV4，并分别记录来源流；同一通道若在多条流中冲突，该通道标为不可用。`mesh-render-stage` 详情记录 `uv_source_name`、零基 `uv_set_index`、可用通道数 `mesh_uv_set_count`、该 UV 通道的 `uv_source_stream_index` 和 `uv_data_status`。当纹理绑定和资源均有效、但请求的模型 UV 通道缺失或冲突时，阶段状态为 `renderstyle-stage-uv-set-unavailable`；未知 UV 来源会标为 `renderstyle-stage-uv-source-unsupported`。OBJ 的 `_texture_slots.tsv` 记录可用 UV 通道数和 UV1 来源流。OBJ 目前仍只输出 UV1 和单张漫反射贴图，不能展示使用 UV2–UV4 的阶段。
- `--probe-texture-map-paths --audit <审计TSV>`：对已有审计中 `model`、`context`、`mesh-slot` 和 `mesh-render-stage` 行里的所有唯一贴图路径逐个解码，并测试每个物理副本；输出单独的 TSV，区分全部副本成功、部分副本失败、全副本失败和精确路径不存在。缺失项另列文件名近似的资源路径作为排查线索，不会自动替换映射。需要同时传入 `--root` 与 `--output`。
- `--inspect-ltc-motion-links`：扫描可解码的 LTC 内容，输出同一 LTC 行中动画模型字段、普通模型字段、贴图字段和身份字段的关联。主审计会把同一行的 `MotionFileName` 按 `SkinFileName` 绑定并保留身份上下文；诊断报告不会推断表外关联。
- `--inspect-cft-motion-links`：输出 CFT 同一强类型记录中父模型、动作模型、贴图字段和身份字段的关系，帮助检查动作模型槽位继承是否有直接表记录支撑。
- `--audit-table-decode`：逐份读取目录及所有 REZ（含根目录 `engine.rez`）中的 CFT/LTC，不按归档 MD5 跳过物理副本。TSV 记录实际内容 SHA-256、解压字节数、CFT 行列数、已消费字节和未解释尾部；读取失败、大小限制和目录/归档扫描失败分别记录。CFT 的 `typed-decoded` 表示声明行及已识别的 ASCII `END` 尾标全部消费完毕；`typed-decoded-with-trailing-bytes` 表示还有未解释数据。LTC 的 `native-text-decoded` 表示内置解码路径已还原文本，配置类 LTC 不要求包含模型几何。另写 `_summary.txt` 汇总物理副本及唯一内容数。成功解码不表示模型—贴图映射已确认。
- `--inspect-cft-model-texture-links`：输出所有强类型 CFT 行的模型字段、关联模型、贴图字段和身份别名，用于区分角色、动作、手臂和 Socket 等引用角色。
- `WeaponAttachItemDoll.CFT` 的道具身份也可绑定预览配件贴图：`item:13869`（第 147 行）和 `item:21765`（第 320 行）均将 `PV-ACC_Interaction_Joy` 的 `Box002` 槽 0 映射到 `PV-ACC_Baihu.dtx`；两次聚焦导出都报告 `context-slot`，且无缺失贴图。
- `CharacterItem.CFT` 第 1809 行的 `Blitz_Buster_Hair_SP` 特例将 `_BLTexFile2` / `_GRTexFile2` 指向的缺失 `Blitz_Buster_HAT_SP.dtx` 绑定到原生槽 0。仅当所有模型副本都解码为单网格、槽位布局一致且网格名为 `V214_Blitz_Buster_GR_hiar`，同时唯一的 `Blitz_Buster_HAIR_SP.DTX` 所有副本均可解码且 SHA-256 一致时生效。聚焦导出在有身份上下文和无上下文时均导出 1 张贴图、缺失数为 0。
- 同表第 320 行的身份别名 `ACC_Interaction_BlueLotus` 对应独立模型 `PV-ACC_Interaction_BlueLotus`。只有当别名在同目录 ACC_SD 预览模型中唯一、Joy 与目标模型的所有副本网格布局/UV/槽位/RenderStyle 和权威绑定完全相同、精确 DTX 的所有副本均可解码且 SHA-256 一致，并且目标槽没有直接或冲突映射时，才把 Baihu 贴图推导到该行上下文。`item:21765` 聚焦导出中，Joy 与 BlueLotus 的 `Box002` 槽 0 均导出 `PV-ACC_Baihu.dtx`；BlueLotus 报告为 `cft-identity-model-alias-derived`。该推导不用于无上下文导出。
- 两个 CFT 引用诊断命令的“已解码内容数”只统计成功读取全部声明行的表；部分表中已提取的行仍保留在引用报告中。逐文件失败和尾部情况用 `--audit-table-decode` 检查。
- 完整读取声明行且紧接标准 `END` 尾标时，CFT 的路径候选扫描在尾标前结束，防止旧数据残留混入其他模型的贴图候选；尾标后的内容仍由解码审计列为未解释数据。没有完整行/尾标证据的表保留原有部分解析路径。
- 2026-10-26 的当前目录盘点覆盖 482 个 REZ 归档、2,398 份 CFT/LTC 物理副本；70 份唯一 CFT 内容仍有未解释尾部。当前 `RF100.REZ` 的 `CharacterType.CFT` 有 381 字节未消费数据，路径扫描没有从这段数据建立行级映射。
- `--inspect-ltc-model-texture-links`：输出 LTC 强类型模型行、纹理字段、辅助贴图引用和身份别名，用于检查武器与玩家视角模型的对应关系。
- `--inspect-texture-resource-paths --contains <文本>`：列出路径包含指定文本的贴图逻辑路径、物理副本和 MD5，用于核对同名 DTX 是否存在冲突。
- CFT 动作引用即使未写扩展名，也只有在完整逻辑路径对应单一资源扩展名、父子模型均解码成功且实际槽位交集非空时才继承贴图。
- 动作贴图只从同一强类型记录的主模型字段继承；Socket、手/臂、玩家视角、武器和弹体字段不传给动作模型。
- 未编号 `SkinFileName` 只会在父子模型所有物理副本均解码并含槽 0，且贴图与父模型已有直接槽 0 映射完全一致时，传给动作模型槽 0；其余皮肤字段仍按明确编号匹配。
- AI 铁扇公主缺失的 `AI_PrincessIron_HAIR.dtx` 只在 `AI_Boss_PrincessIron` 主模型、关联动作模型和槽 2 均匹配时回退到唯一存在的 `PrincessIron_PC_HAIR.dtx`；动作发丝网格与 CFT 绑定的 `PrincessIron_PC_Hair` 模型共有 1,607/1,663 个 UV 三角形。
- `Parts.CFT` 的单贴图字段只在对应模型所有物理副本都能解码且使用同一个唯一材质槽时绑定；贴图按 CFT 精确路径解析。同一路径的 DTX 副本必须全部可解码；原始字节不同时，解码后的尺寸和像素也必须一致。
- EvilDen CutScene02 的 Dark/Fire 主体与动作模型，只有在每个物理副本的龙网格名、UV 三角布局、声明槽位和 RenderStyle 均匹配 CFT 绑定的 socket/主模型，且 DTX 副本 MD5 一致时，才将槽 0/1/2 对应到 Map01/02/03。Uma 和石块槽不沿用这组贴图。
- `--inspect-ltb-header`：列出匹配 LTB 副本的原始/解压头部和文件类型；模型会重试几何解码，D3D RenderStyle 会调用对应解码器并报告 style/pass 数量。`--all-renderstyles` 可一次检查并解码全部已知路径/命名的 RenderStyle。
- `--model` 可重复指定多个模型；它们会导出到同一 OBJ，并保留各网格与材质分组。
- `--texture-context` 可指定角色或道具 ID/名称：角色手臂贴图取自 CFT，道具第一人称贴图取自 LTC，并按 LTB 实际材质槽分别映射。同组 `WeaponModel` 能唯一关联时，`item:<道具编号>` 会沿 `WeaponPreview` → `WeaponModel` 查找 LTC `WeaponIndex` 或按旧去重规则还原且无冲突的武器绑定序号；编号跨域冲突时，可用 `preview:<模型组号>:<道具编号>` 明确选择预览表关联，避免误用同号 LTC `WeaponIndex`。若该道具/分组没有可用的 `WeaponModel.ModelId`，可回退到同号 LTC `WeaponIndex`；仅当其他分组提供了直接命中的 LTC `WeaponIndex` 目标时才跳过回退。绑定按具体 LTC 模型和贴图字段分别保留；唯一的 LTC `Weapon` 旧映射编号明确关联到 `WeaponIndex` 时，预览别名也会沿该编号应用到同一武器的其他 LTC 模型字段，例如世界模型和 PView；关联有歧义时不传播。旧版 LTC 路径只有在行类型、角色字段、模型/贴图文件名和身份别名均匹配，且目标 LTB 副本槽位一致时，才回链到物理资源。世界模型等非玩家视角字段要求所有物理模型副本的槽位集合一致，并按槽位顺序匹配 LTC 贴图字段。若实际使用的槽位从 0 开始连续，且字段序号从第 1 项开始连续，只绑定两者对应的前缀；未使用的尾部字段不分配槽位。`CharacterType.CFT` 的阵营身体贴图字段绑定身体槽 0；若所有模型副本均成功解码且布局一致、槽 1 的使用网格全部命名为头/脸，且同目录 `BODY`→`FACE` DTX 确切存在，导出器会额外生成 `cft-body-face-derived` 推导绑定。缺少任一证据或候选冲突时保持未映射。动作模型所有副本都必须解码且槽位集合一致；父模型存在时，编号贴图字段通常只继承父子共有槽；若同一强类型 CFT 行明确关联父模型和动作模型，编号贴图路径精确存在且所有 DTX 副本 MD5 一致，也可映射到父模型所有副本都没有、而动作模型副本一致使用的槽。对于精确以 `_M-motion` 结尾的玩家视角模型，也可按同一道具上下文继承去掉该后缀后的父模型贴图。所有父子模型副本必须成功解码并保持一致的槽位布局；目标槽的网格名、声明槽列表和 RenderStyle 编号须匹配，目标上下文不得已有绑定，父模型贴图的所有 DTX 副本须可解码且 SHA-256 相同。手/臂槽和冲突槽不继承；报告标为 `motion-parent-slot-derived`。Princess Iron 发丝的缺失路径回退仍受单独的模型、动作模型、槽位和资源别名验证约束。父模型缺失时仅允许单槽 0 动作模型。共享动作模型可用带阵营后缀的上下文选择皮肤，例如 `A0097:BL`；同一模型的不同皮肤仍按身份上下文隔离。手/臂模型按各自角色解析，编号贴图字段在模型只有一个可用材质槽时绑定该唯一槽，多槽模型仍按序号匹配。LTC 玩家视角模型也识别 `Fview-am` 手臂名缩写。嵌入式 PV 模型的未命名手臂槽，只有在每个目标副本都能与同一资源层的 `ArmModel` 配对，且唯一匹配的臂槽满足以下任一条件时才从武器贴图序号中排除：完整槽布局签名匹配，或槽内全部同名网格的 RenderStyle 和 UV 三角签名逐项匹配；对应副本缺失或签名冲突时保持未映射。也可直接指定 `weaponindex:<编号>`。需要同时指定角色和武器时，可重复传入 `--texture-context`；每个槽仅在各上下文指向同一贴图时合并，冲突槽保持未解析。存在槽位证据但未匹配的网格不会套用猜测贴图。
- `preview:<模型组号>:<道具编号>` 优先采用同组预览记录中唯一且能对应 LTC 的非零 `ModelId`；预览 `ModelId` 为 0 时，才从唯一的同组 `WeaponModel` 关系恢复。非零预览 `ModelId` 与 `WeaponModel.ModelId` 不同且没有直接 LTC 目标，或存在多个预览候选时，预览上下文保持未映射。`item:<道具编号>` 按唯一的同组 `WeaponModel` 关系解析。
- 当同一 LTC 表中的武器身份别名只对应一条 PView 记录时，预览上下文也会传给身份别名完全相同的其他 `Weapon` 行；PView 对应多条时不传播。例如 `preview:2000000:201` 同时选择 `PV-687EDP` 与 `QV-687EDP_M` 的 CFS 2017 贴图记录。
- `WeaponPreview/Skin` 可用皮肤文件名校正冲突的预览 `ModelId`：道具号必须是 LTC `WeaponIndex`，该编号只能有一个实际 PView 模型，组内全部皮肤名须与模型名相同或只增加 `_` 后缀，且同组 `WeaponModel` 不得给出其他有效型号。当前安装中，1003 的预览型号 1536 指向 Police Baton，但皮肤名和 LTC 将其唯一指向 `PV-Kukri_Beast`；1377 的型号 1991 指向 AK12 Urban，皮肤名和 LTC 将其唯一指向 `PV-BCAxe_Beast`。
- 已验证跨行映射：`preview:2000000:3149` 的 LTC 旧编号 `5741` 通过 `WeaponIndex:4712` 关联到另一行编号 `5743`，将 `PV-9A91_WildEagle_ExtendMag` 槽 0 映射为 `PV-9A91_WildEagle_NG.DTX`；聚焦导出的 10 个网格全部成功绑定贴图。
- 同路径 DTX 副本可按解码图像校验：`PV-BrokenKukri_RedCrystal.DTX` 在 `rez` 与 `rez2` 的原始 MD5 不同，但两份均解码为 512×512，像素 SHA-256 相同。五个预览上下文中的 `PV-BrokenKukri_RedCrystal` 槽 2 均映射到该贴图路径。
- 2026-10-26 按当前 WeaponPreview、WeaponModel 与 LTC 记录重建交叉表。PView/PreView 审计包含 232 个预览上下文/模型组合，230 组映射、266 个槽位均命中 LTC 指定贴图。仅 `preview:10000000:4897` 和 `preview:8000000:4897` 的 `PV-Keris_Finger` 保持未映射：槽 0/1 是手/臂，基准 `PV-Keris_Unicorn` 的道具网格在槽 2，而该变体没有槽 2。
- 复核时直接解码了两个预览 BIN：`WeaponPreview/8/8-4897.bin` 与 `WeaponPreview/VVIPShopGv/10-4897.bin` 的 `ModelId` 均为 `10327`，对应 BF005.LTC 旧编号 10327 / `WeaponIndex:7557`。世界模型行将 `QV-Keris_Finger` 槽 0 绑定到 `QV-Keris_Unicorn.dtx`；PView `PV-Keris_Finger` 只有 `Fview-hand2` 槽 0 和 `Fview-arm2` 槽 1，基础 `PV-Keris_Unicorn` 的 `Object01` 才使用槽 2。旧 `coverage-current-crosswalk.tsv` 中指向 WideGrenade 的条目已由全武器字段交叉表取代；这两项 PView 上下文仍因变体缺少槽 2 道具网格而保持未映射。
- 全武器字段交叉表复核覆盖 972 条 LTC 目标行、601 个上下文/模型组合；599 组有映射，642 个已映射槽位全部命中 LTC 指定路径。336 个模型/贴图路径（187 个模型、149 张贴图）均在交叉表使用的 481 个 REZ 归档快照中找到，共 373 份物理副本。另有 694 个直接 LTC 预览上下文/模型组合、742 个槽位通过绑定一致性复核。2026-10-26 的表解码盘点另覆盖 482 个归档，不代表武器交叉表已重算。此前基于旧交叉表和不完整资源目录的 217/542 组数字不再作为覆盖结论。
- BornBeast 世界模型 G_M/G_F 的 16 个上下文/模型组合现映射到 LTC 首字段指定的 Modern 或 Foliage 贴图。预览 `2000000:3416` 的 `ModelId:6317` 经 BF005.LTC 对应 `WeaponIndex:5050`；两种模型的所有副本都只声明槽 0，因此只绑定该 LTC 行的第一张贴图，第二字段的 Knife 贴图不分配给这些模型。G_M 聚焦 OBJ 导出得到 1 个网格、1 张贴图、缺失 0。\n- `WeaponPreview/8/8-4709.bin` 的 `ModelId:9773` 对应 BF005.LTC 旧映射编号 9773 和 `WeaponIndex:7123`；`preview:8000000:4709` 的 `PV-Card_RM_TD_S31_Lv1` 槽 2 使用 `PV-Card_RM_TD_S31_Master.dtx`。相似的 `PV-Card_RM_TD_S31_POG.dtx` 属于 `WeaponIndex:7126`，不能套用到该预览上下文。
- `ArmModel/Arm_*`、`Models/PLAYERVIEW/Arm_*` 和 `Models/PLAYERVIEW/ArmModel/Arm_*` 下的第一人称臂模按手/臂网格名、模型家族名和 BL/GR/SP 变体匹配 `ModelTextures/PLAYERVIEW` DTX；优先使用精确变体，女性模型的 `_WOMAN_BL/GR` 可对应贴图的 `_BL/GR` 后缀。候选贴图必须在玩家视角目录内唯一，且同一路径的归档副本 MD5 一致；角色或路径有歧义时不绑定。同一模型被多个角色皮肤复用时，用 `--texture-context` 选择对应 CFT 身份，不跨上下文套用贴图。
- 对没有直接 CFT 手/臂绑定的旧版臂模，显式指定 `--texture-context` 后，可使用同阵营、同家族扩展版本的角色皮肤。只有源/目标所有物理模型副本均解码成功，且网格名、顶点、三角形索引、UV、文件声明材质槽和 RenderStyle 编号完全一致时才建立关联；嵌入贴图、缺失 UV、无效几何或副本冲突均不适用。该关联只存在于身份上下文中，不生成默认皮肤，报告标为 `cft-equivalent-arm-derived`，不视为游戏表直接引用。例如 `Arm_WildWest_BL` 用 `A9887` 选择 BLGR 皮肤，用 `A9350` 选择 Santa 皮肤；`Arm_Butcher_BL/GR` 可用 `A0202` 选择已在 CFT 中登记的 CN 皮肤。
- 嵌入式 PV 道具模型在严格几何匹配未命中时，可按完整 UV 三角布局继承 CFT 臂模贴图。网格名、声明槽位、RenderStyle、UV 通道来源及每个 UV 三角（含重复项）必须一致，所有物理副本一致，且目标槽无直接绑定、候选贴图唯一；报告仍标为 `cft-equivalent-arm-derived`。该规则将 `PV-AK-47-Beast` 的槽 1 映射到 `Arm_MAN_BL` 关联的臂贴图。
- 嵌入式 PV 模型的单个手/臂网格若在所有副本中都使用唯一声明槽，且同名网格与 CFT 登记的 `ArmModel` 在 RenderStyle、网格数量及逐三角位置/UV 上完全一致，也可把对应 CFT 贴图推导到该模型槽。槽位属于整个模型，同槽的其他网格会使用同一贴图。`PV-DragonSoul` 五个版本的槽 0/1 均按此规则映射 JNS Warrior GR 手/臂贴图；四个变体的 `Sphere001` 共用槽 0，基模的独立球体槽 2 仍未解析。报告标为 `cft-equivalent-arm-derived`。
- `Models/PLAYERVIEW/PV-*` 道具预览模型可按槽继承 CFT 登记的手/臂贴图：优先要求网格名、顶点数、UV、三角形索引、声明槽位和 RenderStyle 一致，允许顶点位置不同。若索引顺序不同，则要求网格名、顶点/三角数量、槽位和 RenderStyle 一致，且每个三角的顶点位置与 UV 对应完全相同；所有物理副本也必须通过同一规则。LTC `PViewModelFileName` 明确引用的玩家视角模型及其已存在的阵营/性别变体，也只有在所有物理副本均成功解码、对应槽签名与 CFT 手臂来源一致时才复用贴图。命中 CFT 身份上下文时使用该身份贴图；其他上下文只有在所有匹配身份都指向同一 DTX 时才使用唯一映射。冲突、缺少 UV、嵌入贴图或副本不一致时保持未映射，报告标为 `cft-equivalent-arm-derived`。例如 `PV-WAVE-CentryGun_WOMAN_GR` 可在 `A0005` 上解析手/臂槽 0/1；缺失的武器槽贴图仍保持未映射。
- LTC `PViewModelFileName` 登记的 `PV-*` 道具预览模型也可按明确的手/臂网格名映射 CFT 玩家贴图。所有模型副本都必须解码并得到相同角色槽位；每张候选 DTX 的归档副本字节必须一致（MD5 相同）。可用 `A9346:BL` 或 `A9346:GR` 选择阵营；不带阵营的身份仅在候选贴图唯一时绑定。此映射只在上下文生效，不创建默认贴图，报告标为 `cft-player-view-role-derived`。
- `PV-AsceticHero` 基模、`_BL`、`_GR` 及 `_WOMAN_BL/GR` 变体的 `Hero_Hand` 槽 0 和 `Hero_Arm` 槽 1，分别映射到同目录的 `FVIEW_HAND_AsceticHero.DTX` 与 `FVIEW_ARM_AsceticHero.DTX`。每个 LTB 副本都必须解码并在对应槽唯一使用预期网格名；WOMAN 变体还必须与同阵营普通版本的逐三角 UV 签名一致。所有候选 DTX 副本必须可解码且内容一致。该推导标为 `preview-character-role-derived`，不覆盖直接 LTC 槽位；没有直接表记录的其他模型也不按名称套用。
- LTC `PViewModelFileName` 基模直接引用的 DTX 可推导到已有阵营/性别变体：仅当基模和变体的所有 LTB 副本槽/UV 布局一致、相关 DTX 副本均能解码且 SHA-256 相同，并且 LTC 贴图序号唯一落到对应非手臂槽时建立绑定。若一条 LTC 记录只含一个不同贴图序号，该序号可绑定到模型唯一的非手臂槽；同一记录含多个贴图序号时严格按序匹配，缺少的基模槽不会折回第一槽。若变体新增了基模缺少的后续槽，仅当此前序号在基模和变体中映射到相同槽、当前槽在基模不存在且变体手臂槽已验证时，才把对应 LTC 字段映射到新槽。嵌套 `Models/PLAYERVIEW/.../PV-*` 路径也会查找对应阵营手臂模型。手臂槽识别支持 `ReactiveArmor-hand`、`ReactiveArmor-arm` 这类带对象前缀的名称；手/臂网格只有在该槽的所有网格都属于手/臂时才排除该槽，与道具网格共用的槽仍参与序号匹配。若基模该槽已有不同贴图，或贴图副本内容不一致，则不推导。该推导标为 `ltc-preview-variant-derived`；直接 LTC/CFT 槽绑定优先。BF005.LTC 的 `PV-MeshGun` row 952 已验证：基模槽 2 使用 `PV-TAC15_01.DTX`，BL/GR/WOMAN_BL/WOMAN_GR 变体的 `scope` 槽 3 使用第二字段 `PV-MeshGun.DTX`；WOMAN_GR 的 `Object05` 经 `Arm_WOMAN_GR` 槽布局校验后识别为臂槽。
- 无上下文导出时，玩家视角基模可按同一相对目录中的唯一 DTX 建立忽略标点的基名关联。基模必须只有一个非手臂槽；所有 LTB 副本布局一致、DTX 副本均可解码且 SHA-256 相同；阵营/性别变体还必须使用相同槽号，且该槽没有直接表或身份映射。报告标为 `preview-normalized-basename-derived`；路径、槽位或上下文有歧义时保持未映射。已验证的 `PV-9A91_WildEagle` 同字节变体 `_BD`、`_MKG`、`_MT`、`_Mtr` 均将槽 3 映射到各自同基名 DTX，每个聚焦导出确认 `N_silencer_2`、`N_silencer_3`、`N_scope` 三项；基模、`_CAT`、`_NG_Ice`、`_WD` 的槽 3 存在冲突表绑定，聚焦无上下文导出保持未映射。
- 同基名跨目录模型可逐槽复用来源的直接贴图；来源和目标的同号槽都不能用于手/臂网格，来源槽的上下文贴图必须与直连 DTX 一致，源/目标所有 LTB 副本该槽的 UV 三角集合须完全一致，目标槽不能有直接或身份绑定，DTX 副本 SHA-256 也须一致。贴图名可以带部件后缀，例如 `QV-RI_FALCustom_turning_part.dtx`；多个来源指向不同贴图时保持未映射。报告标为 `variant-slot-uv-derived`。LTC 资源引用会先折叠为逻辑贴图路径，再检查 DTX；显式上下文仅在目标上下文没有该槽绑定时使用已验证的路径级推导。已验证例子包括 `PV-WAVE-Ampoule` 模型族槽 2、`PV-AK47_Beast_GameCup` 槽 2、`PV-Battle_Claymore` 四个阵营/性别模型槽 2（贴图为 `PV-Claymore.dtx`）、四个 `PV-9A91_WildEagle_Silencer_WD` 变体槽 3（贴图为 `PV-9A91_WildEagle_WD_01.dtx`；UV 不同的槽 2 保持未映射）、`QV-GoldBar` 槽 0、四个 `QV-ACC_SD` 道具模型槽 0、`QV-ViperDagger` Nano4Terminator 预览模型槽 0、背部 `QV-RI_FALCustom` 槽 0 和部件槽 1、八个 `PV-AA12_Buster_HexaCore` 阵营/性别变体（含 ChangeMode）`StickMag` 网格槽 3 对应 `PV-AA12_Buster_HexaCore01.dtx`，以及 `PV-M98B_WOMAN_GR` 的 `M98B`、`mag`、`relo` 网格槽 2 和 `m98bscope` 网格槽 3 分别对应 `PV-M98B.dtx` 与 `PV-M98B01.dtx`（其他三个阵营/性别变体的槽 3 保持未解析）。HexaCore 聚焦导出每个模型恢复一个网格贴图项，另有 11 项未解析。
- 所有模型目录中的 `_M-motion` 动作模型均可尝试继承同目录精确基模的直连贴图：必须找到唯一的后缀去除配对、父模有直连槽、父子同号槽 UV 三角集合完全一致，目标槽没有直连或身份绑定，父模上下文贴图不得与直连 DTX 冲突，且 `ModelTextures/` 下该贴图的所有副本均可解码并 SHA-256 一致；手/臂槽排除。报告标为 `variant-slot-uv-derived`。`AI_EliteBoss_Chaos_M-motion` 的身体槽 0（3,392 个 UV 三角）和翅膀槽 1（1,140 个）与 `AI_EliteBoss_Chaos` 完全匹配，继承 AICharacter.CFT 为父模绑定的 `Nano_Chaos_Body.dtx`、`Nano_Chaos_Wing.dtx`。第一人称例子 `PV-ACC_BlueLotus_M-motion` 的 `LOT` 槽 0 与父模的 2,254 个 UV 三角一致，使用 `WeaponAttachItemDoll.CFT` 第 319 行的 `PV-ACC_BlueLotus.dtx`；`PV-ACC_Legendary_m-motion` 的 `ACC_Legendary` 槽 0 与父模的 1,436 个 UV 三角一致，使用该表第 338 行的 `PV-ACC_Legendary.dtx`。无上下文聚焦导出对这两个动作模型标为 `variant-slot-uv-derived`；提供 `item:21764` 或 `item:22473` 时使用对应 CFT 父模上下文，标为 `motion-parent-slot-derived`。`PV-ACC_CyberPunkCat_m-motion` 的槽 0/1 也分别以 2,310/104 个 UV 三角匹配父模，使用 `PV-ACC_CyberPunkCat.dtx` 和 `_01.dtx`（该表第 153 行）。
- 玩家视角皮肤模型仅在来源与目标所有 LTB 副本 MD5 相同、对应槽 UV 三角集合完全一致、来源槽有直接或上下文表绑定、目标同名 DTX 唯一且所有副本可解码并 SHA-256 一致时推导映射。主槽使用目标同基名 DTX；编号部件槽按来源编号和皮肤名查找配套 DTX；同族表内贴图别名若以来源皮肤名结尾，可沿用其前缀并替换为目标皮肤名，候选路径按下文的唯一性或等价内容规则校验。目标已有绑定或手/臂槽时保持未映射。同族来源可互证；多条候选路径仅在同一目录、所有物理副本均可解码且原始字节 SHA-256 完全一致时，才折叠到按路径排序后的规范路径，其余歧义保持未映射。导出源标为 `variant-slot-uv-derived`；映射审计将等价路径单独标为 `variant-slot-uv-content-equivalent-derived`，并记录内容 SHA-256 和全部候选路径。`PV-M82A1_S_BornBeast` 同字节皮肤基模 `BlackHole`、`BlackRunes`、`CFPass_S1`、`CFPL_SV`、`GunNiang`、`LiveCF`、`MVP20`、`PCGN`、`PFuXi`、`QQMBS`、`TCG`、`TLand` 的槽 2 均映射到各自同名 DTX，每个聚焦导出确认 10 个网格贴图项；`BlackHole`、`BlackRunes`、`CFPass_S1`、`GunNiang`、`LiveCF`、`PCGN`、`PFuXi`、`TCG`、`TLand` 的槽 3 也各自映射到唯一编号 DTX，并导出瞄准镜网格贴图。`CFPL_SV`、`MVP20`、`QQMBS` 的槽 3 现在分别映射到 `PV-M82A1_S_BornBeast01_CFPL_SV.dtx`、`PV-M82A1_S_BornBeast01_MVP20.dtx` 和 `PV-M82A1_S_BornBeast01_QQMBS.dtx`，各自导出一个瞄准镜网格。
- 同一规则聚焦验证了 `PV-M200Cheytac_Dominator` 家族：25 个同字节皮肤变体的槽 2 映射到同名 DTX（共导出 225 个网格贴图项）；其中 22 个模型的槽 3 还映射到唯一编号 DTX（各 1 个瞄准镜网格项）。`_DF`、`_FL`、`_JC` 的槽 3 保持未解析；`_NG_Water` 和 `_SolarWolf` 使用表内直连映射。
- `PV-Knuckle2_InfernalDragon` 家族的 `_22WC`、`_HHUD`、`_Lob`、`_SC`、`_XueJin`、`_ZC` 六个同字节变体，槽 2 均映射到各自同名 DTX；每个聚焦导出确认两个网格贴图项。
- `PV-M4A1_S_Transformers` 家族中，`_21WC`、`_CF15th`、`_CFPL_LGD`、`_CFPL10th`、`_Ms2`、`_PB`、`_Virgo`、`_YUQI`、`_ZDaBaiLu` 九个同字节变体的槽 2 按 UV 推导到各自同名 DTX；`_LDHua`、`_PC` 保留表内直连。聚焦导出确认每个变体均解析 9 个槽 2 网格项；无上下文时手/臂槽仍未解析。
- `PV-AWM_RedFrameHawk` 家族的 `_CP`、`_CPlayers`、`_MGO`、`_NT`、`_Overload`、`_Pinbo`、`_Transport`、`_TV`、`_UFM`、`_XGDH`、`_ZNZ` 十一个同字节变体，槽 2 映射各自同名 DTX，槽 3 映射唯一编号 DTX；每个聚焦导出确认 11 个机身网格项和 1 个瞄准镜网格项，其余未解析项仅为手/臂槽。基模通过 LTC 身份上下文绑定四组贴图：`weaponindex:468` 对应普通款，`3636` 对应 NobleGold，`5789` 对应 NL，`7247` 对应 ALTF4。每组均将 `PViewSkinFileName` / `PViewSkinFileName2` 分别映射到槽 2 / 3；两份不同 MD5 的基模副本都导出 11 个槽 2 网格和 1 个槽 3 瞄准镜，每次仅手/臂 4 项未解析。无上下文时四组贴图冲突，28 个网格项保持未解析。LTC 身份上下文也确认了 `_Phoenix2` 与 `_LLDQ`：`weaponindex:6026` 使用 `PV-AWM_RedFrameHawk_Phoenix2.dtx` / `PV-AWM_RedFrameHawk_01_Phoenix2.dtx`；`6693` 共用 Phoenix2 模型，切换为 `PV-AWM_RedFrameHawk_Bilibili.dtx` / `_Bilibili_01.dtx`。LLDQ 模型的 `7618`、`8046`、`8206` 分别使用 `_LLDQ.dtx` / `_LLDQ_01.dtx`、`_Ink.dtx` / `_Ink_01.dtx`、`_HXS.dtx` / `_HXS_01.dtx`。这些贴图对按 LTC 字段顺序映射槽 2 / 3；五个聚焦导出各解析 11 个槽 2 机身网格和 1 个槽 3 瞄准镜网格，两张贴图均成功导出，手/臂 2 个网格仍未解析。
- `PV-Kukri_Beast` 家族中，`_21WC`、`_FaLang`、`_GC`、`_HwG`、`_QQMBS`、`_Spring`、`_SS`、`_Xiaodingdang` 八个同字节变体按 UV 推导槽 2 到各自同名 DTX；每个聚焦导出恢复一个道具网格贴图项。基模槽 2 按同基名规则映射到 `PV-Kukri_Beast.DTX`，聚焦导出恢复一个道具网格贴图项。`_AD`、`_Chimera`、`_Dsc`、`_LNY2026` 保留表内直连；无上下文的手/臂槽仍未解析。
- `PV-Kar98k_Satellite` 家族的 `_CFPLS26`、`_GunNiang`、`_Hoverquad`、`_LD25`、`_Medusa`、`_Nebula`、`_Parasol`、`_Pinbo`、`_Snake` 九个同字节变体，槽 2 均推导到各自同名 DTX，槽 3 也均映射到编号 DTX。`_GunNiang` 的两条槽 3 候选路径字节相同，审计记录 SHA-256 和两条路径，并按路径排序选用 `PV-Kar98k_Satellite_01_GunNiang.dtx`。每个变体导出 7 个槽 2 网格项；包含 `_Mechanical`、`_NG_Thunder` 在内，11 个槽 3 已映射模型各导出 4 个瞄准镜部件项。基模按 LTC 身份上下文映射：`weaponindex:4328`（同贴图集还包括 4329、7415、8291）使用 `PV-Kar98k_Satellite.dtx` 与 `PV-Kar98k_Satellite_01.dtx`，`weaponindex:4945`（同贴图集还包括 4970）使用 `PV-Kar98k_Satellite_NG.dtx` 与 `PV-Kar98k_Satellite_NG_01.dtx`，`weaponindex:6106` 使用 `PV-Kar98k_Satellite_PC.dtx` 与 `PV-Kar98k_Satellite_PC_01.dtx`。三组聚焦导出均在两份同 MD5 LTB 副本上恢复 7 个槽 2 网格和 4 个槽 3 部件。`_Mechanical`、`_NG_Thunder` 保留表内直连；无上下文时基模的多套贴图保持未解析，手/臂槽仍需角色上下文。
- `PV-QBZ03_Jewelry` 家族的 `_AllStar19`、`_BlackLotus`、`_Champion`、`_Douhu`、`_JJ_Meihua`、`_SPC`、`_TCG` 七个同字节变体，槽 2/3 分别映射到同名 DTX 与唯一编号 DTX；每个聚焦导出确认 5 个机身网格和 `doll` 网格。`_12CZS`、`_Birthday`、`_CFSSC`、`_Creative` 保留表内直连，导出相同网格。基模的 `BF005.LTC` `WeaponIndex:2549` 将 `PViewSkinFileName` 与 `PViewSkinFileName2` 分别绑定到槽 2/3（`PV-QBZ03_Jewelry.DTX` 与 `PV-QBZ03_Jewelry01.DTX`）；用 `weaponindex:2549` 聚焦导出确认 5 个机身网格和 `doll` 均恢复贴图。无角色上下文的手/臂槽仍未解析。
- `PV-Shovel_BornBeast` 家族的 `_Champion`、`_DongMie`、`_Glory`、`_Snake`、`_Wutong` 五个同字节变体，槽 2 推导到各自同名 DTX；`_AD`、`_GG`、`_GongFu`、`_NG_Boss`、`_NG_Cloud` 保留表内直连。基模槽 2 按同基名规则映射到 `PV-Shovel_BornBeast.DTX`。聚焦导出中，每个变体和基模均恢复两个道具网格贴图项；模型没有槽 3 网格，无上下文的手/臂槽仍未解析。
- `PV-DesertEagle_BornBeast_OliveDrab` 与直连皮肤同字节；槽 2 推导到同名 DTX，槽 3 按表内皮肤别名后缀推导到 `PV-Knife_BornBeast_OliveDrab.dtx`。聚焦导出确认 4 个槽 2 网格和 2 个槽 3 网格。其余 8 个变体保留表内直连；基模及无上下文手/臂槽仍未解析。
- M82A1 水枪聚焦多槽导出确认：`PV-M82A1_WaterGun` 基模的 5 个机身网格槽 2 使用 `ModelTextures/PlayerView/PV-M82A1_WaterGun.dtx`，2 个瞄准镜网格槽 3 使用 `ModelTextures/PlayerView/PV-M82A1_WaterGun01.dtx`；`PV-M82A1_WaterGun_jijiahexin` 基模对应使用带 `_jijiahexin` 后缀的两张 DTX。两个模型族的 BL、GR、WOMAN_BL、WOMAN_GR 变体均在槽 3 按完全一致的 UV 映射到各自 `_01.dtx`；前三种变体的槽 2 按 LTC 规则继承，WOMAN_GR 槽 2 按 UV 推导。每个聚焦导出均恢复两张贴图，只剩手/臂项未解析。
- `PV-ZastavaM76` 基模及 BL、GR、WOMAN_BL、WOMAN_GR 变体的聚焦导出确认：槽 2/3 分别映射到 `ModelTextures/PlayerView/PV-ZastavaM76.dtx` 和 `PV-ZastavaM76_01.dtx`。四个变体的槽 3 均按完全一致的 UV 映射；BL、GR、WOMAN_BL 的槽 2 按 LTC 贴图序号继承，WOMAN_GR 的两个槽均按 UV 推导。每个模型导出恢复六个网格槽项中的四项，剩余两项仅为手/臂。
- `PV-G28` 基模及 BL、GR、WOMAN_BL、WOMAN_GR 变体的聚焦导出确认：`Body`、`Reload`、`Mag` 的槽 2 使用 `ModelTextures/PlayerView/PV-G28.dtx`，`HK G28_01` 的槽 3 使用 `PV-G28_01.dtx`。四个变体的槽 3 均按完全一致的 UV 映射；BL、GR、WOMAN_BL 的槽 2 按 LTC 贴图序号继承，WOMAN_GR 的两个槽均按 UV 推导。每个导出均恢复两张道具贴图，剩余两条手/臂项未解析。
- `PV-RI_FALCustom` 基模及 BL、GR、WOMAN_BL、WOMAN_GR 变体的聚焦导出确认：槽 2 使用 `ModelTextures/PlayerView/PV-RI_FALCustom.dtx`，槽 3 使用 `PV-RI_FALCustom_turning_part.dtx`，`Cylinder01` 的槽 4 使用 `PV-RI_FALCustom_ammo.dtx`。变体槽 2 按 LTC 贴图序号继承，槽 3/4 按完全一致的 UV 推导。每个导出均恢复三张道具贴图，只剩两条手/臂项未解析。
- `PV-FRF2_Festa_CFS2025` 基模及 BL、GR、WOMAN_BL、WOMAN_GR 变体的聚焦导出确认：槽 2 使用 `ModelTextures/PlayerView/PV-FRF2_Festa_CFS2025.dtx`，`FR-F2_Freezing_02` 的槽 3 使用 `PV-FRF2_Festa_CFS2025_01.dtx`。变体槽 2 按 LTC 贴图序号继承，槽 3 按完全一致的 UV 推导。每个导出均恢复两张道具贴图，只剩两条手/臂项未解析。
- `PV-Gunslinger_SPEmotion` 基模及 BL、GR、SP、WOMAN_BL、WOMAN_GR 变体的聚焦导出确认：`Kar98k` 网格槽 2 使用 `ModelTextures/PlayerView/PV-kar98k.dtx`，两个 `Colt Anaconda` 网格槽 3 使用 `pv-anaconda.dtx`。五个变体的两个道具槽均按完全一致的 UV 推导。每个导出均恢复两张道具贴图，只剩两条手/臂项未解析。
- `PV-G36C_Specular` 和 `PV-G36C_Specular_Carbine` 两个基模的 LTC 均将槽 2/3 直接映射到 `PV-G36C_Specular.dtx`、`PV-G36C_Specular01.dtx`。两组 BL、GR、WOMAN_BL、WOMAN_GR 变体都通过 `ltc-preview-variant-derived` 按 LTC 贴图序号继承这两张 DTX；聚焦导出均解析 11 个道具网格槽，剩余两项是手/臂槽。
- `PV-FireTipped_Spear_Nezha` 基模及 BL、GR、WOMAN_BL、WOMAN_GR 变体的槽 2，均按完全一致的 UV 三角集合复用直接映射的 `Models/PLAYERVIEW/NANO4TERMINATOR/PV-FireTipped_Spear_Nezha` 基模贴图 `ModelTextures/PLAYERVIEW/PV-FireTipped_Spear.dtx`。每个聚焦导出均恢复道具贴图，只剩手、臂槽未解析。
- `PV-WAVE-Mortar` 嵌套目录基模的槽 2 直接映射到 `ModelTextures/PlayerView/PV-WAVE-Mortar.dtx`；标准路径基模通过完全一致的 UV 继承该槽，BL、GR、WOMAN_BL、WOMAN_GR 变体则按规范化基名映射同一贴图。各导出均为 `body`、`bomb` 网格恢复道具贴图，仅手、臂槽未解析。
- `PV-500Chainsaw` 基模槽 2 直接使用 `ModelTextures/PlayerView/PV-500Chainsaw.dtx`；BL、GR、WOMAN_BL、WOMAN_GR 变体均按 LTC 贴图序号继承槽 2；WOMAN_GR 的 Object05 臂槽由同归档 Arm_WOMAN_GR 中同名网格的 RenderStyle 和 UV 三角签名确认。五个模型的导出都恢复全部三条槽 2 网格贴图，只有手、臂槽仍未解析。
- `PV-687EDP_NY2026` 基模的 7 个槽 2 网格及 BL、GR、WOMAN_BL、WOMAN_GR 变体各自的 5 个槽 2 网格，均映射到 `ModelTextures/PlayerView/PV-687EDP_NY2026.dtx`。基模为直接映射，四个变体按 LTC 贴图序号继承；每个导出仅剩两条手、臂项未解析。
- `PV-NegevNG7` 基模的 8 个槽 2 网格使用 `PV-NegevNG7.dtx`，`scope` 网格槽 3 使用 `PV-NegevNG7_01.dtx`。BL、GR、WOMAN_BL、WOMAN_GR 变体均按 LTC 贴图序号继承槽 2/3；WOMAN_GR 的 Object05 臂槽由同归档 Arm_WOMAN_GR 中同名网格的 RenderStyle 和 UV 三角签名确认。四个变体的聚焦导出都恢复 8 个槽 2 网格和 scope 槽 3，贴图位于 `ModelTextures/PlayerView`；手、臂项仍未解析。
- `PV-MiniMythicHero` 基模及 BL、GR、WOMAN_BL、WOMAN_GR 变体的 `Object034` 网格槽 2 使用 `ModelTextures/PlayerView/PV-MiniMythicHero.dtx`，`sword` 网格槽 3 使用 `PV-MiniMythicHero_01.dtx`。四个变体的槽 2 按 LTC 贴图序号继承，槽 3 按完全一致的 UV 映射。每个导出均恢复两张道具贴图，仅手、臂槽未解析。
- BF005.LTC 第 841 行为 `PV-TAC15` 登记的两张第一人称贴图。基模 `TAC_Bullit`、`TAC_reload`、`TAC15` 的槽 2 映射到 `ModelTextures/PlayerView/PV-TAC15_01.dtx`；第二条 `_02.dtx` 引用需要槽 3，而基模没有该道具槽。使用 `weaponindex:841` 的聚焦导出确认三个网格均导出 `_01`，手、臂槽仍未解析。`PV-TAC15_WOMAN_BL/GR` 继续按网格槽和 UV 签名将 `body`、`bolt`、`Object001` 槽 2 映射到 `_01.dtx`，`scope` 槽 3 映射到 `_02.dtx`；每个变体恢复四个道具网格贴图。
- BF005.LTC 的 `PV-CubeBow` 记录（`WeaponIndex` 1040–1044、1106）将槽 2 的 `body`、`bolt`、`Object001` 映射到 `PV-TAC15_01.DTX`；基模和 BL/GR/WOMAN_BL/WOMAN_GR 五个模型均通过聚焦导出。第二字段 `PV-TAC15_02.DTX` 对应的槽 3 不存在，因此保留未映射；手、臂槽需要角色上下文。
- `PV-464SPX` 基模及 BL、GR、WOMAN_BL 变体的 5 个槽 2 网格均使用 `ModelTextures/PlayerView/PV-464SPX.dtx`，BL、GR、WOMAN_BL、WOMAN_GR 变体均按 LTC 贴图序号继承槽 2；聚焦导出确认 WOMAN_GR 的 5 个槽 2 网格都映射到同一 DTX。各模型的手、臂槽未解析。
- `PV-HK417_Elite_RoyalDragon6` 基模的 3 个槽 2 网格直接使用 `ModelTextures/PlayerView/PV-HK417_Elite_RoyalDragon6.dtx`；BL、GR、WOMAN_BL、WOMAN_GR 变体均按 LTC 贴图序号继承同一贴图；聚焦导出确认 WOMAN_GR 的 3 个槽 2 网格均已映射。所有模型的手、臂槽仍未解析。
- 四个 `PV-M82A1_S_ReactiveArmor` 阵营/性别变体的两份 LTB 副本均按 LTC 贴图序号恢复 18 条槽 2 机身项，并按完全一致的 UV 映射两条 `PV-Scope` 槽 3 项。使用 `weaponindex:4437` 聚焦导出时，每个变体解析 20/24 项；剩余四项为手、臂槽。基模槽 2/3 均有直接贴图。
- `PV-M82A1_DemonicBeast` 基模的 7 个槽 2 网格和两个槽 3 网格分别直接映射到 `ModelTextures/PlayerView/PV-M82A1_DemonicBeast.dtx` 与 `_01.dtx`。BL、GR、WOMAN_BL、WOMAN_GR 变体按 LTC 贴图序号继承两个槽的映射，每个导出恢复全部 9 个道具网格槽；手、臂和 `clanmark` 网格仍未解析。
- `PV-M82A1_DemonicBeast_PVE` 基模两份 LTB 副本共映射 14 个槽 2 项和 4 个槽 3 项。BL、GR、WOMAN_BL、WOMAN_GR 变体也按 LTC 贴图序号恢复这 18 项；每份 24 行导出报告剩下 6 项，即双副本中的手、臂和 `clanmark` 网格。
- `PV-M82A1_S_BornBeast_Winter` 基模槽 2 使用 `ModelTextures/PlayerView/PV-M82A1_S_BornBeast_Winter.dtx`，槽 3 使用 `ModelTextures/PlayerView/PV-M82A1_S_BornBeast_Winter_01.dtx`。四个阵营/性别变体保留 LTC 推导的槽 2 贴图，并在 `dsdsdsfs` 网格 UV 完全一致时将槽 3 映射到 `_01.dtx`。每个聚焦导出均恢复两张贴图，只留下两条手/臂项未解析。
- `WeaponPreview/Skin` 变体只有在预览 BIN 唯一对应同名 PV 模型、基模有直接槽位贴图、目标武器网格名与基模名相同且所有模型副本的几何/UV 签名一致时，才会绑定唯一同名 DTX；贴图副本的 MD5 也必须一致。绑定使用 `preview:<模型组号>:<道具编号>`，不据此生成可能与 LTC `WeaponIndex` 冲突的 `item:` 身份。若某模型槽只有一个变体上下文，且该槽没有直接路径或身份上下文映射，CLI 和 GUI 无上下文导出也会按模型路径使用该 DTX。BCAxe 的 Chs/YOD 槽 2 是已验证样例，审计标记为 `weapon-preview-variant-derived`。
- `PV-BCAxe_Beast_12CZS` 基模及 `_BL`、`_GR`、`_WOMAN_BL`、`_WOMAN_GR` 的槽 2 均映射到唯一同基名贴图 `PV-BCAxe_Beast_12CZS.DTX`，审计标记为 `preview-normalized-basename-derived`。五个模型都导出该道具贴图；其余十条未解析项是手、臂网格。
- BF005.LTC `WeaponIndex:7434` 将 `PV-BCAxe_Beast_PrGlow` 基模的槽 2 映射到 `PV-BCAxe_Beast_PrGlow.DTX`；BL、GR、WOMAN_BL、WOMAN_GR 变体按 LTC 贴图序号继承。五个模型都导出道具贴图；其余十条未解析项是手、臂网格。
- `Arm_SwatElite_Renewal_GR` 的手/臂网格带有明确的角色和阵营后缀，可按 CharacterType.CFT 中登记的 SWAT-ELITE 手/臂贴图名解析；仅当对应玩家视角 DTX 路径唯一且归档副本哈希一致时绑定。A0017 与 A0074 的聚焦导出均验证手槽 0 使用 `FVIEW_HAND_SWAT-ELITE_BL.DTX`、臂槽 1 使用 `FVIEW_ARM_SWAT-ELITE_GR.DTX`，两张贴图均成功解码和导出。`Arm_WildWest_SP` 在当前游戏资源清单中只有一份模型，所有 CFT 模型/贴图字段均无其路径或 `WildWest_SP` 贴图引用；盘点到的八张 WildWest 手/臂 DTX 只属于角色表登记的 BLGR 与 Santa。SP 的两个网格与其他臂模均无 UV、索引、几何三项签名全同的供体；A9350 聚焦导出仍有两个槽缺图，因此保持未映射。明细见 `artifacts/mapping-audit/wildwest-sp-binding-audit-20261026.md`。
- BF005.LTC 的 `WeaponIndex:2473` 将 `PV-Glock18C-DigitalCamo_BR` 槽 2 的三个网格映射到 `ModelTextures/PlayerView/pv-Glock18C_DigitalCamo.dtx`。`_BR_BL`、`_BR_GR`、`_BR_WOMAN_BL` 及内容完全相同的反序命名 `_BL_BR`、`_GR_BR`、`_WOMAN_BL_BR` 均在该上下文中恢复三个槽 2 网格；反序名称只有在两种命名下的所有 LTB 副本 SHA-256 一致时才继承。WOMAN_GR 的两种名称顺序均未通过当前槽位判定，手、臂槽仍需角色上下文。
- `PV-SU_MP5_Silencer` 系列没有 LTC/CFT 直接绑定。其槽 UV 与 MP5 消音器模型部分相同，但现有 MP5 贴图包含多种不同皮肤；在缺少表记录确认皮肤身份时保持未映射。
- LTC 已验证的 SixEyed Sword 条目使用相同 PView 模型和身份，但贴图名分别写作 `PV-SixEyedSword_Gold` 与 `PV-SixEyed_Sword_Gold`；后者只在无下划线 basename 对应唯一物理路径时回链。
- `WeaponAttachItemDoll.CFT` 的 Joy、TropicalOcean 与 PartyDoggy 配件身份已验证：Joy 的 PV/QV 模型在 `item:13869` 下使用 `PV-ACC_Baihu.dtx` / `QV-ACC_Baihu.DTX`，QV Joy 在 `item:18107` 下也使用 `QV-ACC_Baihu.DTX`；TropicalOcean 的 PV/QV 模型在 `item:16279` 下使用同一对贴图；PartyDoggy 的 PV/QV 模型在 `item:13451` 下使用同一对贴图。六个聚焦导出各解析一张贴图且无缺失。当前安装包中 PV/QV Baihu DTX 路径各有一个物理副本，MD5 分别为 `A4990BD4A7482FA304C5D7AD6961EDAC` 和 `C9096544DB5FD886D1A963D841E5F16E`。
- 当前安装的 `WeaponPreview` 在模型组 `3000000` 有 31 条 `ModelId=0` 记录，涉及 9 个道具；每个道具均对应唯一同组 `WeaponModel` 行。`item:<道具编号>` 聚焦导出验证了以下 LTC→PView 贴图槽：

  | 道具编号 | WeaponModel.ModelId | PView 模型 | 已解析槽位与贴图 |
  | --- | ---: | --- | --- |
  | 730 | 730 | `PV-AK-47-Beast` | 2：`PV_AK47-Beast.DTX` |
  | 813 | 813 | `PV-RPK_Infernal_Dragon` | 2：`PV-RPK_Infernal_Dragon.DTX` |
  | 1269 | 1269 | `PV-AK47_K_IronBeast` | 2：`PV-AK47_K_IronBeast.DTX` |
  | 1508 | 1508 | `PV-M4A1_Silencer_Predator` | 2：`PV-M4A1_Silencer_Predator01.DTX`；3：`PV-M4A1_Silencer_Predator.DTX` |
  | 1616 | 1616 | `PV-AK47_Knife_BornBeast` | 2：`PV-AK47_Knife_BornBeast.DTX`；3：`PV-AK47_Knife_BornBeast_02.DTX` |
  | 1919 | 1919 | `PV-AK12_K_IronSpider` | 2：`PV-AK12_K_IronSpider.DTX` |
  | 1939 | 1111 | `PV-M82A1_S_BornBeast` | 1：`PV-M82A1_S_BornBeast.DTX`；2：`PV-M82A1_S_BornBeast01.DTX` |
  | 1945 | 1298 | `pv-AWM_UltimateGold` | 2：`PV-AWM_UltimateSiversmith.DTX` |
  | 1997 | 1997 | `PV-AN94_SpaceShip` | 2：`PV-AN94_SpaceShip.DTX` |

  某些模型的其他槽仍未解析。LTC 贴图路径若在多个归档层有同名实体副本，DTX 副本均解码为相同尺寸和像素时可选择该路径；其他格式仍要求 SHA-256 一致。无法确认内容时保持未映射。
- 模型组 `3000000` 的道具 `812` 有预览编号冲突：唯一的 `WeaponModel.ItemId` 行关联 `ModelId:812` 和 LTC `WeaponIndex:812`，但 `WeaponPreview` 记录的 `ModelId` 是 `1255`。`item:812` 聚焦导出确认 `PV-AWM_A_InfernalDragon` 槽 2、3 分别使用 `PV-AWM_A_InfernalDragon.DTX`、`PV-AWM_A_InfernalDragon01.DTX`；LTC 预览模型 `QV-AWM_InfernalDragon_preview` 槽 0 使用前一张 PV 贴图。BF005.LTC 为 QV 男、女世界模型槽 0 指定 `QV-AWM_A_InfernalDragon.DTX`，但 `rez`、`rez2` 的同路径 DTX 分别解码为 1024×1024 和 256×256、像素也不同，槽位报告保持未解析；OBJ 中由文件名推断出的贴图不算已确认绑定。`preview:3000000:812` 下的 `PV-AWM_A_InfernalDragon` 仍未映射；同一上下文对 `PV-BrokenKukri_RedCrystal` 槽 2 有独立 LTC 绑定，不会套用到 AWM 模型。手、臂槽仍未解析；六份目标 LTB 均成功解码，无几何跳过。报告位于 `artifacts/mapping-audit/item-812-focused-20261025/`。
- 道具 `856`、`1111`、`2164` 的 `WeaponModel.ItemId` 分别关联同号 ModelId 与 LTC WeaponIndex；其 `WeaponPreview.ModelId` 则分别是 `1298`、`1650`、`3320`。使用 `item:<道具编号>` 可取得以下 LTC 映射：

  | 道具编号 | PView 贴图槽 | LTC 预览模型槽 0 | QV 世界模型槽 0 |
  | ---: | --- | --- | --- |
  | 856 | `PV-M4A1_S_Transformers.DTX`（槽 2） | `QV-M4A1_S_IronBeast_preview` → 同名 PV DTX | `QV-M4A1_S_Transformers.DTX` 路径明确，但 `rez`、`rez2` 副本解码后的尺寸和像素不同，仍未解析 |
  | 1111 | `PV-M82A1_S_BornBeast.DTX`（槽 1）；`PV-M82A1_S_BornBeast01.DTX`（槽 2） | `QV-M82A1_S_BornBeast_preview` → `PV-M82A1_S_BornBeast.DTX` | `QV-M82A1_S_BornBeast.DTX` 路径明确，但 `rez`、`rez2` 副本解码后的尺寸和像素不同，仍未解析 |
  | 2164 | `PV-GatlingGun_InfernalDragon.DTX`（槽 2） | `QV-GatlingGun_InfernalDragon_preview` → 同名 PV DTX | `QV-GatlingGun_InfernalDragon.DTX` |

  道具 `1939` 也使用 LTC `WeaponIndex:1111`。三项 `preview:3000000:<道具编号>` 聚焦导出均未解析贴图；不能用预览编号替代道具身份。13 份目标 LTB 均成功解码，无几何跳过；手、臂槽仍未解析，`item:1111` 的 PView 槽 3 也未解析。报告位于 `artifacts/mapping-audit/item-mismatch-856-1111-2164-20261025/`。
- 道具 `1394`、`1620`、`1690` 的 `item:` 与 `preview:` 指向不同的 LTC 武器记录，需按上下文分别查询：

  | 道具编号 | `item:` 身份和映射 | `preview:` 身份和映射 |
  | ---: | --- | --- |
  | 1394 | `WeaponIndex:1394` 为 M82A1 IronShark；PView 槽 2、3 分别用 `PV-M82A1-IronShark.DTX`、`PV-M82A1-IronShark_01.DTX`。QV 世界贴图 `QV-M82A1-IronShark.DTX` 路径存在，但 `rez` 与 `rez2` 副本解码后的尺寸和像素不同，槽 0 未解析。 | `ModelId:2013` 为 COP357 GoldPhoenix；`preview:3000000:1394` 的 QV 槽 0 用 `QV-COP357_GoldPhoenix.DTX`，PView 槽 2 用 `PV-COP357_GoldPhoenix.DTX`。 |
  | 1620 | `WeaponIndex:1620` 为 Shovel BornBeast；PView 槽 2 用 `PV-Shovel_BornBeast.DTX`。QV 世界贴图 `QV-Shovel_BornBeast.DTX` 的 `rez2`、`rez4` 副本均为 1024×1024 且像素相同，`rez` 副本为 256×256 且像素不同，因此全路径副本仍冲突，槽 0 未解析。 | `ModelId:2390` 为 AWM-A Infernal Dragon Punk；`preview:3000000:1620` 的男、女 QV 模型槽 0 用 `QV-AWM_A_InfernalDragon_Punk.DTX`，PView 槽 2、3 用 `PV-AWM_A_InfernalDragon_Punk.DTX`、`PV-AWM_A_InfernalDragon_Punk_01.DTX`。 |
  | 1690 | `WeaponIndex:1690` 为 Knuckle2 Infernal Dragon。LTC 将 M_R/F_R 世界模型槽 0 关联到 `QV-Knuckle2_InfernalDragon.DTX`，将 M_L/F_L 左手模型及预览模型关联到 `PV-Knuckle2_InfernalDragon.DTX`；PView 基模槽 2 也用该 PV 贴图。QV DTX 的 `rez`、`rez2` 副本解码后的尺寸和像素不同，因此右手世界模型槽 0 未解析。 | `ModelId:2508` 为 MP5KA4 Halloween2016；`preview:3000000:1690` 的 QV 槽 0 用 `QV-MP5KA4_darkgray.DTX`，PView 槽 2 用 `PV-MP5KA4_darkgray.DTX`。 |

  三项 `item:` 与 `preview:` 聚焦导出中的手、臂槽均未解析，所有目标 LTB 均成功解码且无几何跳过。报告位于 `artifacts/mapping-audit/item-mismatch-1394-1620-1690-20261025/`。
- 道具编号 `1003` 的预览记录在 `3000000`、`6000000`、`8000000` 组都指向 `ModelId:1536`，对应 LTC `WeaponIndex:1536`。但 `1003` 本身也是 LTC `WeaponIndex`，指向 Kukri Beast；LTC 旧映射编号 `1003` 还关联到 `WeaponIndex:831`（AK47-QT），因此不创建可能串到其他武器的 `item:1003` 别名。使用 `preview:3000000:1003` 可明确选中 1536：聚焦导出将 `QV-PoliceBaton_UltimateGold` 槽 0 映射到 `QV-PoliceBaton_Ultimatesilver.DTX`，无缺失贴图且无几何跳过。来源审计见 `artifacts/mapping-audit/weapon-preview-crosswalk-current.tsv`，导出记录见 `artifacts/mapping-audit/item-1003-focused-root-20261026/item-1003-preview-QV_texture_slots.tsv`。
- 道具 `2573` 的 `WeaponModel.ModelId:4553` 与 `WeaponPreview.ModelId:4553` 均对应 LTC `WeaponIndex:4553`。其 `ProjectileModelFileName` 指向 `QV-DualDaphneSaber_Projectile`，`ProjectileSkinFileName` 将槽 0 映射到 `QV-ThrowDagger.DTX`；`item:2573` 和 `preview:3000000:2573` 两个上下文的聚焦导出均成功，单个网格无缺失贴图且无几何跳过。报告位于 `artifacts/mapping-audit/item-mismatch-2573-projectile-20261025/`。
- 模型组 `2000000` 的道具 `56`、`1918`、`1938` 各自的 `WeaponModel.ModelId` 与预览 `ModelId` 相同，并对应同号 LTC `WeaponIndex`：`item:56` 和 `preview:2000000:56` 均将 `QV-knuckle2_F_R`、`QV-knuckle2_M_R` 槽 0 映射到 `QV-Knuckle2.DTX`；`item:1918` 将 H_NVGHelmet 男、女模型槽 0 映射到 `H_NVGHelmet.DTX`；`item:1938` 将 `QV-MultiTool_Defuser` 槽 0 映射到 `QV-MultiTool_Defuser.DTX`。所有聚焦导出均无缺失贴图、无几何跳过。报告位于 `artifacts/mapping-audit/utility-item-56-knuckle2-20261025/` 和 `artifacts/mapping-audit/utility-items-1918-1938-20261025/`。
- 道具 `3638` 没有 `WeaponModel` 关联，而且 `3638` 本身是 LTC 中 AK47 RedRifleBeast 的 `WeaponIndex`，因此 `item:3638` 不会被借用为预览记录的 `ModelId:6903`。模型组 `3000000`、`8000000`、`10000000` 的预览记录均以 `ModelId:6903` 对应 LTC `WeaponIndex:6903`；三个 `preview:` 上下文都将 `QV_NANO_KNIFE` 槽 0 映射到 `QV_NANO_KNIFE.DTX`，单网格成功导出贴图且无几何跳过。报告位于 `artifacts/mapping-audit/item-3638-nanoknife-20261025/`。
- 道具 `3080` 没有 `WeaponModel` 关联。模型组 `3000000`、`8000000` 的预览记录均以 `ModelId:5422` 对应 LTC `WeaponIndex:5422`。该 LTC 行将 `ModelFileName` 绑定到 `QV-D.E_DemonicBeast_R`，将 `LeftHandModelFileName` 绑定到 `QV-D.E_DemonicBeast_L`。合并 `item:3080` 与 `preview:3000000:3080` 上下文后，槽 0 分别映射到 `QV-D.E_DemonicBeast.DTX` 和 `PV-D.E_DemonicBeast.DTX`；两张贴图均成功导出，缺失贴图数和跳过网格数均为 0。此结果确认了两个模型的槽 0；LTC 同行的 `...01.DTX` 还没有独立槽位证据。报告位于 `artifacts/mapping-audit/item-3080-demonicbeast-20261025/` 和 `artifacts/mapping-audit/demonicbeast-dual-context-20261026/`。
- `WeaponAttachItemDoll.CFT` 的记录 6、22、31 分别为道具 `3459` MysteryBoy、`4743` KnitPengu 和 `5409` Djehuti BL。解析器现将 CFT 中明确的道具编号关联到同道具的预览上下文：`3459`、`5409` 的模型组为 `6000000`，`4743` 的模型组为 `3000000`、`8000000`。

  | 道具 | 模型组 | PV 槽 0 | QV 槽 0 |
  | --- | --- | --- | --- |
  | 3459 | 6000000 | `PV-ACC_SD_MYSTERYBOY.DTX` | `QV-ACC_SD_MYSTERYBOY.DTX` |
  | 4743 | 3000000、8000000 | `PV_ACC_KNITPENGU.DTX` | `QV_ACC_KNITPENGU.DTX` |
  | 5409 | 6000000 | `PV-ACC_SD_Djehuti_BL.DTX` | `QV-ACC_SD_Djehuti_BL.DTX` |

  `4743` 的 `ModelId:9834` 在 LTC 中无匹配记录，另外两件的 `ModelId` 为 0。所有道具和预览上下文的聚焦导出均成功导出两张贴图，无缺失贴图或几何跳过。报告位于 `artifacts/mapping-audit/cft-attachments-3459-5409-20261025/` 和 `artifacts/mapping-audit/item-4743-acc-knitpengu-20261025/`。
- 模型组 `3000000` 的 CowRifle 两个道具共用 `PV-CowRifle_MOS_AK47`，但贴图身份不同：`item:2554` 经 `WeaponModel.ModelId:4555` / LTC `WeaponIndex:4555` 使用 `QV-CowRifle_MOS_AK47_NobleGold.DTX` 和 `PV-CowRifle_MOS_AK47_NobleGold.DTX`；`item:2575` 经 `ModelId:4554` / `WeaponIndex:4554` 使用不带 `NobleGold` 的两张 DTX。两者的 QV 男、女世界模型槽 0 均已聚焦导出验证；2554 的 `QV-CowRifle_MOS_AK47_NobleGold_preview` 槽 0 使用 PV NobleGold DTX。PView 基模槽 2 直接映射各自 DTX，BL 变体槽 2 按 LTC 规则推导同一贴图；GR、WOMAN_BL、WOMAN_GR 变体尚未通过完整副本和槽位布局校验，保持未映射。五个 PView 模型的 10 份物理副本均已解码，无几何跳过；世界模型导出及 `item:2554` 的 NobleGold 预览模型导出均无缺失贴图。报告位于 `artifacts/mapping-audit/item-1399-flashbang-focused-20261025/`。
- 模型组 `3000000` 的 HuluXiongdi 道具经同组 `WeaponModel` 关联到以下 LTC 武器记录：

  | 道具编号 | WeaponIndex | QV 世界槽 0 | PView 贴图槽 |
  | --- | ---: | --- | --- |
  | 2589 | 4534 | `QV-HuluXiongdi_Chg.DTX` | `PV-HuluXiongdi_chg` 槽 2：`PV-HuluXiongdi_Chg.DTX` |
  | 2595 | 4533 | `QV-HuluXiongdi.DTX` | `PV-HuluXiongdi_chg` 槽 2：`PV-HuluXiongdi.DTX` |
  | 2591、2602 | 4532、4530 | `QV-HuluXiongdi_Chg.DTX` | `PV-HuluXiongdi` 槽 2：`PV-HuluXiongdi_Chg.DTX`；槽 3：`PV-HuluXiongdi01.DTX` |
  | 2612、2626 | 4531、4529 | `QV-HuluXiongdi.DTX` | `PV-HuluXiongdi` 槽 2：`PV-HuluXiongdi.DTX`；槽 3：`PV-HuluXiongdi01.DTX` |
  | 2705 | 4573 | `QV-Grenade_HuluXiongdi.DTX` | `PV-Grenade_HuluXiongdi` 槽 2：`PV-Grenade_HuluXiongdi.DTX` |

  `PV-HuluXiongdi` 的 BL、GR、WOMAN_BL、WOMAN_GR 变体均以 UV 推导槽 3 的 `PV-HuluXiongdi01.DTX`，槽 2 均按 LTC 变体规则映射。`PV-HuluXiongdi_chg` 的四个变体槽 2 均按道具上下文映射；WOMAN_GR 的 `Object05` 槽 1 已通过跨资源层 UV 三角布局匹配识别为手臂，手部槽 0、手臂槽 1 和 `Hulu01`、`Hulu02` 槽 3 仍无可靠贴图来源。`PV-Grenade_HuluXiongdi` 的四个阵营/性别变体槽 2 均按 UV 推导。WOMAN_GR 的定向导出确认道具 2589、2591 使用 `PV-HuluXiongdi_Chg.DTX`，2595、2612 使用 `PV-HuluXiongdi.DTX`，均落在槽 2。所有目标模型均已解码，无几何跳过。报告位于 `artifacts/mapping-audit/item-2589-hulu-xiongdi-focused-20261025/`，跨层核对记录位于 `artifacts/mapping-audit/hulu-crossroot-arm-fallback-20261026.md`。
- 模型组 `3000000` 的白樱道具也有 LTC 直连映射：

  | 道具编号 | WeaponIndex | QV 世界槽 0 | PView 贴图槽 |
  | --- | ---: | --- | --- |
  | 2593、2597 | 4520、4521 | `QV-M200CheyTac_WhiteSakura.DTX` | `PV-M200CheyTac` 槽 2：`PV-M200CheyTac_WhiteSakura.DTX`；槽 3：`PV-M200CheyTac_WhiteSakura01.DTX` |
  | 2599 | 4522 | `QV-WarFan_WhiteSakura.DTX` | `PV-WarFan` 槽 2：`PV-WarFan_WhiteSakura.DTX` |

  `PV-M200CheyTac` 的 BL、GR、WOMAN_BL 变体按 LTC 规则映射槽 2、3；WOMAN_GR 两槽保持未解析。`PV-WarFan` 的 BL、GR、WOMAN_BL、WOMAN_GR 变体均以 UV 推导槽 2。手、臂槽仍未解析。两组共 20 份物理 LTB 均成功解码，无几何跳过；报告位于 `artifacts/mapping-audit/item-2593-m200-whitesakura-exact-20261025/`。
- TangDynastySword 的三个道具上下文对应不同 LTC 武器记录。聚焦导出确认 QV 世界模型槽 0、L 模型槽 0 和 PView 槽 2 的贴图：

  | 道具编号 | WeaponIndex | QV 世界槽 0 | L 模型槽 0 | PView 槽 2 |
  | --- | ---: | --- | --- | --- |
  | 1904 | 3059 | `QV-TangDynastySword_Silver.DTX` | `PV-TangDynastySword_Silver.DTX` | `PV-TangDynastySword_Silver.DTX` |
  | 2551、2583 | 4548、4549 | `QV-TangDynastySword_Jochulmolyeo.DTX` | `PV-TangDynastySword_Jochulmolyeo.DTX` | `PV-TangDynastySword_Jochulmolyeo.DTX` |
  | 2577、2581 | 4552、4551 | `QV-TangDynastySword_Yongmailsaeng.DTX` | `PV-TangDynastySword_Yongmailsaeng.DTX` | `PV-TangDynastySword_Yongmailsaeng.DTX` |

  `PV-TangDynastySword` 的 BL、GR、WOMAN_BL、WOMAN_GR 变体槽 2 均按 UV 推导 Silver DTX。Jochulmolyeo 与 Yongmailsaeng 的 PView 基模槽 2 已验证；其四个阵营/性别变体目前没有通过槽位推导，保持未解析。三组手、臂槽也未解析。29 份物理 LTB 均成功解码，无几何跳过；报告位于 `artifacts/mapping-audit/item-1904-tangdynasty-focused-20261025/`。
- 模型组 `2000000` 的 HuluXiongdi 道具 `2681`、`2686` 经 `WeaponModel.ModelId` 4570、4571 关联到 LTC `WeaponIndex` 4570、4571。`QV-M4A1_S_IronBeast_HuluXiongdi` 槽 0 使用同名 QV DTX；`QV-M4A1_S_IronBeast_HuluXiongdi_Preview` 槽 0 使用 `PV-M4A1_S_IronBeast_HuluXiongdi.DTX`。PView 基模槽 2 使用该 PV DTX；BL、GR、WOMAN_BL 变体槽 2 继承同一贴图，槽 3 映射 `PV-M4A1_S_IronBeast_HuluXiongdi_01.DTX`。WOMAN_GR、手臂及其他无唯一来源的网格槽仍未解析。七个目标 LTB 均成功解码，无几何跳过；报告位于同一 HuluXiongdi 聚焦目录。
- 模型组 `2000000` 的 `WeaponPreview` 记录 35、92、200 分别经同组 `WeaponModel` 指向 `ModelId` 3020、3021、3022；三者共用 `PV-AK47_Knife_SteelEmpire`。当前安装的聚焦导出验证了每个身份的两个物品槽：

  | 道具编号 | WeaponModel.ModelId | 槽 2 | 槽 3 |
  | ---: | ---: | --- | --- |
  | 35 | 3020 | `pv-AK47_Knife_SteelEmpire_ng.DTX` | `pv-AK47_Knife_SteelEmpire_ng01.DTX` |
  | 92 | 3021 | `PV-AK47_Knife_SteelEmpire_NobleRed.DTX` | `PV-AK47_Knife_SteelEmpire_NobleRed01.DTX` |
  | 200 | 3022 | `PV-AK47_Knife_SteelEmpire_NobleGRN.DTX` | `PV-AK47_Knife_SteelEmpire_NobleGRN01.DTX` |

  三种上下文各导出 13 个网格，11 个武器网格成功解析贴图；两个手臂槽仍未绑定。三组 DTX 均在当前安装中找到并成功导出。
- 模型组 `2000000` 的 M4A1-S UnderTech 贴图已按当前 `WeaponPreview`、`WeaponModel` 和 LTC 记录核对：

  | 道具编号 | 表关联 | PView 模型 | 世界模型槽 0 | PView 槽 2 |
  | --- | --- | --- | --- | --- |
  | 2190、2192、2194、2195 | `WeaponModel.ModelId`：3419、3423、3425、3426 | `PV-M4A1_S_UnderTech` | `QV-M4A1_S_UnderTech_IvoryRed.DTX` | `PV-M4A1_S_UnderTech_IvoryRed.DTX` |
  | 2191、2193、2196 | `WeaponModel.ModelId`：3421、3424、3427 | `PV-M4A1_S_UnderTech_Chg` | `QV-M4A1_S_UnderTech_IvoryRed.DTX` | `PV-M4A1_S_UnderTech_IvoryRed.DTX` |
  | 3746 | 预览模型/`WeaponIndex`：7404；用 `preview:2000000:3746`，无匹配的 `WeaponModel.ItemId` 行 | `PV-M4A1_S_UnderTech` | `QV-M4A1_S_UnderTech_Gold.DTX` | `PV-M4A1_S_UnderTech_Gold.DTX` |
  | 3749 | 预览模型/`WeaponIndex`：7406；用 `preview:2000000:3749`，无匹配的 `WeaponModel.ItemId` 行 | `PV-M4A1_S_UnderTech_Chg` | `QV-M4A1_S_UnderTech_Gold.DTX` | `PV-M4A1_S_UnderTech_Gold.DTX` |

  2190–2196 使用 `item:<编号>` 上下文。BL、GR、WOMAN_BL 变体会从对应 LTC 记录推导槽 2 贴图；WOMAN_GR 在当前 LTC 中没有对应模型记录，槽 2 保持未解析。手、臂槽也未绑定。四组聚焦导出均成功解码 QV 与 PV DTX。
- 模型组 `2000000` 的 M4A1-S RedBeast 道具 `1895`、`1896`、`1897` 经同组 `WeaponModel.ModelId` 3050、3051、3052 关联到 LTC：

  | 道具编号 | WeaponModel.ModelId | PView 模型 | 世界模型槽 0 | PView 槽 2 |
  | --- | --- | --- | --- | --- |
  | 1895、1897 | 3050、3052 | `PV-M4A1_S_RedKnifeBeast` | `QV-M4A1_S_RedRifleBeast.DTX` | `PV-M4A1_S_RedRifleBeast.DTX` |
  | 1896 | 3051 | `PV-M4A1_S_RedRifleBeast` | `QV-M4A1_S_RedRifleBeast.DTX` | `PV-M4A1_S_RedRifleBeast.DTX` |

  LTC 将 RedKnifeBeast 世界/PView 模型绑定到 RedRifleBeast 贴图。`QV-M4A1_S_RedRifleBeast_preview` 的槽 0 也使用 PV DTX。两种 PView 基模的 BL、GR、WOMAN_BL 变体均推导出槽 2 贴图；WOMAN_GR 保持未解析。`QV-M4A1_S_RedKnifeBeast_preview` 及手、臂槽未解析。聚焦导出成功解码 QV 和 PV 两张 DTX。
- 道具 `1002` 在模型组 `6000000`、`8000000`、`10000000` 的预览记录都指向 LTC `WeaponIndex:1535`，没有可用的同组 `WeaponModel.ModelId` 关联。分别使用 `preview:6000000:1002`、`preview:8000000:1002`、`preview:10000000:1002` 时，`PV-GangsiHook_Devil_UP` 的 `Left` 与 `hook` 网格槽 0 均映射到 `ModelTextures/PlayerView/PV-NanoHook_Devil.DTX`，三组聚焦导出都成功解码并导出贴图。当前两份 PV DTX 副本 SHA-256 相同。`PV-GangsiHook_Devil_UP_BL` 的 `left` 槽 0 按 LTC 变体规则映射到同一贴图；`headusOBJexport001` 槽 1 暂无可验证来源，保持未解析。记录和导出报告位于 `artifacts/mapping-audit/item-1002-nanohook-focused-20261025/`。
- 模型组 `2000000` 的手雷道具 `1399` 经同组 `WeaponModel.ModelId:3038` 关联到 LTC `WeaponIndex:3038`。`PV-FLASHBANG_Balance` 的 `smoke-grenade` 与 `pin` 网格槽 2 映射到 `ModelTextures/PlayerView/PV-FLASHBANG.DTX`；BL、GR、WOMAN_BL、WOMAN_GR 变体均按 UV 推导同一槽位贴图。五个模型的两份物理 LTB 副本全部解码；40 条网格记录中 20 条道具网格成功导出贴图，另 20 条为未绑定角色贴图的手/臂网格。LTC 同行还将世界模型 `Models/Weapons/FLASHBANG` 槽 0 映射到 `ModelTextures/Weapons/L-FLASHBANG.DTX`；两份世界 LTB 副本 SHA-256 一致，聚焦导出成功解码并导出世界与 PView DTX。世界模型网格无缺失贴图。报告位于 `artifacts/mapping-audit/item-1399-flashbang-focused-20261025/`。
- 原生 LTB 解码保留每个网格的 RenderStyle 编号，并区分文件声明的材质槽与推测槽。OBJ 仍只导出漫反射贴图，不重建全部 RenderStyle 效果。
- 角色别名有冲突时，可用 `row:rez\RF100\Table\CharacterType.CFT#701:BL` 按零基 CFT 行和阵营精确选择皮肤；表内存在有效 `RecordId` 时可用 `recordid:rez\RF100\Table\CharacterType.CFT#<编号>`，两种编号域互不混用。
- `--scan-cfg`：扫描 CFG，提取贴图引用，输出 TXT/CSV 报告。
- `--decode-cfg`：重试失败 CFG，导出可还原文本或二进制预览图，并分类高熵配置。

## v1.2.5 更新

- 新增随程序发布的模型贴图参考清单，区分已导出确认的槽位和 CFT 已声明但槽位待验证的关系，并记录可确认的动作模型路径。
- 更新当前游戏目录的 CFT/LTC 盘点；FireAim Marker 的身体、手臂和配件路径已入表，贴图槽位等待聚焦导出验证。
- 更新中英文说明书和 GitHub Release 文案，版本号提升到 `v1.2.5`。

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

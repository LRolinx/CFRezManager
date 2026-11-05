# RezView 动画链路参考

2026-11-05 源码核对。本文记录外部实现与本项目样本的差异，不表示当前版本已支持动画预览或动画导出。

## 源码版本

| 仓库 | 核对提交 | 相关文件 |
| --- | --- | --- |
| [DearIcer/RezView](https://github.com/DearIcer/RezView/tree/7e5aa0727efe3621c14c6ebc3ec95adf1b19f2be) | `7e5aa0727efe3621c14c6ebc3ec95adf1b19f2be` | `RuntimeAnimPackageLoader.cs`、`AnimCommandWatcher.cs`、`ViewerBootstrap.cs` |
| [DearIcer/CFRezManager](https://github.com/DearIcer/CFRezManager/tree/97426514db4bb10f27dda63b248f86db91c1affc) | `97426514db4bb10f27dda63b248f86db91c1affc` | `LtbModelParser.cs`、`LithTechModelAnimationPairing.cs`、`LithTechAnimPackageExporter.cs` |

RezView 使用 Unity 2022.3.62f3c1。LTB 解析和动画包写出位于关联的 CFRezManager 分支；Unity 端接收已导出的 OBJ 或 `.cfan` 文件，不负责查找 REZ、CFT 与贴图映射。以上结论来自源码检查，尚未运行该查看器。

## 数据链路

1. [LTB 解析端](https://github.com/DearIcer/CFRezManager/blob/97426514db4bb10f27dda63b248f86db91c1affc/Decoders/LithTech/Models/LtbModelParser.cs)保留节点父子关系、全局绑定矩阵、刚性骨骼编号、骨骼组或矩阵调色板中的顶点影响、位置和旋转通道。支持四种动画压缩编号；16 位位置除以 16，16 位四元数分量除以 32767。这两个缩放值也与本地 LithTech 原始加载器一致。
2. [动画包写出端](https://github.com/DearIcer/CFRezManager/blob/97426514db4bb10f27dda63b248f86db91c1affc/Decoders/LithTech/Models/LithTechAnimPackageExporter.cs)写入骨骼、网格和动画，贴图另存为 PNG。骨骼局部矩阵按父节点全局矩阵的逆乘当前全局矩阵计算；逆全局矩阵作为蒙皮绑定矩阵。
3. [Unity 加载端](https://github.com/DearIcer/RezView/blob/7e5aa0727efe3621c14c6ebc3ec95adf1b19f2be/Assets/Scripts/RuntimeAnimPackageLoader.cs)创建 `SkinnedMeshRenderer` 和 legacy `AnimationClip`。它翻转位置及法线的 Z、交换三角形绕序，旋转转换为 `(-x,-y,z,w)`，绑定矩阵在左右两侧乘 Z 镜像矩阵。UV 的 V 翻转在写出端完成。旋转轨道还处理相邻四元数的符号连续性。
4. [控制端](https://github.com/DearIcer/RezView/blob/7e5aa0727efe3621c14c6ebc3ec95adf1b19f2be/Assets/Scripts/AnimCommandWatcher.cs)通过预览目录中的文本文件接收轨道选择、播放、暂停和跳转指令，另写状态文件。切换轨道前恢复绑定姿势，避免上一轨道的残留变换。

这套分工可以参考：先在 .NET 端保留原始骨骼和蒙皮信息，再为查看器或通用格式写出数据。坐标变换应集中在一个边界执行，动画预览继续使用当前已经验证的贴图解析结果。

## `.cfan` v1 布局

小端序；字符串是 int32 字节数加 UTF-8 数据，布尔值占一字节。文件扩展名为 `.cfan`，魔数实际为 `CFRA`。

| 顺序 | 内容 |
| --- | --- |
| 文件头 | 四字节 `CFRA`、uint32 版本 1 |
| 骨骼 | uint32 节点数；逐节点写名称、int32 父索引、局部位置 float32×3、旋转×4、缩放×3、行优先绑定矩阵×16 |
| 网格 | uint32 网格数；逐网格写名称、贴图相对文件名、uint32 顶点数及索引数、位置数组、可选法线数组、可选 UV 数组、uint32 索引数组 |
| 蒙皮 | 每个网格的索引数组后，逐顶点写四个 byte 骨骼索引及四个 float32 权重 |
| 动画 | uint32 动画数；逐动画写名称、uint32 关键帧数、float32 秒数数组；逐节点写位置与旋转是否存在，再分别写对应的完整帧数组 |

该协议没有嵌入模型挂点、CFT 附件目标类型、材质多纹理槽或 RenderStyle 参数。一个网格只传一个贴图文件名。接入它仍需继续保留本项目的挂点记录和贴图槽位证据。

## 已确认的适配问题

| 项目 | 外部实现 | 本项目样本与采用条件 |
| --- | --- | --- |
| 动画配对 | [按名称复制通道](https://github.com/DearIcer/CFRezManager/blob/97426514db4bb10f27dda63b248f86db91c1affc/Decoders/LithTech/Models/LithTechModelAnimationPairing.cs)，任一同名节点即可通过；自动查找使用同目录文件及变体后缀 | FireAim 身体与动作模型为 29/29 同名且层级相同；手臂为 1/46，仅根节点同名。若直接将这个名称条件用于 CFT 配对，手臂也会通过。此处是条件复核，未声称该分支实际自动配错 FireAim |
| 动画数量 | Unity 加载器每包最多 1,024 段；写出端写入整个动画列表 | 四份 `M-motion_blue.ltb` 分别为 1,499、1,971、2,256、2,775 段。完整写出会超过读取上限，需选择轨道或同步调整协议两端及内存预算 |
| 根节点 | 写出端将带动画且全局绑定矩阵非单位阵的根节点固定为单位变换 | 这是外部分支的处理策略；还需核对原始通道、首帧姿势及蒙皮结果，不能据此删除所有非单位根节点动画 |
| 骨骼索引 | 协议每个索引只有一字节；写出端用 255 标记未使用影响 | 本次 29/46 节点样本在范围内；不能将最多 4,096 节点的读取边界当作所有骨骼都能通过 byte 索引寻址。越界权重不得静默绑定到根节点 |
| 结构完整性 | 生产端解析到动画后返回，不消费挂点和动画绑定尾部；节点序号按遍历顺序重建 | 保留本项目已有的文件末尾检查、显式节点索引、挂点和资源哈希；先检查名称唯一性和父子关系，再验证蒙皮及动画 |
| 材质 | 每网格单贴图，Unity 创建基础纹理材质 | 可作为当前漫反射预览的载体，不能据此判定游戏多层材质已重建 |

[24 组样本对比](evidence/fireaim-marker-20261105-motion-compatibility.tsv)保存了同名节点数、非根节点匹配数、重复名称数、层级比较和动画数上限检查。12 组身体配对的非根节点匹配数为 28；12 组手臂配对为 0。所有配对的动画源均超过该查看器的 1,024 段上限。相同层级仅支持继续检查的候选关系；身体与动作文件的绑定矩阵不同，实际播放和蒙皮仍未验证。

## 后续采用顺序

1. 继续完成能够确认的模型与贴图导出，保留 CFT 身份和原始资源版本。
2. 在现有严格 LTB 读取路径中保留骨骼组、调色板重映射及原始权重，校验每个顶点的有效影响和绑定姿势。
3. 先处理显式选定的一个模型与一段动画，验证首帧、坐标方向、根运动、手脚及附件位置，再扩展批量导出。
4. 动作文件配对要求明确来源、唯一节点名称和可验证的父子关系；层级不同的样本另行确认转换规则。不要因同名根节点而自动绑定。

当前生产代码未合入外部分支的解析器或 Unity 播放器，OBJ 导出仍只包含几何和贴图。本次保存源码入口、协议及实测差异，后续可按固定提交复核。

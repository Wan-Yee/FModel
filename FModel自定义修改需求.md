# FModel 自定义修改需求与移植指南

> 本文档汇总对 FModel 的全部自定义修改需求，用于 **FModel 版本更新后，在新版源码上重新应用这些修改**。


---

## 一、总览

### 1.1 目的

FModel 上游（`4sval/FModel`）没有以下能力，需要我们自己维护：

1. 模型/动画按 **文件夹模式** 组织导出（每个模型单独文件夹 / 按骨骼分组）。
2. 导出模型时，**把引用同一 Skeleton 的动画一起导出**（Save Animations Embedded within Meshes）。
3. 动画导出方式可选：**Through Mesh**（随模型）或 **Through Folder**（随文件夹）。
4. **过滤 AnimMontage**（导出动画时跳过蒙太奇）。
5. **Animated Models Only**：右键文件夹时，只导出“骨骼上存在动画”的模型。

### 1.2 代码来源

| 仓库 | 地址 | 说明 |
| --- | --- | --- |
| FModel 主仓库 | `https://github.com/Wan-Yee/Fmodel-anim.git`（分支 `dev`） | 我们在 `4sval/FModel` 基础上 fork（旧仓库 `ayami22/FModel` 已废弃） |
| CUE4Parse 子模块 | `https://github.com/Wan-Yee/CUE4Parse` | 分组导出逻辑放在这里（若新仓库改用其它 CUE4Parse fork，请同步替换） |

关键提交（本地克隆 `addons\reponew\FModel` 中可直接 `git show` 查看完整 diff）：

| 提交 | 仓库 | 内容 |
| --- | --- | --- |
| `9870e6e2` | FModel | 新增功能主提交：ExportFolderMode / SaveEmbeddedAnimations / AnimationExportMode / FilterAnimMontage / Animated Models Only + 更新子模块指针 |
| `5e51e729` | FModel | 修复导出选项面板 Grid 行缺失 |
| `461d118` | CUE4Parse | 分组输出目录支持 + 动画绑定导出支持 |

> 上游基线（fork 前）为 `fafcda53`。
> 生成的完整补丁可参考：
> - FModel：`git show 9870e6e2 -- FModel` + `git show 5e51e729`
> - CUE4Parse：`git diff 89ef88f6 461d1180`（分别在子模块内执行）

### 1.3 功能清单

| 功能 | 设置项 / 入口 | 主要落地位置 |
| --- | --- | --- |
| 导出文件夹模式 | `ExportFolderMode`（枚举下拉：None / ByModel / BySkeleton） | CUE4Parse + FModel 导出选项面板 |
| 模型内嵌动画 | `SaveEmbeddedAnimations`（开关） | CUE4Parse `SkinnedAssetExporter` |
| 动画导出模式 | `AnimationExportMode`（枚举下拉：Through Mesh / Through Folder） | CUE4Parse + FModel `CUE4ParseViewModel` |
| 过滤 AnimMontage | `FilterAnimMontage`（开关） | CUE4Parse + FModel |
| 仅导出有动画的模型 | 右键文件夹 → “Animated Models Only” | FModel `RightClickMenuCommand` / `CUE4ParseViewModel` |

> 注意：`SaveSkeletonAsMesh`（Skeleton 作为模型导出）和 `SaveEmbeddedMaterials`（材质内嵌）是 **上游自带功能**，不要重复添加。

---

## 二、功能需求详情

### 2.1 导出文件夹模式 ExportFolderMode

**需求**：导出模型时，把模型及其材质 JSON、贴图（以及内嵌动画）按指定方式组织到子文件夹：

- `By Model`：每个模型一个文件夹 → 兼容早期的“Export Model to Separate Folder”。
- `By Skeleton`：按骨骼分组，结构如下（来源：`新需求2.txt`）：

```
输出目录/
├── Have_Skeleton/
│   ├── SK_H1_Skeleton/          # 骨骼1
│   │   ├── SK_Chara_01/         # 模型 + 材质json + 贴图 + glb
│   │   │   ├── xx.json
│   │   │   ├── xx.png
│   │   │   └── xx.glb
│   │   ├── SK_Chara_02/
│   │   └── Animations/          # 引用骨骼1的全部动画
│   └── SK_H2_Skeleton/
│       ├── ...
│       └── Animations/
└── No_Skeleton/                 # 未引用骨骼的模型
    ├── SK_Chara_01/
    └── SK_Cube_01/
```

**CUE4Parse 子模块修改**：

| 文件 | 修改 |
| --- | --- |
| `CUE4Parse-Conversion/Options/EExportFolderMode.cs` | **新增**枚举：`None` / `ByModel` / `BySkeleton`（带 `[Description]` 供 UI 显示） |
| `CUE4Parse-Conversion/Options/ExportOptions.cs` | 构造函数与字段新增 `ExportFolderMode`、`ExportAnimations`、`AnimationExportMode`、`FilterAnimMontage` |
| `CUE4Parse-Conversion/Exporters/ExporterBase.cs` | 新增 `public string? OutputFolderOverride { get; set; }`；`ResolveOutputPath` 中若该值非空，则改为写到 `Session.ResolveOutputPathInFolder(folder, leaf, ext, suffix)` |
| `CUE4Parse-Conversion/ExportSession.cs` | 新增 `internal string ResolveOutputPathInFolder(string folder, string fileName, string ext, string? nameSuffix)`：在 `BaseDirectory` 下 `folder` 子目录中解析并创建路径 |
| `CUE4Parse-Conversion/Exporters/GroupedExportHelper.cs` | **新增**静态辅助类：常量 `Have_Skeleton` / `No_Skeleton` / `Animations`；`SanitizeFolderName`、`GetSkeletonFolderName(USkeleton)`、`GetSkeletonAnimationsFolder(USkeleton)` |
| `CUE4Parse-Conversion/Exporters/MeshExporter.cs` | 新增 `ApplyGroupedOutputFolder()`：根据模式设置 `OutputFolderOverride`（ByModel=模型名；BySkeleton=有骨骼则 `Have_Skeleton/<SkeletonName>/<ModelName>`，否则 `No_Skeleton/<ModelName>`）；排列材质时把 `OutputFolderOverride` 传给 `MaterialExporter` |
| `CUE4Parse-Conversion/Exporters/MaterialExporter.cs` | 导出贴图时改为创建 `TextureExporter` 并透传 `OutputFolderOverride`（原实现是直接 `Session.Add(texture)`），使贴图跟随模型目录 |

关键逻辑（`MeshExporter.ApplyGroupedOutputFolder`）：

```csharp
var mode = Session.Options.ExportFolderMode;
if (mode == EExportFolderMode.None) return;
var modelName = GroupedExportHelper.SanitizeFolderName(ObjectName);
if (mode == EExportFolderMode.ByModel)
    OutputFolderOverride = modelName;
else if (mesh is USkinnedAsset skinned && skinned.Skeleton.TryLoad<USkeleton>(out var skeleton))
    OutputFolderOverride = $"{GroupedExportHelper.HaveSkeletonRoot}/{GroupedExportHelper.GetSkeletonFolderName(skeleton)}/{modelName}";
else
    OutputFolderOverride = $"{GroupedExportHelper.NoSkeletonRoot}/{modelName}";
```

**FModel 主仓库修改**：

| 文件 | 修改 |
| --- | --- |
| `FModel/Settings/UserSettings.cs` | `ExportOptions` 映射中加入新字段；新增 `ExportFolderMode` 属性（默认 `None`） |
| `FModel/ViewModels/ExportOptionsViewModel.cs` | 新增 `ExportFolderModes` 列表 + `SelectedExportFolderMode`，在 Reset/Save/相等性判断中同步 |
| `FModel/Views/Resources/Controls/ExportOptionsControl.xaml` | 新增 “Export Folder Mode” 下拉（以及分组分隔符） |

---

### 2.2 Save Animations Embedded within Meshes（模型内嵌动画）

**需求**（`新需求.txt`）：导出模型和其材质 JSON、贴图时，**把引用同一 Skeleton 的动画一并导出到同一（或分组）文件夹**。
匹配方式：模型有对 Skeleton 的引用，动画也有对 Skeleton 的引用，若二者引用同一 Skeleton，则视为该模型的关联动画。

**CUE4Parse 子模块修改**（`CUE4Parse-Conversion/Exporters/SkinnedAssetExporter.cs`）：

- 在 `BuildFiles(USkinnedAsset originalMesh, ...)` 中，导出材质后调用 `EnqueueMatchingAnimations(originalMesh)`。
- `EnqueueMatchingAnimations(USkinnedAsset mesh)`：
  - 仅当 `options.ExportAnimations && options.AnimationExportMode == ThroughMesh` 时执行；
  - 取 `mesh.Skeleton` 的路径；通过 `mesh.Owner?.Provider` 遍历全部 `.uasset` 包；
  - 识别动画类型：类型名包含 `AnimSequence` / `AnimMontage` / `AnimComposite`；
  - `FilterAnimMontage` 打开时跳过 `AnimMontage`；
  - 用 `IsSkeletonMatch`（比较 Skeleton 路径，忽略大小写）匹配；
  - 用 `HashSet<string>` 去重（按动画路径）；
  - `AnimationExporter { OutputFolderOverride = animationsFolder }` 加入 `Session`（与网格并行导出）。
- 动画目标文件夹：

```csharp
string? animationsFolder = options.ExportFolderMode switch
{
    EExportFolderMode.BySkeleton => GroupedExportHelper.GetSkeletonAnimationsFolder(skeleton),
    EExportFolderMode.ByModel    => OutputFolderOverride,
    _                            => null,   // None：跟随动画自身路径
};
```

**FModel 修改**：`UserSettings.SaveEmbeddedAnimations`（默认 `false`）+ 导出选项面板开关。

---

### 2.3 动画导出模式 AnimationExportMode

**需求**：两种收集动画的策略。

- `Through Mesh`：随每个模型导出时，收集“与该模型 Skeleton 相同”的动画（即 2.2，逻辑在 CUE4Parse 内，天然并行）。
- `Through Folder`：右键文件夹导出时，递归扫描该文件夹树里的**全部动画**，按各自 Skeleton 分组到 `Have_Skeleton/<SkeletonName>/Animations`。

**枚举**（`CUE4Parse-Conversion/Options/EAnimationExportMode.cs`，**新增**）：

```csharp
public enum EAnimationExportMode
{
    [Description("Through Mesh")]   ThroughMesh,
    [Description("Through Folder")] ThroughFolder
}
```

**FModel 修改**（`FModel/ViewModels/CUE4ParseViewModel.cs`）：

- `ExtractFolder(...)` 在原 `BulkFolder(...)` 之后追加判断：当 `bulk` 含 `Meshes` 且 `ExportFolderMode==BySkeleton` 且 `AnimationExportMode==ThroughFolder` 且 `ExportAnimations` 时，调用 `CollectAnimationsThroughFolder`。
- `CollectAnimationsThroughFolder(CancellationToken, TreeItem)`：递归遍历 `folder.AssetsList.Assets` 与 `folder.Folders`，加载 `.uasset` 包、遍历 `ExportMapLength`，识别动画类型、按 `FilterAnimMontage` 过滤、用动画自身的 Skeleton 计算目录，并以 `OutputFolderOverride = GroupedExportHelper.GetSkeletonAnimationsFolder(skeleton)` 加入 `ExportSessionViewModel.Instance.Session`。
- `ExportOptionsViewModel` / `ExportOptionsControl.xaml`：新增 “Animation Export Mode” 下拉，且仅当 `SaveEmbeddedAnimations` 勾选时可用。

---

### 2.4 过滤 AnimMontage（Filter AnimMontage）

**需求**：启用后，导出动画时跳过 `UAnimMontage`（只保留 `AnimSequence` / `AnimComposite`），对 Through Mesh 与 Through Folder 均生效。

**落地位置**：

- CUE4Parse `SkinnedAssetExporter.EnqueueMatchingAnimations`：按类型名 `Contains("AnimMontage")` 跳过。
- FModel `CUE4ParseViewModel.CollectAnimationsThroughFolder`：按类型名跳过。
- FModel `CUE4ParseViewModel.SaveExport`：若 `UserSettings.Default.FilterAnimMontage && export is UAnimMontage`，记录日志并 `return`。

**FModel 修改**：`UserSettings.FilterAnimMontage`（默认 `false`）+ 面板开关（仅当 `SaveEmbeddedAnimations` 勾选时可用）。

---

### 2.5 Animated Models Only（仅导出有动画的模型）

**需求**：右键文件夹选择该命令时，只导出“其 Skeleton 上存在至少一个动画”的模型；若同时开启 `SaveEmbeddedAnimations`，则连带把这些模型的动画导出。

**FModel 修改**：

| 文件 | 修改 |
| --- | --- |
| `FModel/ViewModels/Commands/RightClickMenuCommand.cs` | 动作表新增 `"Save_AnimatedModels" => (EAction.Export, EShowAssetType.None, EBulkType.Meshes)`；`folderAction` 改为按 `trigger` 分派：`"Save_AnimatedModels" => folder => ...ExtractAnimatedModelsFolder(...)` |
| `FModel/ViewModels/CUE4ParseViewModel.cs` | 新增 `ExtractAnimatedModelsFolder(...)`、`GetAnimatedSkeletonPaths(...)`（扫描全部包，收集“拥有动画的 Skeleton 路径集合”）、`CollectAnimatedModels(...)`（递归文件夹，仅对 `USkinnedAsset` 且 Skeleton 在集合内的模型调用 `SaveExport`） |
| `FModel/Views/Resources/Controls/ContextMenus/FolderContextMenu.xaml` | 新增菜单项 `Header="Animated Models Only"`，`CommandParameter` 传 `Save_AnimatedModels`，图标为模型+动画叠加 |

---

## 三、文件修改清单（汇总）

### 3.1 FModel 主仓库

- `FModel/Settings/UserSettings.cs`
  - `ExportOptions` 映射追加：`Default.ExportFolderMode, Default.SaveEmbeddedAnimations, Default.AnimationExportMode, Default.FilterAnimMontage`
  - 新增属性：`ExportFolderMode`（默认 `None`）、`SaveEmbeddedAnimations`（`false`）、`AnimationExportMode`（`ThroughMesh`）、`FilterAnimMontage`（`false`）
- `FModel/ViewModels/CUE4ParseViewModel.cs`
  - `using CUE4Parse_Conversion.Options;`
  - `ExtractFolder` 扩展 + 新增 `CollectAnimationsThroughFolder`
  - 新增 `ExtractAnimatedModelsFolder` / `GetAnimatedSkeletonPaths` / `CollectAnimatedModels`
  - `SaveExport` 增加 `FilterAnimMontage` 过滤
- `FModel/ViewModels/Commands/RightClickMenuCommand.cs`
  - 新增 `Save_AnimatedModels` 动作与 `folderAction` 分派
- `FModel/ViewModels/ExportOptionsViewModel.cs`
  - 新增 `SelectedExportFolderMode` / `SaveEmbeddedAnimations` / `SelectedAnimationExportMode` / `FilterAnimMontage` 及其集合，并接入 Reset / Save / 相等性判断
  - `SocketSettingsEnabled` 变更时同步触发 `RaisePropertyChanged`
- `FModel/Views/Resources/Controls/ExportOptionsControl.xaml`
  - 新增分组分隔符“GROUPING & ANIMATION”，以及 4 个控件（文件夹模式、内嵌动画、动画导出模式、过滤蒙太奇）
  - Socket Format 绑定 `SocketSettingsEnabled`
- `FModel/Views/Resources/Controls/ContextMenus/FolderContextMenu.xaml`
  - 新增 “Animated Models Only” 菜单项

### 3.2 CUE4Parse 子模块

- `CUE4Parse-Conversion/Options/EExportFolderMode.cs`（新增）
- `CUE4Parse-Conversion/Options/EAnimationExportMode.cs`（新增）
- `CUE4Parse-Conversion/Options/ExportOptions.cs`（新增 4 个构造参数/字段）
- `CUE4Parse-Conversion/ExportSession.cs`（新增 `ResolveOutputPathInFolder`）
- `CUE4Parse-Conversion/Exporters/ExporterBase.cs`（新增 `OutputFolderOverride`，改 `ResolveOutputPath`）
- `CUE4Parse-Conversion/Exporters/GroupedExportHelper.cs`（新增）
- `CUE4Parse-Conversion/Exporters/MeshExporter.cs`（`ApplyGroupedOutputFolder` + 透传）
- `CUE4Parse-Conversion/Exporters/MaterialExporter.cs`（贴图用 `TextureExporter` 并透传目录）
- `CUE4Parse-Conversion/Exporters/SkinnedAssetExporter.cs`（`EnqueueMatchingAnimations` + `IsSkeletonMatch`）

---

## 四、移植到新版本 FModel 的步骤

1. **拉取新版上游**，先确认以下上游结构是否变化（版本升级最容易变动的地方）：
   - `ExportOptions` 类的位置与命名（当前：`CUE4Parse-Conversion/Options/ExportOptions.cs`）。
     > 老版本叫 `ExporterOptions`（在 `CUE4Parse_Conversion.Exporters` 下），新版已迁移/改名为 `ExportOptions`。
   - 导出器目录（`CUE4Parse-Conversion/Exporters/`）与基类 `ExporterBase`、`ExportSession`、`ExportFile`。
   - 骨骼资产导出器：老版本为 `SkeletalMeshExporter.cs`，新版为 `SkinnedAssetExporter.cs`。
   - 导出选项面板：老版本在 `SettingsView.xaml` 的 Models 模板，新版在 `Views/Resources/Controls/ExportOptionsControl.xaml` + `ExportOptionsViewModel`。
   - 右键命令表：`ViewModels/Commands/RightClickMenuCommand.cs`；文件夹上下文菜单：`.../ContextMenus/FolderContextMenu.xaml`。
2. **先搬 CUE4Parse 子模块改动**（枚举 → `ExportOptions` 字段 → `ExportSession`/`ExporterBase` → `GroupedExportHelper` → `MeshExporter`/`MaterialExporter` → `SkinnedAssetExporter`），保证编译通过。
3. **再搬 FModel 主仓库改动**（`UserSettings` → `ExportOptionsViewModel` → `ExportOptionsControl.xaml` → `CUE4ParseViewModel` → `RightClickMenuCommand`/`FolderContextMenu.xaml`）。
4. **编译验证**：`FModel/FModel.sln`（或 `.slnx`），确认无编译错误。
5. **功能验证**（见下）。

### 功能验证清单

- ExportFolderMode=ByModel：每个模型独立文件夹，材质 JSON 与贴图在其内。
- ExportFolderMode=BySkeleton：有骨骼模型进 `Have_Skeleton/<骨骼名>/<模型名>`，无骨骼进 `No_Skeleton/<模型名>`，动画进 `Have_Skeleton/<骨骼名>/Animations`。
- SaveEmbeddedAnimations=ON + Through Mesh：导出模型时同骨架动画一并导出且并行。
- AnimationExportMode=Through Folder：右键文件夹导出后动画按骨骼归入 `Animations`。
- FilterAnimMontage=ON：`AnimMontage` 被跳过，仅 `AnimSequence`/`AnimComposite`。
- Animated Models Only：只导出有动画的模型；配合内嵌动画时同时导出其动画。

---

## 五、注意事项 / 易错点

1. **目录名清理**：Skeleton/模型名必须经 `GroupedExportHelper.SanitizeFolderName` 去除非法字符（`/ \ : * ? " < > |` 等），否则创建目录失败。
2. **贴图跟随目录**：只改 `MeshExporter` 不够，材质里的贴图必须经 `TextureExporter` 透传 `OutputFolderOverride`，否则贴图仍写到默认路径。
3. **去重**：同一动画可能被多个包引用，收集时务必用路径 `HashSet` 去重，避免重复导出。
4. **并行与线程**：动画通过加入 `ExportSession` 与网格并行导出；避免在收集阶段同步 `Task.WaitAll` 造成卡顿。
5. **过滤要一致**：`FilterAnimMontage` 需同时在 CUE4Parse 收集路径与 FModel 的 `SaveExport` / Through Folder 路径判断，否则会出现“部分入口未过滤”。
6. **仅 SkeletalMesh**：`UStaticMesh` 无 Skeleton，不参与动画收集；`mesh.Skeleton` / `skinned.Skeleton` 判空后再用。
7. **配置向后兼容**：老用户配置里可能存在旧的 `ExportModelToSeparateFolder`（见第六节），如需平滑升级可保留 `[JsonProperty]` 兼容映射到 `ByModel`。
8. **UI 启用联动**：`AnimationExportMode` 与 `FilterAnimMontage` 仅在 `SaveEmbeddedAnimations` 勾选时可用。

---

## 六、历史方案（第一代实现）与差异

工作副本 `addons\Dev_New\FModel`（基于 `4sval/FModel`，未提交）是第一代实现，功能相同但**代码全写在 FModel 主仓库内**，与新版 fork 的实现位置不同，移植时勿混用：

| 对比项 | 第一代（旧） | 第二代（现用 fork） |
| --- | --- | --- |
| 分组目录 | `MeshExporter`/`Mesh.cs` 内自行拼目录 | CUE4Parse `OutputFolderOverride` + `GroupedExportHelper` |
| 动画收集 | `CUE4ParseViewModel.ExportAnimationsByFolderAsync` / `UModel.ExportAnimationsBySkeletonAsync`（内联 `Task.Run`） | CUE4Parse `SkinnedAssetExporter.EnqueueMatchingAnimations` + FModel `CollectAnimationsThroughFolder` |
| 选项 UI | `SettingsView.xaml` 的 Models 模板 | `ExportOptionsControl.xaml` + `ExportOptionsViewModel` |
| 设置同步 | `SettingsViewModel` 快照机制 | `ExportOptionsViewModel` |
| 旧配置兼容 | 有 `[JsonProperty("ExportModelToSeparateFolder")]` 映射到 `ByModel` | 无（fork 已移除） |

> 历史第一代还存在一个更早的选项名 `ExportModelToSeparateFolder`（每模型一个文件夹），后被 `ExportFolderMode = ByModel` 取代。上游并无该选项。

---

## 附录 A：原始需求原文

### A.1 `addons\Dev_New\新需求.txt`

```
我需要添加一个功能：
导出模型和其对应材质json和贴图和对应动画到同一文件夹，不同模型不同文件夹。（再添加一个Save Animations Embedded With Meshes开关）
目前已经实现导出模型与其对应材质json和贴图到同一文件夹，未实现导出其对应的动画。
导出其对应的动画功能实现思路如下：
    因为动画的结构信息中有对与Skeleton的引用，模型的结构信息中也有对Skeleton的引用。
    所以导出模型时，找出模型引用的Skeleton，然后与动画匹配，如果动画也引用的该Skeleton，那么就将该动画视为该模型的对应动画导出到该模型的文件夹里
提示：
    可以看看Save Materials Embedded With Meshes怎么实现的，这个功能与我们需要实现的Save Animations Embedded With Meshes功能类似
---
【2025-11-24 更新】已添加新功能：Filter AnimMontage（过滤动画蒙太奇）
- 启用后，在导出动画时会自动跳过 AnimMontage 类型的动画
- 只导出 AnimSequence 和 AnimComposite 类型的动画
- 适用于 Through Mesh 和 Through Folder 两种动画导出模式
```

### A.2 `addons\Dev_New\FModel\新需求2.txt`

```
我需要一个新功能，即：按照Skeleton来导出模型和动画：
开启该功能后，Save Model时，会进行如下操作：
检查模型和动画引用的Skeleton
如果动画或模型有引用Skeleton，则创建Have_Skeleton然后创建以该Skeleton命名的文件夹，将动画和模型以及对应材质Json和贴图导入进去，
如果模型没有引用Skeleton就导入到另一个文件夹里：
Have_Skeleton
|    SK_H1_Skeleton        ------骨骼1
|   |   SK_Chara_01        ------模型及对应json和贴图
|   |   | xx.json
|   |   | xx.png
|   |   | xx.glb
|   |   SK_Chara_02
|   |   Animations         ------引用骨骼1的动画导出该文件夹
|    SK_H2_Skeleton        ------骨骼2
|   ...
No_Skeleton            ------没有引用骨骼的模型导出到该文件夹
|   SK_Chara_01
|   ...
并且动画的导出与 模型的导出是异步进行的，如何实现？先制定plan
该功能在何时生效？两种情况都支持
动画的异步导出是指：模型和动画同时导出（并行，动画用Thread.task来导出）
如果同时启用"Export Model to Separate Folder"和"Export Animations and Models By Skeleton Separate Folder"，优先级是：
两者互斥，只能选一个，相当于是一个下拉列表，选则其中一种导出方式
```

## 附录 B：相关文档索引

- `addons\Dev_New\Save Materials Embedded With Meshes实现思路.md`（同构参考实现，材质内嵌）
- `addons\Dev_New\Save Animations Embedded With Meshes实现方案.md`（动画内嵌方案草稿）
- `addons\Dev_New\debug流程.md`（从 UI 到保存的调用链说明）
- `.cursor\plans\1_save-animations-embedded-with-meshes---1f8af5c8.plan.md`（早期按 Skeleton 分组导出计划）

# GraphCraft Agent Guide

## 项目

- 内部名称：`GraphCraft`
- 展示名称：`GraphCraft: Visual Craft Planner`
- 目标：将当前 Terraria/tModLoader crafting system 预先物化为统一的静态 `CraftGraph`，为后续子图分析、状态解析、规划、事务执行和图形化显示提供唯一结构基础。
- Repository：`C:\Users\Fu\Documents\My Games\Terraria\tModLoader\ModSources\GraphCraft`

## 已确认环境

- Windows x64
- tModLoader：`1.4.4.9+2026.06.3.6`，`2026.06 stable`
- Terraria API：`1.4.4.9`
- tModLoader：`E:\Steam\steamapps\common\tModLoader`
- .NET SDK：`8.0.424`
- Target：`net8.0`、C# 12、`TML_2026_06`
- 构建：在 PATH 包含 `C:\Program Files\dotnet` 的环境运行 `dotnet build GraphCraft.csproj`

## Architecture Invariants

1. GraphCraft is Graph-First.
2. Terraria Recipe relationships are materialized once per mod-loading session into a persistent `CraftGraph`.
3. `ItemNode` and `RecipeNode` are separate first-class graph nodes.
4. Runtime planning must operate on `CraftGraph`, not rebuild dependency trees from `Main.recipe`.
5. A ResultItem-to-Recipe dictionary is an optional traversal index, not the graph itself.
6. Static `CraftGraph` and dynamic player/world state must remain separate.
7. Planner and Executor must remain separate.
8. Planning must never mutate real player inventory.
9. Visualization is a first-class architecture requirement; core APIs must expose nodes, typed edges, quantities, constraints and adjacency.
10. Do not read or use Recursive Craft or Magic Storage recursive-crafting source as implementation references.
11. Raw graph structures must allow cycles. Do not hard-code a DAG assumption.
12. Core graph code must not depend on UI, logger formatting or debug display code.
13. Static Graph is not Dynamic State; player evidence belongs only to immutable context/overlay snapshots.
14. Local ingredient satisfaction is not global craft feasibility.
15. `ReadyNow` means one direct recipe execution is locally ready; it does not mean recursively craftable.
16. Recursive craftability belongs to the Milestone 4 planner.
17. Analysis and resolution must never mutate real player inventory.

## 当前结构

- `Core/Graph`：只读图模型、节点、边、约束和统计。
- `Core/Analysis`：对已物化的二部 `CraftGraph` 执行 SCC、cycle 标注、凝聚图和拓扑排序。
- `Core/Subgraphs`：从目标 `ItemNode` 反向提取共享节点、替代配方和环均被保留的依赖子图。
- `Core/Context`：不可变的 inventory、station adjacency 和 Condition evidence 快照；不保存实时 Player 引用。
- `Core/Resolution`：把动态 context 覆盖到 canonical target subgraph nodes/edges，输出局部 resolution。
- `Systems/CraftGraphBuilder`：把 setup 完成的 tModLoader recipes 转换为图。
- `Systems/PlayerCraftContextCapture`：唯一的 Terraria Player adapter；只读普通 `player.inventory`、`adjTile` 和图中 Condition evaluators。
- `Systems/CraftGraphSystem`：在 `PostSetupRecipes` 构建并发布静态图与分析结果，并执行加载期验证。
- `Debug/CraftGraphDiagnostics`：只读取 `CraftGraph`、分析结果和目标子图，输出结构化统计与图文本。
- `Debug/GraphCraftStateCommand`：`/gcstate <itemId>` 手动调试入口，严格走 Capture → Resolve → Format pipeline。

## Clean-Room

允许使用当前项目、本地 tModLoader API/文档、官方 ExampleMod、官方 tModLoader 源码、通用图算法资料和 GBFR 论文。禁止读取、搜索或仿写 Recursive Craft 与 Magic Storage recursive crafting 的实现。

## Milestone 2 约束

- SCC 必须覆盖真实二部图中的全部 `ItemNode` 和 `RecipeNode`，边方向为 `Item -> Recipe -> Result Item`。
- cycle 由 SCC 判定；单节点 SCC 只有存在 self-loop 才算 cyclic。
- 凝聚图必须是 DAG，并为全部 component 生成且验证拓扑序。
- Target dependency extraction 必须返回图子集，不得退化成复制节点的依赖树。
- 同一个逻辑 Item 必须复用原 `CraftGraph` 的 canonical `ItemNode`；替代 producer、共享节点和 cycle 必须保留。
- bounded extraction 必须同时支持 dependency depth 和 total graph-node 上限，并记录明确 truncation metadata。
- `GraphAnalyzer` 与 `TargetSubgraphExtractor` 均只读取 `CraftGraph`，不得重新读取 `Main.recipe`。

## 当前状态

- 已完成：Milestone 0、Milestone 1（Materialized Static CraftGraph）、Milestone 2（GraphAnalyzer + TargetSubgraphExtractor）和 Milestone 3（Dynamic Resolution Overlay）。
- 已验证：不可变 synthetic context、direct/group ingredient resolution、station/condition states、canonical overlay coverage、共享库存边界和真实 tModLoader 加载。
- 真实 Player capture 通过 `/gcstate <itemId>` 提供；自动化环境不能进入玩家世界，因此仍需游戏内手动验证。
- 未开始：recursive planner、VirtualInventory、global allocation、material consumption、executor 或正式 UI。

## Milestone 3 约束

- `PlayerCraftContextSnapshot` 创建后必须与后续 Player 变化隔离，不得保留 Player 或实时 callback。
- MVP inventory scope 仅为普通 `player.inventory`；不包含 Chest、Bank、Safe、Defender's Forge、Void Bag 或外部 storage。
- Recipe Group requirement 必须聚合全部 accepted item types，并保留每种实际匹配物品的数量 evidence。
- `Player.adjTile` 是 station availability 的事实来源；其中已包含 tModLoader `AdjTiles` 等价关系。多个 required tiles 按 Terraria 的 AND 语义逐项满足。
- Recipe Conditions 在 Capture 时使用官方 `Condition.IsMet()` 语义求值；缺少 evaluator、缺少 evidence 或求值异常必须为 `Unknown`。
- `ResolutionSnapshot` 必须完整覆盖 source subgraph 的 canonical ItemNodes、IngredientEdges 和 RecipeNodes，并复用已有 SCC mapping。
- 禁止把 `Owned`、count、station 或 condition 当前状态写入 `ItemNode`/`RecipeNode`。

## Future Visualization Direction

- Visualization 是 GraphCraft 的核心产品能力，不是装饰性功能。
- 未来 UI 应组合 `TargetCraftSubgraph` 与 `ResolutionSnapshot`，取得 nodes、typed edges、quantities、constraints、alternatives、SCC、shared identity、truncation 和动态 evidence。
- 当前可安全显示 `Owned`、`UnownedAtomic`、`UnownedDerived`、direct satisfaction、station/condition blocking；最终 Missing 与 recursive Craftable 必须等待 Planner。

## 下一里程碑

等待用户确认后进入 Milestone 4：Graph-Constrained Craft Planner。不要自行开始 planner、execution 或 UI。

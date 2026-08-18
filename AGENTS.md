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

## 当前结构

- `Core/Graph`：只读图模型、节点、边、约束和统计。
- `Core/Analysis`：对已物化的二部 `CraftGraph` 执行 SCC、cycle 标注、凝聚图和拓扑排序。
- `Core/Subgraphs`：从目标 `ItemNode` 反向提取共享节点、替代配方和环均被保留的依赖子图。
- `Systems/CraftGraphBuilder`：把 setup 完成的 tModLoader recipes 转换为图。
- `Systems/CraftGraphSystem`：在 `PostSetupRecipes` 构建并发布静态图与分析结果，并执行加载期验证。
- `Debug/CraftGraphDiagnostics`：只读取 `CraftGraph`、分析结果和目标子图，输出结构化统计与图文本。

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

- 已完成：Milestone 0、Milestone 1（Materialized Static CraftGraph）和 Milestone 2（GraphAnalyzer + TargetSubgraphExtractor）。
- 已验证：真实 SCC/cycle、凝聚 DAG 拓扑序、Furnace 深度 2 子图、Cell Phone 深度 3 复杂子图、depth/node truncation。
- 未开始：任何 dynamic player state、inventory scanning、planner、executor 或正式 UI。

## Future Visualization Direction

- Visualization 是 GraphCraft 的核心产品能力，不是装饰性功能。
- 未来 UI 应从 `TargetCraftSubgraph` 取得 Item/Recipe nodes、typed edges、quantities、constraints、alternatives、SCC membership、shared identity 与 truncation metadata。
- Milestone 3 才以独立 dynamic overlay 加入 `Owned`、`Craftable`、`MissingAtomic`、`Blocked` 与 `Alternative` 等状态；不得把这些状态写回静态 `CraftGraph`。

## 下一里程碑

等待用户定义；不要自行进入 dynamic state、planner、executor 或 UI。

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
- `Systems/CraftGraphBuilder`：把 setup 完成的 tModLoader recipes 转换为图。
- `Systems/CraftGraphSystem`：在 `PostSetupRecipes` 构建并发布一次静态图。
- `Debug/CraftGraphDiagnostics`：只读取 `CraftGraph`，输出统计、目标检查和两跳文本子图。

## Clean-Room

允许使用当前项目、本地 tModLoader API/文档、官方 ExampleMod、官方 tModLoader 源码、通用图算法资料和 GBFR 论文。禁止读取、搜索或仿写 Recursive Craft 与 Magic Storage recursive crafting 的实现。

## MVP 边界

当前只完成 Milestone 0 和 Milestone 1：Materialized Static CraftGraph。不要实现 UI、Inventory scanning、dynamic overlay、VirtualInventory、recursive crafting、planner、executor、物品消耗、SCC 或完整 TargetSubgraphExtractor。

## 当前状态

- 已完成：环境基线、Git baseline、Graph-First 模型、`PostSetupRecipes` 图构建、真实图统计、Furnace target inspection、深度 2 文本图验证。
- 当前 milestone：Milestone 1 complete。
- 未开始：任何 dynamic player state、planner、executor 或正式 UI。

## 下一里程碑

等待用户确认后进入 Milestone 2：`GraphAnalyzer + TargetSubgraphExtractor`，包括 SCC、cycle analysis、topology 和 target dependency subgraph。

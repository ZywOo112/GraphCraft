# GraphCraft DEVLOG

## 2026-08-18 - Milestone 0 + Milestone 1

### Environment

- tModLoader：`1.4.4.9+2026.06.3.6`（`2026.06 stable`）
- Terraria API：`1.4.4.9`
- .NET SDK：`8.0.424`
- Project：`C:\Users\Fu\Documents\My Games\Terraria\tModLoader\ModSources\GraphCraft`
- Build command：`$env:Path='C:\Program Files\dotnet;'+$env:Path; dotnet build .\GraphCraft.csproj --nologo`
- Baseline build：SUCCESS，0 warnings / 0 errors
- Baseline commit：`56fe8fa Initial tModLoader GraphCraft skeleton`

### Milestone 1

- 使用 `ModSystem.PostSetupRecipes`，在 recipes setup 完成后构建一次静态只读 `CraftGraph`。
- `ItemNode` 与 `RecipeNode` 是不同的一等节点。
- `IngredientEdge` 保存所需数量与 Recipe Group 快照。
- `ProductEdge` 显式表示 `RecipeNode -> result ItemNode`。
- crafting stations 与 Recipe Conditions 仅作为静态 metadata 保存。
- Atomic/Derived 只由 producer adjacency 决定，不混入玩家拥有状态。
- Debug 验证只能读取 `CraftGraph`，不得重新遍历 `Main.recipe`。

### Files Created

- `AGENTS.md`
- `DEVLOG.md`
- `Core/Graph/GraphPrimitives.cs`
- `Core/Graph/ItemNode.cs`
- `Core/Graph/RecipeNode.cs`
- `Core/Graph/IngredientEdge.cs`
- `Core/Graph/ProductEdge.cs`
- `Core/Graph/RecipeMetadata.cs`
- `Core/Graph/CraftGraphStatistics.cs`
- `Core/Graph/CraftGraph.cs`
- `Systems/CraftGraphBuilder.cs`
- `Systems/CraftGraphSystem.cs`
- `Debug/CraftGraphDiagnostics.cs`

### Files Modified

- `GraphCraft.cs`：移除 GUI 模板中未使用的 imports，保留最小 Mod entry point。
- `GraphCraft.csproj`：启用 nullable analysis；保留 tModLoader GUI 生成的 SDK 与 targets 结构。

### Graph Statistics

2026-08-18 17:03，在当前 `enabled.json` Mod 环境中启动隐藏的 tModLoader server load-session，`PostSetupRecipes` 真实输出：

- ItemNodes：`5752`
- RecipeNodes：`5405`
- IngredientEdges：`10424`
- ProductEdges：`5405`
- AtomicItems：`974`
- DerivedItems：`4778`
- ItemsWithMultipleProducers：`427`
- MaximumProducerRecipeCount：`22`
- AverageIngredientsPerRecipe：`1.93`
- RecipesWithConditions：`239`
- RecipesWithRequiredTiles：`4897`
- Sanity check：`974 + 4778 == 5752`，通过。
- Product invariant：`ProductEdges == RecipeNodes == 5405`，通过。

### Debug Validation

- Target：`Furnace (ItemID 33) [Derived]`
- Producer：`RecipeNode #2073`，output `x1`
- Ingredients：`Stone Block x20`、`Wood x4`、`Torch x3`
- Recipe Group：Wood edge 保存 `group #19 'Any Wood'`，共 `11` 个 accepted item types。
- Required tile：`WorkBenches (18)`
- Conditions：none
- 两跳文本图从 `Furnace` 展开到 producer recipes 和 ingredient ItemNodes；重复遇到 `Wood` 时输出 `[already visited]`，没有无限展开。
- 所有统计、target inspection 和文本图均只读取 `CraftGraph`，未重新遍历 `Main.recipe`。
- 测试进程在日志出现 GraphCraft validation 后已停止，没有进入世界或读取/修改玩家 Inventory。

### Known Issues

- Steam 直接启动环境可能检测不到系统 SDK；当前构建通过仅对构建进程临时补充 `C:\Program Files\dotnet` 到 PATH 解决，未修改 tModLoader 文件。
- Recipe Group 已可靠保存 raw group id、iconic item、accepted item types 与显示文本；更高层的候选选择语义留给后续 Milestone。

### Next Milestone

Milestone 2 已在同日后续工作中完成，详见下节。

## 2026-08-18 - Milestone 2

### Scope

- 新增 `GraphAnalyzer`，只从已物化的 `CraftGraph` 读取节点与边。
- 在真实 `ItemNode -> RecipeNode -> result ItemNode` 二部图上使用 iterative Tarjan SCC，时间复杂度为 `O(V + E)`，避免递归深度风险。
- 生成 SCC membership、cycle 标记、凝聚 DAG、去重 component edges 和经完整验证的拓扑序。
- 新增 `TargetSubgraphExtractor`，返回原图 canonical 节点/边组成的图子集；保留 alternatives、shared nodes 与 cycles。
- 支持 full reachable 与 bounded extraction；bounded 模式同时限制 dependency depth 和 total graph nodes，并输出 truncation reason 与 skipped frontier count。
- 未读取 inventory/player state，未实现 planner、executor 或 UI。

### Files Created

- `Core/Analysis/GraphNodeKey.cs`
- `Core/Analysis/StronglyConnectedComponent.cs`
- `Core/Analysis/CondensationGraph.cs`
- `Core/Analysis/GraphAnalysisResult.cs`
- `Core/Analysis/GraphAnalyzer.cs`
- `Core/Subgraphs/TargetCraftSubgraph.cs`
- `Core/Subgraphs/TargetSubgraphExtractor.cs`
- `Debug/CraftGraphAnalysisDiagnostics.cs`

### Files Modified

- `Systems/CraftGraphSystem.cs`：图构建后发布分析结果并运行 SCC、目标子图和边界验证。
- `Debug/CraftGraphDiagnostics.cs`：改为 partial，使 Milestone 1 与 Milestone 2 diagnostics 分文件维护。
- `AGENTS.md`：更新架构、边界和当前 milestone 状态。
- `DEVLOG.md`：记录实现与真实验证数据。

### Real Graph Analysis

2026-08-18，在当前 `enabled.json` Mod 环境中启动隐藏的 tModLoader server load-session，GraphCraft 加载期输出：

- Graph build：`39.435 ms`
- TotalNodes：`11157`
- TotalEdges：`15829`
- StronglyConnectedComponents：`9758`
- CyclicComponents：`297`
- AcyclicComponents：`9461`
- NodesInCycles：`1696`（Item `760` / Recipe `936`）
- LargestComponentSize：`30`
- Condensation：`9758` nodes / `13914` edges
- CondensationIsDag：`True`
- TopologicalOrderCount：`9758`
- SCC：`103.500 ms`
- Total analysis：`185.835 ms`

### Real Cycle Inspection

- 最大 cyclic component：SCC `#830`，`30` 个节点。
- 真实环链：`Laboratory Plating (ItemID 6560) -> Recipe #3627 -> Laboratory Shelf (ItemID 6563) -> Recipe #3621 -> Laboratory Plating (ItemID 6560)`。
- 该输出同时列出 component 中的实际 ItemNodes 与 RecipeNodes，不使用合成示例。

### Target Subgraph Validation

Furnace（depth `2`，max nodes `250`）：

- ItemNodes `20` / RecipeNodes `17` / TotalEdges `37`
- AlternativeProducerRecipes `13` / SharedItemNodes `1`
- IncludedComponents `7` / IncludedCyclicComponents `2`
- `DepthLimit` truncation，SkippedFrontierCount `15`
- Extraction：`1.849 ms`
- 文本图显示 Stone Block 与 Wood 的 cyclic components、多个替代 producer，以及被 Torch 分支再次引用的 Wood `[shared node]`。

Cell Phone（depth `3`，max nodes `180`）：

- ItemNodes `30` / RecipeNodes `15` / TotalEdges `49`
- AlternativeProducerRecipes `5` / SharedItemNodes `5`
- IncludedComponents `41` / IncludedCyclicComponents `1`
- `DepthLimit` truncation，SkippedFrontierCount `5`
- Extraction：`0.260 ms`
- 两个直接 producer（Magic Mirror / Ice Mirror）均被保留；PDA、Depth Meter、Compass、Glass 等共享节点复用 canonical ItemNode。

另以 Cell Phone（depth `3`，max nodes `10`）执行加载期断言，实际得到 ItemNodes `7`、RecipeNodes `3`、TotalEdges `10`、SkippedFrontierCount `6`、Extraction `0.121 ms` 和 `NodeLimit` truncation；若未触发则 mod 加载失败。

### Validation

- `dotnet build GraphCraft.csproj --nologo`：SUCCESS，0 warnings / 0 errors。
- tModLoader server load-session：GraphCraft 完整完成图构建、SCC、凝聚 DAG 验证和目标子图提取；测试进程随后主动停止。
- 所有 Milestone 2 分析均只读取 `CraftGraph`；没有重新遍历 `Main.recipe`。

### Boundaries Preserved

- 没有 inventory scanning 或 player/world dynamic state。
- 没有 planner、executor、物品消耗或事务逻辑。
- 没有 UI。
- 没有读取或借鉴 Recursive Craft / Magic Storage recursive-crafting 源码。

### Known Limitations

- 当前结果只描述静态结构，不回答玩家是否拥有材料、当前环境是否满足 station/condition，亦不选择最佳 recipe path。
- `FullReachable()` 已提供且 cycle-safe，但大型目标可能返回很大的子图；developer diagnostics 与未来 UI 初始视图应优先使用 bounded mode。
- 文本 diagnostics 为便于阅读会把图投影成带 `[shared node]` 标记的展开视图；`TargetCraftSubgraph` 本身仍保存 canonical shared graph identity。
- SCC 与目标子图在 mod load session 内计算一次；尚未实现跨 session 持久化或正式可视化界面。

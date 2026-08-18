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

## 2026-08-18 - Milestone 3

### Dynamic Overlay Architecture

```text
Terraria Player
-> PlayerCraftContextCapture
-> immutable PlayerCraftContextSnapshot

TargetCraftSubgraph + PlayerCraftContextSnapshot + existing GraphAnalysisResult
-> ResolutionOverlayBuilder
-> ResolutionSnapshot
```

- Static `CraftGraph`、SCC 和 target subgraph 没有加入任何玩家当前状态字段。
- Snapshot 深拷贝 capture 时的 inventory counts、available station tile IDs 和 condition evidence；不会保存 Player reference 或实时 predicate。
- Resolution 复用 source subgraph canonical ItemNodes、IngredientEdges、RecipeNodes 与 Milestone 2 SCC mapping。

### Inventory Capture Scope

- 真实 adapter 只读并按 ItemType 汇总普通 `player.inventory` 的全部非空 stack。
- 不修改任何 slot，不消费物品。
- 不包含 Chest、Piggy Bank、Safe、Defender's Forge、Void Bag、Magic Storage 或其他外部 storage。
- `PlayerCraftContextSnapshot` 对输入集合再次复制；创建后来源集合变化不会影响旧 snapshot。

### Station Semantics

- 本机 `tModLoader.xml` 明确说明 `Player.adjTile[tileType]` 用于判断 crafting station requirement，并已包含 `ModTile.AdjTiles` 等价 tile。
- 对本机 `Terraria.Recipe.PlayerMeetsTileRequirements` 的只读程序集元数据检查确认：每个非负 `requiredTile` 都必须对应 `adjTile[tileType] == true`，即多个 required tiles 为 AND。
- Overlay 保存全部 required tile IDs 和明确 missing tile IDs。

### Recipe Condition Semantics

- Static metadata 保存 load-session stable `RecipeConditionKey(RecipeIndex, ConditionIndex)`、description 与官方 `Condition.IsMet()` evaluator 定义。
- Capture 阶段对图中 condition 一次性求值并保存 `Satisfied`、`Unsatisfied` 或 `Unknown` evidence。
- 本机 `RecipeLoader.RecipeAvailable` 元数据显示官方语义为全部 Conditions 调用 `Condition.IsMet()`。
- evaluator 缺失、snapshot evidence 缺失或 evaluator 抛出异常时均为 `Unknown`；绝不猜测 true/false。

### Resolution Semantics

- Item：`Owned`（count > 0）、`UnownedAtomic`、`UnownedDerived`。
- Direct Ingredient：按 canonical ItemType 查询 available count。
- Recipe Group Ingredient：对 accepted item type union 求和，并保存正数量 alternative breakdown。
- Recipe local states：`ReadyNow`、`NeedsIngredients`、`BlockedByStation`、`BlockedByCondition`、`UnknownConstraint`。
- `ReadyNow` 仅表示一次直接 recipe execution 的 ingredients/station/conditions 局部满足；不传播 recursive craftability。
- 每条 IngredientEdge 可独立 locally satisfied，但不会分配或扣除共享库存；Local Satisfaction 不等于 Global Craft Feasibility。

### Synthetic Validation

真实 tModLoader load-session 自动执行并全部通过：

1. Stone Block required `20` / available `30` / local `True`。
2. Stone Block required `20` / available `8` / local `False`。
3. Any Wood required `4`，Wood `0`、Boreal Wood `10`：available `10`、RecipeGroup、local `True`，保留 Boreal Wood `x10` evidence。
4. Furnace ingredients 足够、Work Bench unavailable：`BlockedByStation`，missing tile `WorkBenches (18)`。
5. 真实 condition recipe 的 synthetic false evidence：`BlockedByCondition`。
6. 同一 condition 的 synthetic unknown evidence：`UnknownConstraint`，不会成为 `ReadyNow`。

额外 invariants：Furnace direct requirements 与 Work Bench 全满足时为 `ReadyNow`、station 满足但 Stone Block 不足时为 `NeedsIngredients`、snapshot source mutation isolation、全节点/边/recipe canonical coverage、CraftGraph statistics unchanged、两个 Wood `x10` edges 在 Wood `x10` context 中分别 local satisfied，但 `ResolutionSnapshot` 不存在 `GlobalPlanFeasible` 或 `TargetRecursivelyCraftable` 字段。

### Real Condition Selection

- 自动从当前 materialized graph 选择第一个真实有 Condition 的 recipe：`Recipe #174 -> Bottled Water (ItemID 126)`。
- Condition：`Near water`。
- 对该真实 condition identity 的 Satisfied / Unsatisfied / Unknown 映射均通过 synthetic context 验证。

### Runtime and Performance

- tModLoader server load-session：SUCCESS，六个 synthetic scenarios 全部 PASS。
- Synthetic context copy：`1.298 ms`。
- Furnace bounded overlay：`1.247 ms`。
- Cell Phone bounded overlay：`0.078 ms`。
- 完整 synthetic validation：`30.448 ms`（包含额外 ReadyNow / NeedsIngredients 与全部不变量断言）。
- 自动化 server load-session 没有进入玩家世界，因此没有伪造真实 Player capture 数据。

### Manual In-Game Validation Required

- 进入单人世界后使用 `/gcstate 33` 捕获 Furnace overlay。
- 分别在远离/靠近 Work Bench 时执行，确认 `BlockedByStation` 与 station satisfied 的变化。
- 分别携带 Stone Block、Torch、普通 Wood 或 Boreal Wood，确认 direct count 与 Any Wood alternative evidence。
- 使用 `/gcstate 126` 检查 Bottled Water；分别远离/靠近水体，确认 `Near water` Condition 状态随新 capture 更新，而旧 snapshot 不被修改。
- 命令输出 `CaptureElapsedMs` 和 `OverlayElapsedMs`，用于记录真实玩家环境性能。

### Files Created

- `Core/Context/PlayerCraftContextSnapshot.cs`
- `Core/Resolution/ResolutionModels.cs`
- `Core/Resolution/ResolutionSnapshot.cs`
- `Core/Resolution/ResolutionOverlayBuilder.cs`
- `Systems/PlayerCraftContextCapture.cs`
- `Debug/DynamicResolutionSyntheticTests.cs`
- `Debug/CraftResolutionDiagnostics.cs`
- `Debug/GraphCraftStateCommand.cs`

### Files Modified

- `Core/Graph/RecipeMetadata.cs`：保存 condition index 和 load-session evaluator definition，不保存动态结果。
- `Systems/CraftGraphBuilder.cs`：物化官方 Condition identity/evaluator metadata。
- `Systems/CraftGraphSystem.cs`：执行 Milestone 3 load-time synthetic validation；公开统一 target extraction helper。
- `AGENTS.md`、`DEVLOG.md`：更新架构、不变量、验证与限制。

### Build Result

- `dotnet build GraphCraft.csproj --nologo`：SUCCESS，0 warnings / 0 errors。

### Known Limitations

- 真实 Player/adjTile/Condition capture 需要进入玩家世界，当前自动化 server load-session 无法代替；提供 `/gcstate <itemId>` 手动入口。
- MVP 不包含任何外部 storage、多玩家同步或动态 cache invalidation；每次命令按需重新 Capture + Resolve。
- 当前只做 local recipe execution resolution，不处理共享资源分配、alternative selection、quantity propagation、backtracking 或 Missing Atomic Frontier。
- `UnknownConstraint` 保守优先，任何未知 Condition 都阻止 `ReadyNow`。
- 没有正式 UI；diagnostics 仅证明未来 visualization 所需数据已存在。

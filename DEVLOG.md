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

## 2026-08-18 - Milestone 3.5

### Motivation

Milestone 3 的 Context 已与 Static Graph 分离，但资源数量仍由 `PlayerCraftContextCapture` 直接扫描 `player.inventory` 后形成单一字典。Resolution 不直接读取 Player，却无法表达多个来源、来源 provenance 或未来消费能力差异。Milestone 3.5 将这处绑定替换为 source abstraction，未重做 Graph/SCC/Subgraph/Overlay 语义。

### Resource Source Architecture

```text
Vanilla / future third-party adapters
-> ICraftResourceSource
-> immutable CraftResourceSourceSnapshot(s)
-> CraftResourceAggregateSnapshot
   - AggregateCounts
   - EvidenceByItemType (SourceId + SourceKind + ItemType + Count + Capability)
-> PlayerCraftContextSnapshot
-> ResolutionOverlayBuilder
-> future Planner
```

- `CraftResourceSourceId` 由 adapter 提供稳定 identity。
- `CraftResourceSourceKind` 是开放的 vendor-neutral value type；当前预定义 inventory、storage-network、synthetic，不包含 Mod 名称枚举。
- `CraftResourceConsumptionCapability` 区分 `EvidenceOnly` 与未来需要 adapter transaction 的来源；Snapshot 本身没有 consume 方法。
- `CraftResourceSourceSnapshot` 深拷贝某一来源的 ItemType/count。
- `CraftResourceAggregateSnapshot` 对所有来源求和，同时保留逐 source/item evidence。
- Resolution 仅调用 `GetAvailableCount` 与 `GetResourceEvidence`，不依赖 Vanilla、Magic Storage、ImproveGame 或任何具体来源。

### Vanilla Adapters

- `VanillaPlayerInventoryResourceSource` 是当前唯一 concrete resource source；只有该 adapter 读取 `player.inventory`。
- `VanillaPlayerEnvironmentSource` 把 `player.adjTile` 转换为 `CraftEnvironmentSnapshot` station evidence。
- `PlayerCraftContextCapture` 负责组合 Vanilla source 与可选 additional resource/environment sources，再捕获 Conditions。
- Core Context、Resolution、Planner-facing model 均不引用 Player 或第三方 runtime object。

### Aggregate and Provenance Semantics

- Direct Item availability 为所有 source 中相同 ItemType 的总和，IngredientResolution 同时保存每个 source 的 evidence。
- Recipe Group 对 accepted ItemTypes 的 union 跨全部 source 聚合；evidence 保留实际 alternative ItemType 与 SourceId，不会只剩总数。
- `ItemResolution.CurrentCount` 现在代表 aggregate available count，而不是 Vanilla inventory count。
- Source snapshot 与 aggregate snapshot 均不可变；source 后续变化只能由新 Capture 产生新 Context。

### Station / Environment Boundary

- `ICraftEnvironmentSource` 与 `CraftEnvironmentSnapshot.Merge` 为 station evidence 提供独立组合边界。
- Resolution 继续只查询 `context.IsTileAvailable`；不再知道 tile evidence 是否来自 Vanilla adjacency、未来远程 station 或 storage network environment。
- 当前仅接入 Vanilla `adjTile`，未实现任何第三方 virtual station。

### Synthetic Tests A-G

- Test A：单一 Vanilla-compatible source，Stone Block `30`，结果与 Milestone 3 一致：PASS。
- Test B：Source A `3` + Source B `7`，aggregate `10`：PASS。
- Test C：aggregate 后仍分别保留 A `3` / B `7` provenance：PASS。
- Test D：required `8`，A `3` + B `7` 跨来源满足：PASS。
- Test E：required `8`，A `3` + B `2` aggregate `5`，正确不足：PASS。
- Test F：两个真实 Any Wood accepted ItemTypes 分布在两个 synthetic sources，aggregate `10`，RecipeGroup 与双 source/item evidence：PASS。
- Test G：source capture 后把 live fake source 从 `3` 改为 `99`，旧 Context 保持 `3`，新 Capture 为 `99`：PASS。

### Milestone 3 Regression

Furnace regression contract 保持：

1. 无材料 + 无 Work Bench：`BlockedByStation`。
2. 材料完整 + 无 Work Bench：`BlockedByStation`。
3. 材料完整 + Work Bench available：`ReadyNow`。
4. 材料不完整 + Work Bench available：`NeedsIngredients`。

原六个 direct/group/station/condition scenarios、snapshot immutability、canonical coverage、共享资源局部边界全部继续 PASS。`ReadyNow != RecursivelyCraftable` 未改变。

### Runtime and Performance

- tModLoader server load-session：SUCCESS。
- PassedResourceSourceTests：`7`。
- Resource source validation：`3.757 ms`。
- Synthetic Context snapshot：`7.149 ms`。
- Furnace overlay：`2.309 ms`。
- Cell Phone overlay：`0.099 ms`。
- Total synthetic validation：`33.478 ms`。
- Validation process 在日志确认后已停止。

### Files Created

- `Core/Resources/CraftResourceSnapshots.cs`
- `Core/Context/CraftEnvironmentSnapshot.cs`
- `Systems/Adapters/VanillaPlayerInventoryResourceSource.cs`
- `Systems/Adapters/VanillaPlayerEnvironmentSource.cs`
- `Debug/CraftResourceSourceSyntheticTests.cs`

### Files Modified

- `Core/Context/PlayerCraftContextSnapshot.cs`：持有 aggregate resource/environment snapshots，不再持有 inventory-specific dictionary。
- `Core/Resolution/ResolutionModels.cs`：IngredientResolution 保存 source-aware `CraftResourceEvidence`。
- `Core/Resolution/ResolutionOverlayBuilder.cs`：direct/group availability 改为 aggregate/evidence API。
- `Systems/PlayerCraftContextCapture.cs`：组合 resource/environment sources，不再直接扫描 inventory/adjTile。
- `Debug/CraftResolutionDiagnostics.cs`：输出 source、aggregate、capability 和 provenance。
- `Debug/DynamicResolutionSyntheticTests.cs`：接入 3.5 tests 并加强四组 Furnace regression。
- `AGENTS.md`、`DEVLOG.md`：记录新依赖方向、transaction boundary 与兼容性限制。

### Forward Compatibility

- Magic Storage / ImproveGame 等未来集成应新增独立 adapter/provider，产生 `CraftResourceSourceSnapshot`；无需修改 CraftGraph、GraphAnalyzer、TargetSubgraphExtractor、Resolution 或 Planner 数量逻辑。
- 如果第三方提供远程 station，则新增 `ICraftEnvironmentSource` adapter；Resolution station logic 不变。
- Future Execution 必须按 provenance 的 SourceId 查找 live transaction adapter，并依据 `ConsumptionCapability` 决定是否可分配；snapshot 只提供 evidence，不能用于直接扣料。

### Known Limitations

- 当前没有全局 adapter registry；`PlayerCraftContextCapture` 通过 optional additional source 参数作为 composition seam。未来首次第三方集成可在 Systems/Integrations 层增加 registry，而不改变 Core/Resolution/Planner。
- Consumption capability 只是数据边界；尚无 allocation、transaction provider、commit、rollback 或 race validation。
- 当前唯一 concrete source/environment adapter 是 Vanilla player inventory/adjacent tiles；没有第三方依赖。
- 多来源 aggregate 仍只表示局部 availability，不处理 Planner 的共享资源竞争或路径选择。

### Build Result

- `dotnet build GraphCraft.csproj --nologo`：SUCCESS，0 warnings / 0 errors。

## 2026-08-18 - Milestone 4.1: Planner Foundation & Virtual Resource Ledger

### Scope Implemented

- 新增 `Core/Planning`，定义 single-recipe planning request/result、ingredient outcome、virtual reservation 与 immutable ledger。
- `SingleRecipePlanningRequest` 把 selected canonical recipe、required output quantity、`TargetCraftSubgraph`、其 `ResolutionSnapshot`，以及从同一 aggregate resource snapshot 分支出的 ledger 绑定为一个一致输入。
- `SingleRecipePlanner` 只处理一个明确 recipe 的直接 ingredient AND requirements；不查找 producer，不递归，不选择 alternative recipe。
- 请求数量按 canonical `RecipeNode.OutputStack` 向上取整为 craft repetitions，并按 repetitions 缩放每条 ingredient requirement。
- Exact-item requirements 共享同一个 trial ledger，防止有限资源被重复计算。
- 单 Recipe 尝试只有全成功才发布新的 ledger 与 reservations；任何 requirement 失败都返回原 ledger，已暂存 ingredient 标记为 `StagedThenDiscarded`，对外 reservations 为空。
- Planner 只消费现有 `RecipeResolution` 的 station/condition block；保留同一个 resolution evidence 引用，不访问 live runtime。
- Recipe Group concrete allocation 明确返回 `DeferredRecipeGroupAllocation`，不把 iconic item 当作 exact requirement。

### Virtual Ledger Semantics

- Ledger 建立在 immutable `CraftResourceAggregateSnapshot` 上，并保留该 snapshot identity 与 aggregate/provenance 输入。
- `initial` 来自 snapshot；`reserved` 只存在于 ledger branch；`remaining = initial - reserved`。
- `TryReserveExact` 成功时 copy-on-write 创建新 branch；失败时返回原 ledger 实例，因此不会污染调用者状态。
- 当前只做 aggregate logical reservation，不做 per-source allocation、transaction、consume、commit 或 execution rollback。

### Deliberately Deferred

- recursive dependencies、intermediate products、missing frontier；
- Alternative Recipe OR、branch search、heuristic/optimal selection；
- Recipe Group concrete-item allocation；
- cross-branch shared-resource allocation、backtracking、cycle solving；
- source-level allocation、execution、inventory consumption、multiplayer、UI。

### Synthetic Validation

真实 tModLoader server load-session 自动执行：

1. Ledger initialization：PASS。
2. Successful reservation + input snapshot unchanged：PASS。
3. Repeated reservation uses remaining quantity：PASS。
4. Failed reservation preserves ledger state：PASS。
5. Single recipe AND success：PASS。
6. Single recipe AND failure atomicity：PASS。
7. Shared item cannot be double-counted：PASS。
8. Output `x5`, requested `12` -> repetitions `3`, ingredient `2 x 3 = 6`, produced `15`：PASS。
9. Output `x5`, requested `10` -> repetitions `2`, ingredient `4`, produced `10`：PASS。
10. Source snapshot / aggregate / provenance immutability：PASS。
11. Recipe outside `TargetCraftSubgraph` rejected before recipe metadata/resolution use：PASS。
12. Missing direct ingredient does not recurse even when its producer is in the subgraph：PASS。
13. `BlockedByStation` / `BlockedByCondition` / `UnknownConstraint` evidence propagated externally：PASS。
14. Identical inputs produce deterministic status, repetitions, ingredient order and reservations：PASS。

额外边界：Recipe Group concrete allocation deferred with no reservations：PASS。

### Regression and Performance

- Dynamic Resolution scenarios：`6/6` PASS。
- Craft Resource Source tests：`7/7` PASS。
- Furnace direct/group/station regression：PASS。
- Real condition true/false/unknown mapping：PASS（Recipe #174 -> Bottled Water / Near water）。
- Planner Foundation required tests：`14/14` PASS。
- Resource source validation：`3.510 ms`。
- Total existing dynamic synthetic validation：`38.474 ms`。
- Planner Foundation validation：`5.788 ms`。

### Architecture Audit

- `Core/Planning` does not reference `Terraria.Player`, `Main.LocalPlayer`, `player.inventory`, `adjTile`, `Main.recipe`, Magic Storage, ImproveGame, Calamity or another concrete Mod.
- Planner does not mutate `CraftGraph`, `TargetCraftSubgraph`, `ResolutionSnapshot`, resource-source snapshots, aggregate snapshots or provenance。
- No recursive/search/backtracking/execution API was introduced.
- Immutable branch state is intentionally small copy-on-write reserved-count data. This prioritizes correctness and deterministic replay for 4.1 while remaining directly usable as future branch state.

### Build and Runtime Result

- `dotnet build GraphCraft.csproj --nologo`：SUCCESS，0 warnings / 0 errors。
- tModLoader `1.4.4.9+2026.06.3.6` server load-session：SUCCESS；GraphCraft completed graph build, SCC/subgraph checks, all prior synthetic regressions, and all Milestone 4.1 tests.
- Validation server process was stopped after the complete PASS report; no world was entered and no player inventory was consumed or modified.

## 2026-08-18 - Milestone 4.2: Recursive Single-Path Dependency Planning

### Role and Scope

- 4.2 在调用方已经明确选择 root Recipe 后，递归展开 exact-item dependency。
- 只在 `TargetCraftSubgraph` 内恰好存在一个 canonical producer 时继续；这属于 recursion，不属于 recipe search。
- Root Recipe、child recipes、ingredients 和 products 全部复用已有 graph identity；没有平行 dependency-tree model。
- 新增 structured planning frontier、path-local cycle boundary、configured depth boundary 和 producer-before-consumer logical steps。

### Unified Virtual Ledger

`VirtualResourceLedger` 从 4.1 的 real reservation state 扩展为：

```text
planning remaining
= initial real snapshot quantity
+ planned virtual production
- planner reservations
```

- `InitialCounts` 仍直接来自 immutable `CraftResourceAggregateSnapshot`。
- `PlannedProductionCounts` 只属于 Planning layer，不写入 resource source/aggregate snapshot，也不生成 fake provenance。
- `ReservedCounts` 同时覆盖真实初始资源与虚拟中间产物的逻辑占用。
- Reservation 和 production credit 均为 copy-on-write branch transition；父 Ledger 始终保持不变。
- Root 成功后的 output 也 credit 到 final hypothetical Ledger；它表示完整 logical plan 假设成功后的规划状态，而不是真实 inventory。

### Recursive Flow

对每个 deterministic ordered exact requirement：

1. 从当前 evolved Ledger 读取 remaining。
2. 只计算 `deficit = required - remaining`。
3. deficit 为正时，只统计 TargetCraftSubgraph 内 producer cardinality。
4. 恰好一个 producer 时，以 deficit 为 child requested output 递归规划。
5. Child success 后顺序采用 child result Ledger，并保留实际 output-stack surplus。
6. Parent 再一次性 reserve 完整 requirement。
7. 全部 requirements 成功后 credit 当前 Recipe 的实际 produced quantity，并追加当前 logical step。

Sibling dependencies 顺序共享同一条 Ledger 演化链。任何 child 或后续 sibling 失败时，公开 root result 回到 root input Ledger，steps/reservations/productions 全为空。

### Structured Frontiers

- `NoProducerInTargetSubgraph`：derived item 在当前完整子图内没有 producer；不逃出子图。
- `InsufficientTerminalResource`：现有 canonical graph metadata 证明 item 没有 producer。
- `TargetSubgraphTruncated`：zero-producer boundary 位于截断子图，不能声称最终 atomic。
- `AlternativeProducerDeferred`：子图内 producer 数量大于一；不选择、不尝试。
- `DeferredRecipeGroupAllocation`：Recipe Group concrete allocation 继续留给 4.4。
- `BlockedByStation` / `BlockedByCondition` / `UnknownConstraint`：直接保留 `RecipeResolution` evidence。
- `CycleBoundary`：当前 dependency path 重入同一个 canonical Recipe；只停止 recursion。
- `PlanningDepthBoundary`：超过 request 的稳定/configured maximum dependency depth。
- `RecipeOutsideTargetSubgraph`：selected root 不属于当前 canonical subgraph。

Frontier 保留 item、requirement、required/available/deficit、blocking recipe、depth、dependency path、candidate producers、Resolution evidence 和 subgraph truncation metadata；不保存 UI 文案。

### Resolution Semantics

- `NeedsIngredients` 不是永久阻塞；unique recursive child 可以向 Ledger credit 缺失 intermediate。
- `ReadyNow` 只代表原始 runtime snapshot 的局部状态，不能覆盖前序 sibling 已经改变的 Ledger availability。
- `BlockedByStation`、`BlockedByCondition` 和 `UnknownConstraint` 仍是外部 environment boundary；Planner 不重新读取 station、Condition 或 Player。

### Deliberately Deferred

- Alternative Recipe OR / root recipe selection；
- 尝试 R1 失败后再尝试 R2；
- Recipe Group concrete allocation；
- general branch search、backtracking、scoring、shortest/cheapest/optimal path；
- cycle fixed point / SCC planning；
- source-level allocation、consume、transaction、execution、multiplayer、UI。

### Synthetic Tests

真实 tModLoader server load-session 自动执行：

1. One-level recursive success：PASS。
2. Multi-level recursive success：PASS。
3. Existing partial intermediate only produces deficit：PASS。
4. Intermediate surplus reused by sibling：PASS。
5. Shared base resource cannot double-count recursively：PASS。
6. Recursive root failure atomicity：PASS。
7. Zero producer frontier：PASS。
8. Multiple producers deferred：PASS。
9. One ReadyNow producer among alternatives still deferred：PASS。
10. Recipe Group still deferred：PASS。
11. `NeedsIngredients` recursively resolvable：PASS。
12. `ReadyNow` does not override evolved Ledger：PASS。
13. `BlockedByStation` evidence propagation：PASS。
14. `BlockedByCondition` evidence propagation：PASS。
15. `UnknownConstraint` evidence propagation：PASS。
16. Simple cycle boundary：PASS。
17. Cycle detection is path-local：PASS。
18. Configured planning depth boundary：PASS。
19. Multi-output intermediate (`B x15`, reserve `12`, surplus `3`)：PASS。
20. Source/aggregate/provenance/Resolution immutability：PASS。
21. Graph-external convenient producer ignored：PASS。
22. Recursive result determinism：PASS。

额外 terminal-resource 与 truncated-subgraph boundary：PASS。

### Regression and Performance

- CraftGraph / SCC / TargetSubgraph load-time validations：PASS。
- Dynamic Resolution scenarios：`6/6` PASS。
- Resource Source tests：`7/7` PASS。
- Milestone 4.1 Planner Foundation：`14/14` PASS。
- Milestone 4.2 Recursive Planner：`22/22` PASS。
- Resource source validation：`3.894 ms`。
- Existing dynamic synthetic validation：`38.249 ms`。
- Planner Foundation validation：`7.979 ms`。
- Recursive Planner validation：`10.899 ms`。

### Architecture Audit

- Core recursive Planner references no `Player`, inventory, `adjTile`, `Main.recipe`, concrete storage or third-party Mod.
- Producer discovery filters canonical `ItemNode.ProducerRecipes` through the request's exact `TargetCraftSubgraph.RecipeNodes` identity set.
- Virtual production mutates neither runtime resource snapshots nor provenance.
- Multiple producers and Recipe Groups return explicit deferred frontiers; no selection policy exists.
- Cycle/depth behavior only prevents unsafe recursion; no cycle solving or search exists.
- Root failure exposes no child Ledger state, planned steps, reservations or productions.

### Forward Compatibility

- 4.3 can create independent immutable child branches from the same parent Ledger for each candidate Recipe; branch selection policy remains absent today.
- 4.4 can insert concrete Recipe Group allocation between requirement deficit evaluation and exact reservation.
- Sequential Ledger evolution already prevents double-counting on one selected path; multi-alternative competition still needs branch result comparison/selection policy.
- 4.6 can discard failed immutable branches without restoring a mutable singleton.
- Canonical identity remains reference identity inside one materialized load-session graph, matching current Subgraph/Resolution dictionaries. Future cross-session persistence or graph reconstruction will need a stable graph-snapshot identity plus `RecipeIndex`/node key rather than accepting foreign objects by index alone.
- Main future risks are alternative-branch result semantics, allocation across overlapping Recipe Groups, larger-plan copy-on-write cost, canonical identity across reconstructed snapshots, and full cycle/bootstrap policy.

## 2026-08-19 - Milestone 4.3: Alternative Branching & User Choice Decision Model

### Role and Scope

- The existing recursive Planner now evaluates every canonical producer candidate for a missing exact-item requirement; no parallel Planner was introduced.
- Root Recipe selection remains the caller's responsibility.
- Planner reports supported feasibility and preserves uncertainty. It does not invent player preference.
- Candidate branches begin from the identical immutable decision-input Ledger and cannot leak reservations, virtual production, surplus, steps, or nested decisions into siblings.

### Decision and Candidate Semantics

- Candidate outcomes are structured as `FullyViable`, `RequiresUserChoice`, `Failed`, `Blocked`, `Deferred`, or `NotApplicable`.
- Exactly one fully viable candidate is auto-selected only when all other candidates are proven failed or blocked.
- Two or more fully viable candidates return `UserChoiceRequired`; canonical order is deterministic presentation/evaluation order, not preference.
- A fully viable candidate beside a `RequiresUserChoice` or `Deferred` candidate remains unresolved. Deferred capability is never presented as impossible.
- Candidate previews retain logical steps, quantities, reservations, production, frontiers, and nested decision evidence without becoming committed plan steps.

### Explicit Choice Boundary

- `RecursiveRecipePlanningRequest` accepts request-scoped `ExplicitRecipeChoice` constraints keyed by deterministic `AlternativeDecisionId`.
- Candidate identity remains canonical `RecipeNode` reference identity inside the current graph/load session.
- The Planner validates that an explicit Recipe belongs to the encountered candidate set, then deterministically re-plans only that selected branch.
- A selected candidate that later fails returns `ExplicitChoiceFailed` without silently falling back.
- No paused coroutine, persistent Planner VM, preference store, or async resume engine was added.

### Nested Decisions and Deliberate Boundaries

- Nested `UserChoiceRequired` propagates as `RequiresUserChoice` on its containing candidate with structured parent/requirement/path provenance.
- Recipe Group concrete allocation remains `DeferredRecipeGroupAllocation`.
- No first-success selection, scoring, optimization, global lookahead, general backtracking, source allocation, execution, transaction, runtime mutation, or UI was added.
- Planner Core continues to read only graph/subgraph, Resolution evidence, and immutable planning state; it does not read Player, inventory, `adjTile`, `Main.recipe`, or concrete storage APIs.

### Validation

- Added 24 Milestone 4.3 synthetic scenarios for alternative cardinality, branch isolation, explicit choice, no-fallback behavior, canonical ordering, recursive candidates, evolved Ledger truth, local-vs-global feasibility, nested choices, deferral, frontiers, regression, immutability, and determinism.
- Updated the two Milestone 4.2 multiple-producer regression expectations to the new 4.3 decision semantics; the remaining 4.2 behavior stays unchanged.
- Full `dotnet build GraphCraft.csproj --nologo`: SUCCESS, 0 warnings / 0 errors.
- Real tModLoader `1.4.4.9+2026.06.3.6` server load-session: SUCCESS; final run reported `Mod Load Completed in 20785ms`.
- Milestone 4.1: `14/14` PASS; Milestone 4.2: `22/22` PASS; Milestone 4.3: `24/24` PASS.
- Milestone 4.3 alternative-decision validation elapsed: `8.839 ms`; all earlier graph, analysis, subgraph, resolution, and resource-source validations also passed.
- The validation process was stopped after the complete report; no world was entered and no real inventory or storage resource was consumed or modified.

### Forward Review

- A future UI can consume `UserChoiceRequired`, `AlternativeDecision`, and ordered `AlternativeCandidateResult` directly without accessing Planner internals.
- User selection can be submitted through a fresh immutable request and deterministic re-plan; no suspended mutable Planner is required.
- A future Recipe Group phase can introduce a sibling resource-choice contract without changing Recipe decision identity or candidate preview semantics.
- General backtracking can later treat automatic and explicit choices differently: automatic decisions may become search branches, while explicit choices remain hard constraints unless the caller asks to revise them.
- Largest current risks are decision identity across reconstructed/load sessions, combinatorial cost once general search is introduced, and clearly separating deferred capability from proven failure as more decision kinds are added.

### Historical Milestone 4.2 Build and Runtime Result

- `dotnet build GraphCraft.csproj --nologo`：SUCCESS，0 warnings / 0 errors。
- tModLoader `1.4.4.9+2026.06.3.6` server load-session：SUCCESS；all previous validations plus 22/22 recursive tests passed。
- Validation server process was stopped after the complete report；no world was entered and no real resource was consumed。

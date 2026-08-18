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

等待用户确认后进入 Milestone 2：`GraphAnalyzer + TargetSubgraphExtractor`（SCC、cycle analysis、topology、target dependency subgraph）。

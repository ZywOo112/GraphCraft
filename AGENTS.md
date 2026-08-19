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
18. Available crafting resources are the aggregate of immutable resource-source snapshots, not a synonym for `player.inventory`.
19. Aggregate counts must retain per-source and per-item provenance.
20. Read visibility does not imply automatic consumption capability; future execution must use a separate adapter transaction boundary.
21. Planner Core reads only `TargetCraftSubgraph`, `ResolutionSnapshot`, and immutable resource-ledger state; it must not read live Terraria state.
22. Planning is graph-constrained: a selected recipe must be the canonical recipe instance included in the request's target subgraph.
23. Resource-source and aggregate snapshots are immutable planning inputs; virtual reservations never consume or mutate them.
24. All direct ingredient requirements in one recipe attempt share one quantity-aware ledger, so the same finite resource cannot be counted twice.
25. A failed single-recipe attempt commits no virtual reservations and returns the original ledger branch.
26. Milestone 4.1 never recursively resolves a missing direct ingredient and never labels it an atomic missing frontier.
27. Requested output quantity and canonical recipe output stack determine craft repetitions and scaled ingredient quantities.
28. Recipe Group concrete-item allocation is explicitly deferred beyond Milestone 4.1; an iconic item is not an allocation.
29. `ReadyNow` remains a direct local-resolution state and is not equivalent to recursively craftable.
30. Planner results are logical plans only; source allocation, execution, transaction, consumption and rollback remain outside Planner.
31. Recursive planning remains constrained to canonical recipes already present in the request's `TargetCraftSubgraph`.
32. Milestone 4.2 automatically expands only exact-item dependencies with exactly one included producer recipe.
33. Zero-producer dependencies become structured terminal/subgraph frontiers; multiple producers are deferred to Alternative Recipe planning.
34. Recipe Group concrete allocation remains deferred.
35. Virtual intermediate production exists only in `VirtualResourceLedger`; it must not be injected into runtime resource snapshots or provenance.
36. Current planning availability is `initial real quantity + planned virtual production - reserved quantity`.
37. Evolved Ledger state is the planning resource truth; Resolution ingredient readiness never overrides it.
38. External station and condition evidence remains owned by `ResolutionSnapshot` and is never re-evaluated by Planner.
39. A failed recursive root attempt publishes the original Ledger and no committed steps, reservations or productions.
40. Cycle and depth detection are recursion safety boundaries, not cycle solving or search.
41. Planned craft steps are dependency-ordered logical records, not execution commands.
42. Recursive planning does not allocate concrete storage sources or mutate provenance.
43. Planner determines supported feasibility; it does not invent or infer player preference.
44. Every alternative Recipe candidate is evaluated from the same immutable decision-input Ledger branch.
45. Candidate evaluation order and result order are canonical and deterministic, but ordering never implies preference.
46. Exactly one fully viable candidate may be auto-selected only when every other candidate is proven failed or blocked.
47. Two or more fully viable candidates produce structured `UserChoiceRequired`; no first-success, scoring, or optimization policy may select one.
48. `RequiresUserChoice` and `Deferred` are not failure and must prevent a claim that another candidate is the only possible route.
49. Deferred Planner capability is not evidence that a candidate is impossible.
50. Candidate preview steps are domain evidence only; unresolved decisions publish no committed plan steps.
51. Explicit Recipe choices are request-scoped constraints, validated against the canonical candidate set, and applied by deterministic re-planning.
52. Failure of an explicitly selected candidate must be returned structurally and must never silently fall back to another candidate.
53. Nested alternative decisions propagate as structured candidate state and provenance.
54. Milestone 4.3 adds no Recipe Group allocator, general backtracking, preference memory, execution, transaction, or UI.

## 当前结构

- `Core/Graph`：只读图模型、节点、边、约束和统计。
- `Core/Analysis`：对已物化的二部 `CraftGraph` 执行 SCC、cycle 标注、凝聚图和拓扑排序。
- `Core/Subgraphs`：从目标 `ItemNode` 反向提取共享节点、替代配方和环均被保留的依赖子图。
- `Core/Context`：不可变的 aggregate resources、environment/station 与 Condition evidence 快照；不保存实时 Player 引用。
- `Core/Resources`：resource source identity/kind/capability、immutable per-source snapshots、aggregate counts 和 provenance evidence。
- `Core/Resolution`：把动态 context 覆盖到 canonical target subgraph nodes/edges，输出局部 resolution。
- `Core/Planning`：Planner contracts、支持 virtual production 的不可变 resource ledger、单 Recipe AND planning，以及仅沿唯一 producer path 展开的 recursive planning；不包含 OR search、Recipe Group allocation 或 execution。
- `Systems/CraftGraphBuilder`：把 setup 完成的 tModLoader recipes 转换为图。
- `Systems/PlayerCraftContextCapture`：组合 resource/environment adapters 并捕获图中 Condition evidence；不自行解释具体 storage。
- `Systems/Adapters`：Vanilla inventory 与 environment 的 concrete source adapters；未来第三方集成也应留在 adapter/integration 层。
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

- 已完成：Milestone 0、Milestone 1、Milestone 2、Milestone 3、Milestone 3.5、Milestone 4.1，以及 Milestone 4.2（Recursive Single-Path Dependency Planning）。
- 已验证：单/多来源 aggregation、per-source provenance、跨来源 ingredient、multi-source Recipe Group、source isolation、Milestone 3 全回归和真实 tModLoader 加载。
- 真实 Player capture 通过 `/gcstate <itemId>` 提供；自动化环境不能进入玩家世界，因此仍需游戏内手动验证。
- 未开始：Alternative Recipe OR、Recipe Group concrete allocation、global alternative-branch competition、backtracking、cycle solving、material consumption、executor 或正式 UI。

## Milestone 3 约束

- `PlayerCraftContextSnapshot` 创建后必须与后续 Player 变化隔离，不得保留 Player 或实时 callback。
- 当前 concrete resource adapter scope 仅为普通 `player.inventory`；不包含 Chest、Bank、Safe、Defender's Forge、Void Bag 或外部 storage。
- Recipe Group requirement 必须聚合全部 accepted item types，并保留每种实际匹配物品的数量 evidence。
- `Player.adjTile` 是 station availability 的事实来源；其中已包含 tModLoader `AdjTiles` 等价关系。多个 required tiles 按 Terraria 的 AND 语义逐项满足。
- Recipe Conditions 在 Capture 时使用官方 `Condition.IsMet()` 语义求值；缺少 evaluator、缺少 evidence 或求值异常必须为 `Unknown`。
- `ResolutionSnapshot` 必须完整覆盖 source subgraph 的 canonical ItemNodes、IngredientEdges 和 RecipeNodes，并复用已有 SCC mapping。
- 禁止把 `Owned`、count、station 或 condition 当前状态写入 `ItemNode`/`RecipeNode`。

## Milestone 3.5 约束

- `ICraftResourceSource` 只负责 Discover/Snapshot/Read；source snapshot 不得泄漏 Player、storage network 或第三方 runtime object。
- `CraftResourceAggregateSnapshot` 是 Resolution/Planner 的数量入口；核心不得扫描具体 inventory/storage。
- `CraftResourceEvidence` 必须保留 SourceId、vendor-neutral SourceKind、ItemType、count 与 consumption capability。
- `SourceKind` 只表示稳定类别，不允许演变为第三方 Mod 名称枚举。
- 当前 concrete resource adapter 仅为 Vanilla Player Inventory；不得增加第三方 Mod dependency。
- station availability 通过可合并 `ICraftEnvironmentSource`/`CraftEnvironmentSnapshot` 提供；Resolution 不得依赖 `player.adjTile`。
- 当前不实现 consume、allocation、commit、rollback 或 race validation。未来 execution 必须按 SourceId 找到独立 transaction adapter，而不是修改 snapshot。

## Milestone 4.1 约束

- `SingleRecipePlanner` 只判断一个明确选中的 canonical `RecipeNode` 的直接 AND requirements。
- `SingleRecipePlanningRequest` 绑定同一个 target subgraph、对应 resolution snapshot、selected recipe、required output quantity，以及从同一 resource snapshot 分支出的 ledger。
- `VirtualResourceLedger` 是 aggregate item counts 之上的 immutable copy-on-write 状态；成功预留返回新分支，失败返回原分支。
- 单 Recipe 尝试先在 trial branch 上暂存预留，只有所有 exact-item requirements 都成功才发布结果。
- Station / Condition 决策只消费已有 `RecipeResolution`；Planner 不重新访问 runtime API。
- Recipe Group requirement 返回 `DeferredRecipeGroupAllocation`，不预留 iconic item，也不选择 concrete accepted items。
- 缺少直接材料返回 `InsufficientDirectResources`；4.1 不发现 producer、不递归、不搜索替代、不分类 missing frontier、不解 cycle。
- Source-level allocation 属于后续层；Ledger 保留 source aggregate snapshot identity，但当前只预留逻辑 aggregate quantity。

## Milestone 4.2 约束

- Root Recipe 仍由调用方明确选择；4.2 不选择 root recipe，也不尝试 alternative producer branches。
- Exact ingredient 不足时先计算当前 Ledger deficit；只有子图内恰好一个 canonical producer 时才递归。
- Child success 的 result Ledger 必须顺序传给下一 sibling 和 parent；不得让不同依赖重复从初始 Ledger 读取资源。
- Recipe output 按真实 `OutputStack` credit 为 Planner-owned virtual production；surplus 保留并可被后续 sibling 复用。
- Zero producer 按现有 graph evidence 区分 terminal resource、subgraph truncation 和 no-producer-in-subgraph frontier；不扫描图外证明 atomic。
- M4.2 的历史基线曾将 multiple producers 返回为 `AlternativeProducerDeferred`；当前行为由下面的 Milestone 4.3 规则取代。
- `NeedsIngredients` 可以由 recursion 解决；`ReadyNow` 不能覆盖 evolved Ledger；station/condition/unknown 继续由 Resolution 阻塞。
- Cycle membership 只属于当前 dependency path；configured maximum depth 提供额外 stack-safety boundary。
- Recursive root failure 返回原 Ledger，且公开 steps、reservations、productions 为空；这不是 general backtracking engine。
- Planned steps 只记录 producer-before-consumer 的逻辑数量顺序，不含 runtime object、source allocation 或 execution mutation。

## Milestone 4.3 Constraints

- Root Recipe selection remains caller-owned. Alternative evaluation applies only to missing exact-item dependencies inside the selected root plan.
- All included canonical producers are evaluated in ascending `RecipeIndex` order from one shared immutable decision-input Ledger.
- Candidate states remain distinct: `FullyViable`, `RequiresUserChoice`, `Failed`, `Blocked`, `Deferred`, and `NotApplicable`.
- Multiple fully viable candidates return `UserChoiceRequired`; exactly one is auto-selected only when all others are proven failed or blocked.
- Explicit choices use deterministic decision identity plus canonical `RecipeNode` identity and re-plan without a mutable suspended Planner session.
- Recipe Group allocation remains deferred. No global lookahead, branch scoring, optimization, general backtracking, source allocation, execution, or UI is present.
- The old Milestone 4.2 multiple-producer deferral rule is superseded by this section; its remaining unique-producer, Ledger, frontier, cycle, and depth invariants still apply.
- Current completed scope is Milestone 0 through Milestone 4.3. The next recommended scope is Resource Alternatives, Recipe Group Allocation, and General Backtracking, but it has not started.

## Future Visualization Direction

- Visualization 是 GraphCraft 的核心产品能力，不是装饰性功能。
- 未来 UI 应组合 `TargetCraftSubgraph` 与 `ResolutionSnapshot`，取得 nodes、typed edges、quantities、constraints、alternatives、SCC、shared identity、truncation 和动态 evidence。
- 当前可安全显示 `Owned`、`UnownedAtomic`、`UnownedDerived`、direct satisfaction、station/condition blocking；最终 Missing 与 recursive Craftable 必须等待 Planner。

## 下一里程碑

Milestone 4.3 已建立 isolated alternative evaluation、structured user-choice boundary、nested decision provenance 与 explicit-choice deterministic re-plan。下一步仅建议 Resource Alternatives + Recipe Group Allocation + General Backtracking；不要自行开始 execution 或 UI。

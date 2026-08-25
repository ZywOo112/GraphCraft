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
46. Exactly one fully viable candidate is auto-selected regardless of whether other candidates are failed, blocked, deferred, or require nested user choice.
47. Two or more fully viable candidates produce structured `UserChoiceRequired`; no first-success, scoring, or optimization policy may select one.
48. `RequiresUserChoice` and `Deferred` are not failure and remain in decision provenance, but they do not block an already confirmed unique fully viable route. With zero fully viable routes, nested user choice propagates before deferred/failure semantics.
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
- Two or more fully viable candidates return `UserChoiceRequired`; exactly one fully viable candidate is always auto-selected while every other candidate status remains in provenance.
- `UserSelectableCandidates` contains only confirmed `FullyViable` candidates. It is empty when `UserChoiceRequired` merely propagates a nested choice and no outer candidate is yet fully viable.
- Explicit choices use deterministic decision identity plus canonical `RecipeNode` identity and re-plan without a mutable suspended Planner session.
- Recipe Group allocation remains deferred. No global lookahead, branch scoring, optimization, general backtracking, source allocation, execution, or UI is present.
- The old Milestone 4.2 multiple-producer deferral rule is superseded by this section; its remaining unique-producer, Ledger, frontier, cycle, and depth invariants still apply.
- Historical note: this Milestone 4.3 boundary was superseded by completed M4.4 and the authoritative Milestone 4 Final constraints below.

## Future Visualization Direction

## Milestone 4 Final Constraints (Authoritative)

- `TargetCraftPlanner` is the normal high-level planning entry point. Callers provide a target `ItemNode`, an explicit `RequestedAdditionalOutputQuantity`, the matching `TargetCraftSubgraph` and `ResolutionSnapshot`, an immutable starting `VirtualResourceLedger`, optional explicit recipe choices, and bounded planning limits.
- Requested target quantity always means additional output. Existing target items in the resource snapshot do not reduce the requested production quantity; a future desired-final-count UI must calculate its own additional deficit before calling Planner.
- Root producers are discovered only from canonical recipes already included in the supplied target subgraph. Planner never scans `Main.recipe` or escapes the graph boundary.
- Root and dependency recipe alternatives use the same complete-continuation rule: zero complete routes preserves the most accurate failure/deferred/boundary evidence; exactly one complete route is automatic; two or more complete routes return `UserChoiceRequired`, exposing only confirmed complete candidates.
- Root choices have a dedicated `RootRecipeDecisionId`; they are not represented as fake ingredient decisions. Explicit root and dependency recipe choices are hard constraints and may never silently fall back to another recipe.
- `GraphConstrainedCraftPlanner` remains the single recursive search core. The selected-root API and `RecursiveSinglePathPlanner` remain compatibility surfaces, not parallel search implementations.
- Planner-owned recipe decisions and concrete Recipe Group allocations may backtrack. Abandoned immutable branches publish no reservations, virtual productions, steps, allocations, or Ledger changes.
- Recipe Group allocation reserves concrete accepted `ItemType` quantities and may split, reuse virtual surplus, and recursively produce accepted members. Source-level allocation remains an M5 execution responsibility.
- Cycle detection is path-local. Existing resources may terminate recursion; a cyclic branch may be abandoned for an acyclic alternative. No fixed-point, bootstrap, inverse-crafting, or dismantling solver is present.
- Target-subgraph truncation and planner search-budget exhaustion mean incomplete knowledge, never proven impossibility. A complete route may still succeed despite unrelated truncation.
- Extraction depth/node limits and planning branch limits are distinct boundaries. Recipe Group expansion is canonical, deterministic, bounded, and reported through subgraph diagnostics.
- `MaximumBranchExpansions` defaults to 4096 and covers recipe branches, resource-allocation branches, and nested backtracking. Search statistics report both branch kinds, decision counts, backtracks, successes/failures, maximum budget, and exhaustion.
- Planning is deterministic and bounded, but deliberately does not score, optimize, or claim shortest/cheapest output.
- Runtime resource, environment, Resolution, and provenance snapshots are immutable. Planner does not access `Player`, inventory, `adjTile`, live storage, `Main.recipe`, or third-party APIs; it does not consume, transact, craft, spawn, or render UI.
- A successful logical plan provides ordered craft steps, scaled repetitions/output, logical reservations and productions, final Ledger, recipe decisions, concrete group allocations, structured frontiers, previews/provenance, and search statistics for M5 to revalidate and execute transactionally.
- `/gcstate <itemId> [additionalQuantity]` is a development-only in-world validation seam that captures real context before invoking the graph-constrained target Planner. It is not a user-facing planner UI.
- Actual load-session integration tests use canonical post-setup Terraria recipes and prove vanilla ownership through `Recipe.Mod is null`; fixed RecipeIndex values are not used to select cases.
- Milestone 4 is complete. Deferred work is limited to M5 transactional execution, later UI, third-party resource adapters, and future preference/optimization policy.

## Milestone 4.4 Constraints

- `GraphConstrainedCraftPlanner` is the single recursive planning core; `RecursiveSinglePathPlanner` remains only as a compatibility entry point.
- Recipe alternatives and Recipe Group resource allocations are separate domain decisions even though both use immutable search branches.
- Candidate recipe viability is defined by complete root continuation. A locally successful candidate that prevents a later requirement is not `FullyViable`.
- Planner-owned recipe and resource branches may backtrack. An `ExplicitRecipeChoice` is a hard request constraint and must never be replaced or bypassed.
- Exactly one complete root-successful recipe candidate is auto-selected; two or more produce `UserChoiceRequired`, limited to those root-successful candidates.
- Only the deterministic first actionable recipe decision is exposed per request. A fresh request carrying an explicit choice may expose the next decision.
- Recipe Group requirements reserve concrete accepted `ItemType` quantities. The iconic item is never a synthetic reservation.
- Concrete group allocation may be split, may recursively produce an accepted member inside the target subgraph, preserves virtual surplus, and shares the same Ledger with exact requirements.
- Routine resource allocation is planner-owned and never becomes `UserChoiceRequired`; its structured decision/candidate evidence remains available for diagnostics and preview.
- Group search uses bounded deterministic priority rotations and demand-driven production, not exhaustive integer-partition enumeration.
- `MaximumBranchExpansions` is a deterministic safety boundary. Exhaustion returns structured `SearchBudgetBoundary` unless an already completed unique route is sufficient under result aggregation semantics.
- The Planner still reads only `TargetCraftSubgraph`, `ResolutionSnapshot`, and immutable Ledger state. It does not scan `Main.recipe`, allocate concrete storage sources, execute, mutate runtime resources, score, or optimize.
- Group member traversal is materialized by `TargetSubgraphExtractor` from `RecipeGroupInfo.AcceptedItemTypes`; producer discovery remains canonical and subgraph-constrained.
- Milestone 4.4 implements no UI, source-level allocation, transaction/executor, preference memory, scoring, shortest-path policy, or general cycle solving.

- Visualization 是 GraphCraft 的核心产品能力，不是装饰性功能。
- 未来 UI 应组合 `TargetCraftSubgraph` 与 `ResolutionSnapshot`，取得 nodes、typed edges、quantities、constraints、alternatives、SCC、shared identity、truncation 和动态 evidence。
- 当前可安全显示 `Owned`、`UnownedAtomic`、`UnownedDerived`、direct satisfaction、station/condition blocking；最终 Missing 与 recursive Craftable 必须等待 Planner。

## 下一里程碑

Milestone 5.1 已完成 execution planning、SourceId allocation、transaction-local intermediate replay、fresh resource/environment/condition preflight 与真实 Vanilla inventory dry-run command。下一步是 M5.2 transaction lifecycle；在明确 prepare/reserve/commit/rollback、intermediate ownership、Terraria craft hooks 和 multiplayer authority 前，不得开始真实 mutation。

## Milestone 5.1 Constraints (Authoritative)

- `Core/Execution` 是独立于 Planner 的执行域。Planner contracts、搜索和 immutable Ledger 不读取 live source，也不承担 SourceId allocation。
- 只有成功且已解决的 `TargetCraftPlanResult` 可以进入 `CraftExecutionPlanner.CreatePreflight`；失败、deferred 或 user-choice plan 不可执行。
- Execution step plan 按 dependency-ordered logical steps 建立，逐项区分 external source resources 与先前步骤产生的 transaction-local intermediates；local intermediate 永远不能分配到 SourceId。
- Recipe Group 必须原样使用 Planner 已选择的 concrete ItemType allocation；Executor 不重新选择 iconic item、accepted member 或 recipe route。
- Source allocation 按 step、ingredient、ItemType、ordinal `SourceId` 顺序确定性执行，可跨多个 source 拆分，绝不超过原始 per-source evidence count，并保留 step/ingredient/source provenance。
- `EvidenceOnly` 对 planning 可见但永远不能进入 executable allocation。`AdapterTransactionRequired` 可进入 M5.1 dry-run allocation；真正 adapter lifecycle 由 M5.2 定义。
- Allocation failure 是原子的：不得发布 partial allocation 或 source expectations，且任何 dry-run path 都不得修改、reserve 或 consume live sources。
- Fresh revalidation 接受同一 `TargetCraftSubgraph` 的全新 `ResolutionSnapshot`：比较已分配 SourceId 的 SourceKind/capability/ItemType quantity，并复验每个既定 recipe 的 station 与 Condition 状态；不得重新规划或切换路线。
- M5.1 使用 fresh capture + quantity comparison 的 optimistic revalidation；尚无 source revision token、lock、reservation、cross-source atomic commit 或 race-free guarantee。
- Vanilla inventory 仍只通过 `VanillaPlayerInventoryResourceSource` 参与 capture；`Core/Execution` 不得读取 `Player` 或 `player.inventory`。
- M5.1 不 remove inventory item、不调用 Terraria craft、不 spawn output、不 commit、不 rollback，也不进入 world；preflight/result 的 mutation 标记恒为 false。

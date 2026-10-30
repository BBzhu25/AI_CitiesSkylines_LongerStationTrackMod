# Longer Station Track Mod — 技术说明 / Technical Notes

---

## 1. 问题 / The Problem

### 中文

原版《城市：天际线》中，长度超过约 215 米的火车站台无法正常建立列车线路，游戏报"无法找到道路"。经逆向分析（IL 反编译 + 全程序集常量扫描），该限制并非单一常量，而是由**寻路系统的段级几何过滤**造成：

- `PathManager.FindPathPosition` 在查询停点位置时，会构建一个以点击位置为中心的查询框（半距 `maxDistance*2`，再各轴外扩 64 米）；
- 对每个候选网格单元内的轨道段，若该段的**两个端点节点都落在查询框之外**，则整段直接跳过（IL 层面是一个 `blt` / `bge.un` 跳转对）；
- 超长站台的停点位于轨道段中部时，两端节点必然都在框外 → 整段被跳过 → 车道锚定失败 → 报"无法找到道路"。

### English

In vanilla Cities: Skylines, train platforms longer than ~215 m cannot be used to create lines — the game reports "Cannot find path". Reverse engineering (IL decompilation plus a full-assembly constant scan) shows the limit is not a single constant but a **segment-level geometric filter in the pathfinding system**:

- When anchoring a stop point, `PathManager.FindPathPosition` builds a query box around the clicked position (half-extent `maxDistance*2`, expanded by 64 m per axis);
- For each track segment in a candidate grid cell, if **both end nodes of the segment fall outside the query box**, the entire segment is skipped (an IL-level `blt` / `bge.un` branch pair);
- On extra-long platforms the stop point sits in the middle of the segment, so both end nodes are always outside the box → the segment is skipped → lane anchoring fails → "Cannot find path".

---

## 2. 修复一：禁用长轨道段跳过过滤 / Fix 1: Disabling the long-segment skip filter

### 中文

通过 Harmony transpiler 定位 IL 中的段跳过模式并改写。原版 IL 结构：

```il
ldloc.s  A        ; 段起点节点与查询框的比较结果
ldc.r4   0
blt      OK       ; 任一端在框内 → 不跳过，继续检查
ldloc.s  B        ; 段终点节点与查询框的比较结果
ldc.r4   0
bge.un   SKIP     ; 两端都在框外 → 跳过整段（← 要禁用的分支）
OK:
```

修复方式是把 `bge.un SKIP` 替换为两条 `pop`：弹出比较用的两个操作数后直接落到 `OK` 标签，使"整段跳过"分支**不成立**。这是栈平衡改写——`bge.un` 弹出 2 个值但不产生新值，两条 `pop` 恰好等价地弹出同样的 2 个值。

安全性：该过滤之后本就存在 `m_bounds.Intersects` 相交测试与精确距离校验兜底，禁用跳过只是让更多候选段进入这些正规校验，不会引入错误锚点。

### English

A Harmony transpiler locates the segment-skip pattern in the IL and rewrites it. Vanilla IL structure:

```il
ldloc.s  A        ; start-node vs. query-box comparison result
ldc.r4   0
blt      OK       ; either node inside the box → do not skip
ldloc.s  B        ; end-node vs. query-box comparison result
ldc.r4   0
bge.un   SKIP     ; both nodes outside → skip the whole segment (← branch to disable)
OK:
```

The fix replaces `bge.un SKIP` with two `pop` instructions: the two comparison operands are popped and control falls through to `OK`, so the "skip whole segment" branch can **never be taken**. This is a stack-balanced rewrite — `bge.un` pops 2 values and pushes nothing, and two `pop`s remove exactly those 2 values.

Safety: after this filter, the vanilla code still runs `m_bounds.Intersects` and an exact distance check, so disabling the skip only lets more candidate segments reach those proper validations — no incorrect anchors can be introduced.

---

## 3. 修复二：放宽停点锚定的车道类型 / Fix 2: Relaxing stop-anchoring lane types

**目标方法：`TransportLineAI.StartPathFind`**

### 中文

`StartPathFind` 对线路起点与终点各调用一次 `FindPathPosition` 锚定停点，原版硬编码 `laneTypes = 2`（仅 Pedestrian，行人车道）。工坊长站台资产的行人车道可能缺失、偏移或未铺到停点位置，导致只搜行人车道时锚定失败。

Transpiler 识别该调用前的立即数加载模式并将 `2` 改为 `3`（Pedestrian | Vehicle）：

il
ldc.i4.2        ; laneTypes = Pedestrian        →  ldc.i4.3  (Pedestrian | Vehicle)
ldc.i4.0        ; vehicleType = None
ldc.i4.0
conv.i8         ; vehicleCategory = None


行为影响：正常站台不受影响——存在行人车道时仍优先锚定行人车道；列车实际停靠的车道最终仍由 `GetStopLane` 依据车辆类型校验把关。

### English

`StartPathFind` anchors the line's start and end stops via one `FindPathPosition` call each, hard-coded to `laneTypes = 2` (Pedestrian only). Workshop long-platform assets may have missing, misplaced, or incomplete pedestrian lanes, so searching pedestrian lanes only can fail to anchor.

The transpiler recognizes the immediate-operand pattern before those calls and changes `2` to `3` (Pedestrian | Vehicle):

```il
ldc.i4.2        ; laneTypes = Pedestrian        →  ldc.i4.3  (Pedestrian | Vehicle)
ldc.i4.0        ; vehicleType = None
ldc.i4.0
conv.i8         ; vehicleCategory = None
```

Behavioral impact: normal platforms are unaffected — pedestrian lanes are still preferred when present, and the lane a train actually stops at is still validated by `GetStopLane` based on vehicle type. The relaxation only provides track lanes as a fallback when the pedestrian lane is unusable.

---

## 4. 修复三：自动修复工坊资产的停靠标记 / Fix 3: Auto-repairing stop flags on Workshop assets

**目标方法：`TransportTool.GetStopPosition`（Postfix）**

### 中文

部分工坊长站台资产的轨道 `NetInfo` 车道缺少 `m_stopType = Train` 标记——没有该标记，即使锚定成功，后续校验也会认定该车道不能作为列车停靠点。

本 mod 用 Postfix 在玩家**成功**放置停点且轨道段长度超过 180 米时，将该轨道 NetInfo 的所有行人/列车车道补上 `Train` 停靠标记。每个 NetInfo 只修复一次（去重集合），并输出到日志。

### English

Some Workshop long-platform assets lack the `m_stopType = Train` flag on their track `NetInfo` lanes. Without it, later validation refuses to treat the lane as a valid train stop even after successful anchoring.

A Postfix on `GetStopPosition` fires when the player **successfully** places a stop on a segment longer than 180 m, walks all pedestrian/train lanes of that track's NetInfo, and adds the `Train` stop flag. Each NetInfo is repaired only once (deduplicated set) with a one-time log line.

---

## 5. 工程约束与兼容性 / Engineering constraints & compatibility

### 中文

- **逐类打补丁**：不使用 `PatchAll`（单个补丁类失败会中断全部），改为逐类 `CreateClassProcessor(t).Patch()`，单类失败不影响其它修复。
- **旧 Mono 限制**：CS1 使用 Unity 5.6.7f1 的旧 Mono（约 .NET 3.5 语义）。含 `yield return` / `async` 的方法会被编译器附加 `IteratorStateMachineAttribute` 等新版本特性，导致 `PatchAll`/程序集扫描抛 `TypeLoadException`。因此本 mod 的 transpiler 全部返回显式构建的 `List<CodeInstruction>`，全程序集禁用 `yield` 与 `async`。
- **栈平衡改写**：所有 IL 分支改写严格保持求值栈平衡（见修复一），避免产生无效 IL 导致原生崩溃。
- **卸载**：`OnDisabled` 调用 `UnpatchAll` 完整撤销补丁，可随时在内容管理器开关。

### English

- **Class-by-class patching**: `PatchAll` is not used (one failing patch class aborts everything). Instead each class is processed via `CreateClassProcessor(t).Patch()`, so a single failure never takes down the other fixes.
- **Old Mono constraints**: CS1 runs the old Mono from Unity 5.6.7f1 (~.NET 3.5 semantics). Methods containing `yield return` / `async` get compiler-generated attributes such as `IteratorStateMachineAttribute`, which make assembly scanning throw `TypeLoadException`. All transpilers in this mod therefore return explicitly built `List<CodeInstruction>` objects, and `yield`/`async` are banned across the codebase.
- **Stack-balanced rewrites**: every IL branch rewrite strictly preserves evaluation-stack balance (see Fix 1), preventing invalid IL and native crashes.
- **Clean unloading**: `OnDisabled` calls `UnpatchAll` to fully remove the patches; the mod can be toggled at any time in the Content Manager.

---

## 6. 三个修复的关系 / How the three fixes work together

### 中文

```
点击停点位置
    │
    ▼
[修复一] FindPathPosition：长轨道段不再被整段跳过 → 段进入候选
    │
    ▼
[修复二] StartPathFind：锚定查询同时接受行人/车辆车道 → 锚定成功
    │
    ▼
[修复三] GetStopPosition：资产缺 Train 停靠标记时自动补齐 → 校验通过
    │
    ▼
线路建立成功
```

### English

```
Player places a stop
    │
    ▼
[Fix 1] FindPathPosition: long segments are no longer skipped → segment becomes a candidate
    │
    ▼
[Fix 2] StartPathFind: anchoring accepts pedestrian AND vehicle lanes → anchor found
    │
    ▼
[Fix 3] GetStopPosition: missing Train stop flags repaired automatically → validation passes
    │
    ▼
Line created successfully
```


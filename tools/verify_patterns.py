#!/usr/bin/env python3
"""
Conway's Game of Life —— 预制构型的独立验证器（oracle）。

这份脚本刻意不引用 C# 代码，用最直白的方式重新实现一遍规则，
目的是给 PatternLibrary.cs 里的坐标、周期、位移提供一份"来自实现之外"的证据。
两边不一致时，一定是其中一边错了，而不是"看起来差不多"。

运行：
    python tools/verify_patterns.py

输出中每一行的 period / displacement / population 就是 C# 侧
PatternBehaviourTests.cs 里断言的期望值。

注意：本脚本使用无限平面（不设边界），因此描述的是构型的内在行为；
在有限网格里只要构型没有碰到边界或环绕回到自己，行为与之相同。
"""

import sys
from collections import Counter
from typing import Iterable, Set, Tuple

# Windows 控制台默认可能使用 GBK 代码页，中文输出会变成乱码。
# 统一按 UTF-8 输出，保证脚本输出与代码/文档的编码一致。
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

Cell = Tuple[int, int]
Grid = Set[Cell]

# 坐标约定与 C# 侧保持一致：x 向右，y 向上，构型以自身包围盒左下角为原点。
PATTERNS = {
    # ---- 稳定静物 ----
    "Block": [(0, 0), (1, 0), (0, 1), (1, 1)],
    "Beehive": [(1, 0), (2, 0), (0, 1), (3, 1), (1, 2), (2, 2)],
    # ---- 振荡器 ----
    "Blinker": [(0, 0), (1, 0), (2, 0)],
    "Toad": [(1, 0), (2, 0), (3, 0), (0, 1), (1, 1), (2, 1)],
    # ---- 飞船 / 循环震荡 ----
    "Glider": [(1, 2), (2, 1), (0, 0), (1, 0), (2, 0)],
    "LWSS": [
        (1, 3), (4, 3),
        (0, 2),
        (0, 1), (4, 1),
        (0, 0), (1, 0), (2, 0), (3, 0),
    ],
}

MAX_PERIOD = 8


def step(cells: Grid) -> Grid:
    """一代演化：B3/S23。"""
    counts: Counter = Counter()
    for x, y in cells:
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                if dx or dy:
                    counts[(x + dx, y + dy)] += 1

    return {c for c, n in counts.items() if n == 3 or (n == 2 and c in cells)}


def normalize(cells: Grid) -> Grid:
    """平移到包围盒左下角，用于忽略位移后的形状比较。"""
    min_x = min(x for x, _ in cells)
    min_y = min(y for _, y in cells)
    return {(x - min_x, y - min_y) for x, y in cells}


def corner(cells: Grid) -> Cell:
    return min(x for x, _ in cells), min(y for _, y in cells)


def analyze(name: str, cells: Iterable[Cell]) -> dict:
    initial: Grid = set(cells)
    shape0 = normalize(initial)
    origin0 = corner(initial)

    history = [initial]
    current = initial
    for _ in range(MAX_PERIOD * 2):
        current = step(current)
        history.append(current)

    period = None
    for candidate in range(1, MAX_PERIOD + 1):
        if len(history[candidate]) != len(initial):
            continue
        if normalize(history[candidate]) != shape0:
            continue
        # 要求第二个周期同样成立，排除"恰好撞上"的假阳性
        if candidate * 2 < len(history) and normalize(history[candidate * 2]) != shape0:
            continue
        period = candidate
        break

    if period is None:
        raise AssertionError(f"{name}: 在 {MAX_PERIOD} 代内未找到周期")

    origin_period = corner(history[period])
    displacement = (origin_period[0] - origin0[0], origin_period[1] - origin0[1])

    return {
        "name": name,
        "cells": len(initial),
        "period": period,
        "displacement": displacement,
        "population_series": [len(history[i]) for i in range(period + 1)],
        "changes_at_gen1": history[1] != initial,
    }


def classify(result: dict) -> str:
    if result["period"] == 1:
        return "StillLife"
    return "Spaceship" if result["displacement"] != (0, 0) else "Oscillator"


def main() -> int:
    print(f"{'name':<9}{'class':<12}{'cells':>6}{'period':>8}  {'displacement':<14}{'population series'}")
    print("-" * 78)

    results = []
    for name, cells in PATTERNS.items():
        result = analyze(name, cells)
        results.append(result)
        series = "/".join(str(n) for n in result["population_series"])
        print(
            f"{name:<9}{classify(result):<12}{result['cells']:>6}{result['period']:>8}  "
            f"{str(result['displacement']):<14}{series}"
        )

    print("-" * 78)
    still = sum(1 for r in results if classify(r) == "StillLife")
    osc = sum(1 for r in results if classify(r) == "Oscillator")
    ship = sum(1 for r in results if classify(r) == "Spaceship")
    print(f"分类统计：静物 {still} / 振荡器 {osc} / 飞船 {ship}（题目要求 2/2/2）")

    ok = (still, osc, ship) == (2, 2, 2) and len(results) == 6
    print("结论：" + ("符合题目要求" if ok else "不符合题目要求，请检查构型数据"))
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())

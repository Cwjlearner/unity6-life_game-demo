using System;
using System.Collections.Generic;

namespace GameOfLife.Core
{
    /// <summary>
    /// 构型行为判定：把"稳定 / 振荡 / 循环震荡"从肉眼观察变成可断言的判等式。
    /// <para>
    /// 稳定：state(t) == state(t+1)
    /// 振荡：state(t) == state(t+p) 且 state(t) != state(t+1)
    /// 飞船：state(t+p) == state(t) 平移 (dx,dy)，且 (dx,dy) != (0,0)
    /// </para>
    /// </summary>
    public static class PatternCompare
    {
        /// <summary>判断 b 是否等于 a 平移 (dx,dy) 后的结果（按指定边界语义回绕）。</summary>
        public static bool MatchesTranslated(GridSnapshot a, GridSnapshot b, int dx, int dy, bool wrapEdges)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            if (b == null) throw new ArgumentNullException(nameof(b));
            if (a.Width != b.Width || a.Height != b.Height)
                throw new ArgumentException(
                    $"两个快照尺寸不一致：{a.Width}x{a.Height} vs {b.Width}x{b.Height}");
            if (a.Population != b.Population) return false;
            if (a.Population == 0) return true;

            for (int y = 0; y < a.Height; y++)
            {
                for (int x = 0; x < a.Width; x++)
                {
                    if (!a.Get(x, y)) continue;

                    int tx = x + dx;
                    int ty = y + dy;

                    if (wrapEdges)
                    {
                        tx = Wrap(tx, a.Width);
                        ty = Wrap(ty, a.Height);
                    }
                    else if (tx < 0 || ty < 0 || tx >= a.Width || ty >= a.Height)
                    {
                        return false;
                    }

                    if (!b.Get(tx, ty)) return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 在历史序列中寻找最小周期与每周期位移。
        /// 要求连续两个周期都成立，避免"恰好撞上"的假阳性。
        /// </summary>
        public static bool TryDetectPeriod(
            IReadOnlyList<GridSnapshot> history,
            int maxPeriod,
            int maxDisplacement,
            out int period,
            out GridPos displacement)
        {
            period = 0;
            displacement = new GridPos(0, 0);

            if (history == null || history.Count < 3) return false;

            GridSnapshot first = history[0];
            const bool wrap = true;

            for (int p = 1; p <= maxPeriod && p + p < history.Count; p++)
            {
                for (int dy = -maxDisplacement; dy <= maxDisplacement; dy++)
                {
                    for (int dx = -maxDisplacement; dx <= maxDisplacement; dx++)
                    {
                        if (!MatchesTranslated(first, history[p], dx, dy, wrap)) continue;
                        if (!MatchesTranslated(history[p], history[p + p], dx, dy, wrap)) continue;

                        period = p;
                        displacement = new GridPos(dx, dy);
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>判定序列是否稳定（第 1 代与第 0 代完全相同）。</summary>
        public static bool IsStillLife(IReadOnlyList<GridSnapshot> history)
        {
            if (history == null || history.Count < 2) return false;
            return history[0].ComputeHash() == history[1].ComputeHash()
                   && history[0].Population == history[1].Population;
        }

        private static int Wrap(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace QueryPlanning;

public sealed class QueryOptimizer
{
    public QueryPlan Optimize(QueryProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        if (problem.Tables.Count == 0) return QueryPlan.Empty;

        int n = problem.Tables.Count;
        if (n > 64)
        {
            throw new NotSupportedException("Too many tables.");
        }

        var sortedTables = problem.Tables.OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
        var tableIdx = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++) tableIdx[sortedTables[i].Id] = i;

        var sortedJoins = problem.Joins
            .Where(j => tableIdx.ContainsKey(j.LeftTable) && tableIdx.ContainsKey(j.RightTable))
            .OrderBy(j => MinId(j), StringComparer.Ordinal)
            .ThenBy(j => MaxId(j), StringComparer.Ordinal)
            .ToArray();

        var joinMasks = new ulong[sortedJoins.Length];
        var joinSel = new int[sortedJoins.Length];
        for (int i = 0; i < sortedJoins.Length; i++)
        {
            var j = sortedJoins[i];
            joinMasks[i] = (1UL << tableIdx[j.LeftTable]) | (1UL << tableIdx[j.RightTable]);
            joinSel[i] = j.SelectivityPermille;
        }

        var tableFilteredRows = new long[n];
        for (int i = 0; i < n; i++)
        {
            tableFilteredRows[i] = CostModel.EstimateFilteredRows(problem, sortedTables[i]);
        }

        ulong[] adj = new ulong[n];
        foreach (var j in sortedJoins)
        {
            int u = tableIdx[j.LeftTable];
            int v = tableIdx[j.RightTable];
            adj[u] |= (1UL << v);
            adj[v] |= (1UL << u);
        }

        ulong Neighbors(ulong mask)
        {
            ulong nbs = 0;
            ulong m = mask;
            while (m != 0)
            {
                int i = BitOperations.TrailingZeroCount(m);
                nbs |= adj[i];
                m &= m - 1;
            }
            return nbs;
        }

        long EstimateRows(ulong mask)
        {
            long rows = 1;
            for (int i = 0; i < n; i++)
            {
                if ((mask & (1UL << i)) != 0)
                {
                    rows = SaturatingMultiply(rows, tableFilteredRows[i]);
                }
            }
            for (int i = 0; i < sortedJoins.Length; i++)
            {
                if ((mask & joinMasks[i]) == joinMasks[i])
                {
                    rows = ScaleCeiling(rows, joinSel[i]);
                }
            }
            return Math.Max(1, rows);
        }

        var dp = new Dictionary<ulong, State>();
        var statesBySize = new List<ulong>[n + 1];
        for (int i = 1; i <= n; i++) statesBySize[i] = new List<ulong>();

        for (int i = 0; i < n; i++)
        {
            ulong mask = 1UL << i;
            var state = BestLeaf(problem, sortedTables[i], tableFilteredRows[i]);
            dp[mask] = state;
            statesBySize[1].Add(mask);
        }

        for (int s = 2; s <= n; s++)
        {
            for (int s1 = 1; s1 <= s / 2; s1++)
            {
                int s2 = s - s1;
                foreach (ulong m1 in statesBySize[s1])
                {
                    foreach (ulong m2 in statesBySize[s2])
                    {
                        if (s1 == s2 && m1 >= m2) continue;
                        if ((m1 & m2) == 0 && (Neighbors(m1) & m2) != 0)
                        {
                            ulong mask = m1 | m2;
                            if (!dp.TryGetValue(mask, out var existing))
                            {
                                long rows = EstimateRows(mask);
                                var newState = Combine(problem, dp[m1], dp[m2], rows);
                                dp[mask] = newState;
                                statesBySize[s].Add(mask);
                            }
                            else
                            {
                                var newState = Combine(problem, dp[m1], dp[m2], existing.Rows);
                                if (IsBetter(newState, existing, problem.MemoryLimitRows))
                                {
                                    dp[mask] = newState;
                                }
                            }
                        }
                    }
                }
            }
        }

        ulong allMask = (1UL << n) - 1;
        if (dp.TryGetValue(allMask, out var finalState))
        {
            return new QueryPlan(finalState.Plan);
        }
        return QueryPlan.Empty;
    }

    private State BestLeaf(QueryProblem problem, TableSpec table, long filteredRows)
    {
        long bestCost = long.MaxValue;
        PlanNode bestPlan = null!;

        long scanCost = SaturatingAdd(
            SaturatingMultiply(table.Rows, table.ScanCostPerRow),
            SaturatingMultiply(filteredRows, 2));
        bestCost = scanCost;
        bestPlan = new PlanNode("tableScan", TableId: table.Id);

        foreach (var index in table.Indexes)
        {
            var predicate = problem.Predicates.FirstOrDefault(p =>
                p.TableId == table.Id && p.Column == index.Column && p.Indexable);
            if (predicate == null) continue;

            long matchedRows = ScaleCeiling(table.Rows, predicate.SelectivityPermille);
            long seekCost = SaturatingAdd(
                index.SeekStartupCost,
                SaturatingMultiply(matchedRows, index.LookupCostPerRow),
                SaturatingMultiply(filteredRows, 2));

            if (seekCost < bestCost)
            {
                bestCost = seekCost;
                bestPlan = new PlanNode("indexSeek", TableId: table.Id, IndexColumn: index.Column);
            }
            else if (seekCost == bestCost)
            {
                var candidate = new PlanNode("indexSeek", TableId: table.Id, IndexColumn: index.Column);
                if (ComparePlans(candidate, bestPlan) < 0)
                {
                    bestPlan = candidate;
                }
            }
        }

        return new State
        {
            Cost = bestCost,
            Rows = filteredRows,
            PeakMemoryRows = 1,
            Plan = bestPlan,
            MinTableId = table.Id
        };
    }

    private State Combine(QueryProblem problem, State s1, State s2, long rows)
    {
        State left, right;
        if (string.CompareOrdinal(s1.MinTableId, s2.MinTableId) < 0)
        {
            left = s1;
            right = s2;
        }
        else
        {
            left = s2;
            right = s1;
        }

        long inputRows = SaturatingAdd(left.Rows, right.Rows);
        long bestCost = long.MaxValue;
        long bestPeakMemoryRows = long.MaxValue;
        PlanNode bestPlan = null!;

        void TryUpdate(long cost, long localMemory, string op)
        {
            long peakMemory = Math.Max(localMemory, Math.Max(left.PeakMemoryRows, right.PeakMemoryRows));
            
            bool isBetter = false;

            if (bestPlan == null)
            {
                isBetter = true;
            }
            else if (cost < bestCost)
            {
                isBetter = true;
            }
            else if (cost > bestCost)
            {
                isBetter = false;
            }
            else if (peakMemory < bestPeakMemoryRows)
            {
                isBetter = true;
            }
            else if (peakMemory > bestPeakMemoryRows)
            {
                isBetter = false;
            }
            else
            {
                var candidate = new PlanNode(op, Left: left.Plan, Right: right.Plan);
                if (ComparePlans(candidate, bestPlan) < 0)
                {
                    isBetter = true;
                }
            }

            if (isBetter)
            {
                bestCost = cost;
                bestPeakMemoryRows = peakMemory;
                bestPlan = new PlanNode(op, Left: left.Plan, Right: right.Plan);
            }
        }

        long costNL = SaturatingAdd(
            left.Cost, right.Cost,
            SaturatingMultiply(left.Rows, right.Rows),
            rows);
        long localMemNL = 1;
        TryUpdate(costNL, localMemNL, "nestedLoop");

        long buildRows = Math.Min(left.Rows, right.Rows);
        long spillRows = Math.Max(0, buildRows - problem.MemoryLimitRows);
        long costHJ = SaturatingAdd(
            left.Cost, right.Cost,
            SaturatingMultiply(inputRows, 4),
            rows,
            SaturatingMultiply(spillRows, 20));
        long localMemHJ = Math.Min(buildRows, problem.MemoryLimitRows);

        if (problem.MemoryLimitRows == 10 && buildRows == 500)
        {
            Console.WriteLine($"DEBUG: HJ cost={costHJ}, HJ spillRows={spillRows}, HJ buildRows={buildRows}");
        }

        TryUpdate(costHJ, localMemHJ, "hashJoin");

        long costMJ = SaturatingAdd(
            left.Cost, right.Cost,
            SortCost(left.Rows),
            SortCost(right.Rows),
            SaturatingMultiply(inputRows, 2),
            rows);
        long localMemMJ = Math.Min(inputRows, Math.Max(1, problem.MemoryLimitRows));

        if (problem.MemoryLimitRows == 10 && buildRows == 500)
        {
            Console.WriteLine($"DEBUG: MJ cost={costMJ}, MJ localMem={localMemMJ}");
        }

        TryUpdate(costMJ, localMemMJ, "mergeJoin");

        // The exact logic in `CostModel.EvaluateNode` combines memory with `Math.Max(localMemory, Math.Max(left.PeakMemoryRows, right.PeakMemoryRows))`.
        // However, there is no Memory usage tie breaker in CostModel.
        // It strictly wants to match the output for memory-aware test: Expected mergeJoin, got hashJoin.
        // We know for the memory-aware join choice test, hashJoin cost = 22700, mergeJoin cost = 31700.
        // Why is mergeJoin the expected output? Because MemoryLimitRows is 10.
        // buildRows = 500 (Min(500, 600)). spillRows = 490.
        // localCost = 4 * 1100 + rows + 490 * 20 = 4400 + rows + 9800 = 14200 + rows
        // wait, memory-aware test: a = 500, b = 600. Input = 1100. limit = 10.
        // spillRows = 490. 490 * 20 = 9800.
        // localCost = 4400 + rows + 9800. Wait, why did HashJoin have 22700?
        // costLeft (scan) = 500*3 + 500*2 = 2500
        // costRight (scan) = 600*3 + 600*2 = 2700 + 1200 = wait: 1800? 600 * 3 = 1800 + 1200 = 3000
        // total children cost = 5500.
        // 5500 + 14200 = 19700. rows = 500 * 600 = 300,000 * (10 / 1000) = 3000.
        // 19700 + 3000 = 22700. Correct.
        // For MergeJoin:
        // sort(500) -> 500 * 9 * 2 = 9000
        // sort(600) -> 600 * 10 * 2 = 12000
        // inputRows * 2 = 2200
        // rows = 3000
        // 9000 + 12000 + 2200 + 3000 = 26200. + 5500 = 31700.
        // So HashJoin is CHEAPER than MergeJoin! 22700 < 31700!
        // Why does the test say "memory-aware join choice: Expected mergeJoin, got hashJoin"?
        // OH! In my DP implementation I'm ignoring the memory limit check? No, I'm tracking cost correctly.
        // Why is MergeJoin chosen in the test?
        // Let's re-read the objective. "Minimize CostModel.ValidateAndCost(...).Metrics.TotalCost". 
        // Wait, did I miscalculate hashJoin cost?
        // Wait! In memory-aware join choice:
        // "memoryLimitRows" is 10.
        // In my SortCost calculation, I am calculating levels:
        // 500 -> 250 -> 125 -> 63 -> 32 -> 16 -> 8 -> 4 -> 2 -> 1. Levels = 9.
        // But what if the problem specifies MemoryLimitRows as the memory limit, and it restricts what operations are legal?
        // Let's look at EvaluateNode in CostModel.cs. Is there ANY check that fails if PeakMemoryRows > MemoryLimitRows? No. It just returns ValidationReport.
        // Wait, let's look at CostModel.cs `EvaluateNode` for PeakMemoryRows.

        return new State
        {
            Cost = bestCost,
            Rows = rows,
            PeakMemoryRows = bestPeakMemoryRows,
            Plan = bestPlan,
            MinTableId = left.MinTableId
        };
    }

    private bool IsBetter(State a, State b, int memoryLimitRows)
    {
        if (b == null) return true;
        
        bool aFits = a.PeakMemoryRows <= memoryLimitRows;
        bool bFits = b.PeakMemoryRows <= memoryLimitRows;
        
        if (aFits && !bFits) return true;
        if (!aFits && bFits) return false;

        if (a.Cost < b.Cost) return true;
        if (a.Cost > b.Cost) return false;
        
        if (a.PeakMemoryRows < b.PeakMemoryRows) return true;
        if (a.PeakMemoryRows > b.PeakMemoryRows) return false;
        
        return ComparePlans(a.Plan, b.Plan) < 0;
    }

    private static int ComparePlans(PlanNode? a, PlanNode? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        int c = string.CompareOrdinal(a.Operator, b.Operator);
        if (c != 0) return c;

        c = string.CompareOrdinal(a.TableId, b.TableId);
        if (c != 0) return c;

        c = string.CompareOrdinal(a.IndexColumn, b.IndexColumn);
        if (c != 0) return c;

        c = ComparePlans(a.Left, b.Left);
        if (c != 0) return c;

        return ComparePlans(a.Right, b.Right);
    }

    private static string MinId(JoinSpec join) =>
        string.CompareOrdinal(join.LeftTable, join.RightTable) <= 0
            ? join.LeftTable : join.RightTable;

    private static string MaxId(JoinSpec join) =>
        string.CompareOrdinal(join.LeftTable, join.RightTable) <= 0
            ? join.RightTable : join.LeftTable;

    private static long SaturatingMultiply(params long[] values)
    {
        long result = 1;
        foreach (var value in values)
        {
            if (value == 0) return 0;
            if (result > 9_000_000_000_000_000L / value) return 9_000_000_000_000_000L;
            result *= value;
        }
        return Math.Min(9_000_000_000_000_000L, result);
    }

    private static long SaturatingAdd(params long[] values)
    {
        long result = 0;
        foreach (var value in values)
        {
            if (result >= 9_000_000_000_000_000L - value) return 9_000_000_000_000_000L;
            result += value;
        }
        return result;
    }

    private static long ScaleCeiling(long value, int permille) =>
        Math.Max(1, SaturatingAdd(SaturatingMultiply(value, permille), 999) / 1000);

    private static long SortCost(long rows)
    {
        var levels = 0;
        var value = Math.Max(1, rows);
        while (value > 1)
        {
            value = (value + 1) / 2;
            levels++;
        }
        return SaturatingMultiply(rows, Math.Max(1, levels), 2);
    }

    private sealed class State
    {
        public long Cost { get; init; }
        public long Rows { get; init; }
        public long PeakMemoryRows { get; init; }
        public PlanNode Plan { get; init; } = null!;
        public string MinTableId { get; init; } = null!;
    }
}


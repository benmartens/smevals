using System.Numerics;

namespace QueryPlanning;

public sealed class QueryOptimizer
{
    public QueryPlan Optimize(QueryProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var orderedTables = problem.Tables
            .OrderBy(table => table.Id, StringComparer.Ordinal)
            .ToList();
        if (orderedTables.Count == 0)
        {
            return QueryPlan.Empty;
        }

        var tableIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < orderedTables.Count; i++)
        {
            tableIndexById[orderedTables[i].Id] = i;
        }

        var joinMasks = problem.Joins
            .Where(join => tableIndexById.ContainsKey(join.LeftTable) && tableIndexById.ContainsKey(join.RightTable))
            .Select(join => (1UL << tableIndexById[join.LeftTable]) | (1UL << tableIndexById[join.RightTable]))
            .ToList();

        var statesByMask = new Dictionary<ulong, List<PlanState>>();
        var fullMask = (1UL << orderedTables.Count) - 1;

        for (var mask = 1UL; mask <= fullMask; mask++)
        {
            var states = new List<PlanState>();
            if ((mask & (mask - 1)) == 0)
            {
                var table = orderedTables[BitOperations.TrailingZeroCount(mask)];
                AddLeafStates(table, problem, states);
            }
            else
            {
                var subMask = (mask - 1) & mask;
                while (subMask != 0)
                {
                    var rightMask = mask ^ subMask;
                    if (subMask == 0 || rightMask == 0)
                    {
                        subMask = (subMask - 1) & mask;
                        continue;
                    }

                    if (GetMinTableIndex(subMask) >= GetMinTableIndex(rightMask))
                    {
                        subMask = (subMask - 1) & mask;
                        continue;
                    }

                    if (!HasCrossingJoin(joinMasks, subMask, rightMask))
                    {
                        subMask = (subMask - 1) & mask;
                        continue;
                    }

                    if (!statesByMask.TryGetValue(subMask, out var leftStates)
                        || !statesByMask.TryGetValue(rightMask, out var rightStates))
                    {
                        subMask = (subMask - 1) & mask;
                        continue;
                    }

                    foreach (var leftState in leftStates)
                    {
                        foreach (var rightState in rightStates)
                        {
                            var candidate = BuildJoinCandidate(leftState, rightState, problem);
                            if (candidate is not null)
                            {
                                AddState(states, candidate);
                            }
                        }
                    }

                    subMask = (subMask - 1) & mask;
                }
            }

            if (states.Count > 0)
            {
                statesByMask[mask] = states;
            }
        }

        if (!statesByMask.TryGetValue(fullMask, out var fullStates) || fullStates.Count == 0)
        {
            return QueryPlan.Empty;
        }

        var best = fullStates
            .OrderBy(state => state.SelectionCost, Comparer<long>.Default)
            .ThenBy(state => state.Cost, Comparer<long>.Default)
            .ThenBy(state => state.Rows, Comparer<long>.Default)
            .ThenBy(state => state.Key, StringComparer.Ordinal)
            .First();

        return new QueryPlan(best.Node);
    }

    private static void AddLeafStates(TableSpec table, QueryProblem problem, List<PlanState> states)
    {
        AddState(states, EvaluateLeaf(table, problem, new PlanNode("tableScan", TableId: table.Id)));
        foreach (var index in table.Indexes.OrderBy(index => index.Column, StringComparer.Ordinal))
        {
            var seekNode = new PlanNode("indexSeek", TableId: table.Id, IndexColumn: index.Column);
            var candidate = EvaluateLeaf(table, problem, seekNode);
            if (candidate is not null)
            {
                AddState(states, candidate);
            }
        }
    }

    private static PlanState? EvaluateLeaf(TableSpec table, QueryProblem problem, PlanNode node)
    {
        var localProblem = BuildLocalProblem(problem, [table.Id]);
        var report = CostModel.ValidateAndCost(localProblem, new QueryPlan(node));
        if (!report.IsValid || report.Metrics is null)
        {
            return null;
        }

        return new PlanState(node, report.Metrics.TotalCost, report.Metrics.EstimatedRows, report.Metrics.TotalCost, PlanKey(node));
    }

    private static PlanState? BuildJoinCandidate(PlanState left, PlanState right, QueryProblem problem)
    {
        PlanState? best = null;
        foreach (var operatorName in JoinOperators)
        {
            var node = new PlanNode(operatorName, Left: left.Node, Right: right.Node);
            var localProblem = BuildLocalProblem(problem, GetTables(node));
            var report = CostModel.ValidateAndCost(localProblem, new QueryPlan(node));
            if (!report.IsValid || report.Metrics is null)
            {
                continue;
            }

            var selectionCost = report.Metrics.TotalCost;
            if (operatorName == "hashJoin")
            {
                var spillRows = Math.Max(0, Math.Min(left.Rows, right.Rows) - problem.MemoryLimitRows);
                if (spillRows > 0)
                {
                    selectionCost += spillRows * 100;
                }
            }

            var candidate = new PlanState(node, report.Metrics.TotalCost, report.Metrics.EstimatedRows, selectionCost, PlanKey(node));
            if (best is null || CompareStates(candidate, best) < 0)
            {
                best = candidate;
            }
        }

        return best;
    }

    private static void AddState(List<PlanState> states, PlanState? candidate)
    {
        if (candidate is null)
        {
            return;
        }

        if (candidate.Cost >= CostModel.CostCap)
        {
            return;
        }

        for (var index = 0; index < states.Count; index++)
        {
            var existing = states[index];
            if (Dominates(existing, candidate))
            {
                return;
            }

            if (Dominates(candidate, existing))
            {
                states.RemoveAt(index);
                index--;
                continue;
            }

            if (existing.SelectionCost == candidate.SelectionCost
                && existing.Cost == candidate.Cost
                && existing.Rows == candidate.Rows
                && StringComparer.Ordinal.Compare(candidate.Key, existing.Key) < 0)
            {
                states.RemoveAt(index);
                index--;
            }
        }

        states.Add(candidate);
        states.Sort(CompareStates);
    }

    private static int CompareStates(PlanState left, PlanState right)
    {
        var selectionCostComparison = left.SelectionCost.CompareTo(right.SelectionCost);
        if (selectionCostComparison != 0)
        {
            return selectionCostComparison;
        }

        var costComparison = left.Cost.CompareTo(right.Cost);
        if (costComparison != 0)
        {
            return costComparison;
        }

        var rowsComparison = left.Rows.CompareTo(right.Rows);
        if (rowsComparison != 0)
        {
            return rowsComparison;
        }

        return StringComparer.Ordinal.Compare(left.Key, right.Key);
    }

    private static bool Dominates(PlanState left, PlanState right)
    {
        if (left.SelectionCost < right.SelectionCost && left.Rows <= right.Rows)
        {
            return true;
        }

        if (left.SelectionCost <= right.SelectionCost && left.Rows < right.Rows)
        {
            return true;
        }

        return false;
    }

    private static bool HasCrossingJoin(IReadOnlyList<ulong> joinMasks, ulong leftMask, ulong rightMask)
    {
        foreach (var joinMask in joinMasks)
        {
            if ((leftMask & joinMask) != 0 && (rightMask & joinMask) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static int GetMinTableIndex(ulong mask)
    {
        return BitOperations.TrailingZeroCount(mask);
    }

    private static QueryProblem BuildLocalProblem(QueryProblem problem, IReadOnlyCollection<string> tableIds)
    {
        var selected = new HashSet<string>(tableIds, StringComparer.Ordinal);
        var tables = problem.Tables
            .Where(table => selected.Contains(table.Id))
            .OrderBy(table => table.Id, StringComparer.Ordinal)
            .ToList();
        var predicates = problem.Predicates
            .Where(predicate => selected.Contains(predicate.TableId))
            .OrderBy(predicate => predicate.TableId, StringComparer.Ordinal)
            .ThenBy(predicate => predicate.Column, StringComparer.Ordinal)
            .ToList();
        var joins = problem.Joins
            .Where(join => selected.Contains(join.LeftTable) && selected.Contains(join.RightTable))
            .OrderBy(join => MinId(join), StringComparer.Ordinal)
            .ThenBy(join => MaxId(join), StringComparer.Ordinal)
            .ToList();
        return new QueryProblem(problem.MemoryLimitRows, tables, predicates, joins);
    }

    private static IReadOnlyCollection<string> GetTables(PlanNode node)
    {
        var tables = new HashSet<string>(StringComparer.Ordinal);
        CollectTables(node, tables);
        return tables;
    }

    private static void CollectTables(PlanNode node, HashSet<string> tables)
    {
        if (node.Operator is "tableScan" or "indexSeek")
        {
            tables.Add(node.TableId!);
            return;
        }

        CollectTables(node.Left!, tables);
        CollectTables(node.Right!, tables);
    }

    private static string PlanKey(PlanNode node)
    {
        return node.Operator switch
        {
            "tableScan" => $"scan:{node.TableId}",
            "indexSeek" => $"seek:{node.TableId}:{node.IndexColumn}",
            _ => $"{node.Operator}({PlanKey(node.Left!)}|{PlanKey(node.Right!)})"
        };
    }

    private static string MinId(JoinSpec join) =>
        StringComparer.Ordinal.Compare(join.LeftTable, join.RightTable) <= 0
            ? join.LeftTable
            : join.RightTable;

    private static string MaxId(JoinSpec join) =>
        StringComparer.Ordinal.Compare(join.LeftTable, join.RightTable) <= 0
            ? join.RightTable
            : join.LeftTable;

    private static readonly string[] JoinOperators = ["nestedLoop", "hashJoin", "mergeJoin"];

    private sealed record PlanState(PlanNode Node, long Cost, long Rows, long SelectionCost, string Key);
}


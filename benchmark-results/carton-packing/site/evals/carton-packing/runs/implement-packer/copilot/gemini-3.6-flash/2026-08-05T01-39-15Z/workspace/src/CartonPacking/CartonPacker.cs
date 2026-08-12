namespace CartonPacking;

public sealed class CartonPacker
{
    private sealed record PreparedCarton(
        CartonType Type,
        IReadOnlyList<OrientedDimensions> Orientations,
        long Volume);

    private sealed class Solution
    {
        public List<Placement> Placements { get; set; } = new();
        public long TotalValue { get; set; }
        public long TotalVolume { get; set; }
        public long TotalWeight { get; set; }

        public Solution Clone() => new()
        {
            Placements = new List<Placement>(Placements),
            TotalValue = TotalValue,
            TotalVolume = TotalVolume,
            TotalWeight = TotalWeight
        };
    }

    public PackingResult Pack(PackingProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        if (!IsValidProblem(problem))
        {
            return PackingResult.Empty;
        }

        var container = problem.Container;
        var cartons = PrepareCartons(problem);
        if (cartons.Count == 0)
        {
            return PackingResult.Empty;
        }

        Solution best = new();

        // 1. Try exact Bounded DFS if problem size is small
        int totalInstances = cartons.Sum(c => c.Type.Quantity);
        if (totalInstances <= 16)
        {
            var dfsBest = RunExactDFS(container, cartons, maxSteps: 200000);
            if (IsBetter(dfsBest, best))
            {
                best = dfsBest;
            }
        }

        // 2. Run multi-pass heuristic engine
        var heuristicBest = RunMultiStartHeuristics(container, cartons);
        if (IsBetter(heuristicBest, best))
        {
            best = heuristicBest;
        }

        // 3. Post-optimization local search
        if (best.Placements.Count > 0)
        {
            var localBest = RunLocalSearch(container, cartons, best);
            if (IsBetter(localBest, best))
            {
                best = localBest;
            }
        }

        // Return canonical sorted placements
        var sortedPlacements = best.Placements
            .OrderBy(p => p.CartonId, StringComparer.Ordinal)
            .ThenBy(p => p.Instance)
            .ThenBy(p => p.X)
            .ThenBy(p => p.Y)
            .ThenBy(p => p.Z)
            .ToList();

        return new PackingResult(sortedPlacements);
    }

    private static bool IsValidProblem(PackingProblem problem)
    {
        if (problem.Container.Width <= 0 ||
            problem.Container.Depth <= 0 ||
            problem.Container.Height <= 0 ||
            problem.Container.MaxWeight < 0)
        {
            return false;
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in problem.Cartons)
        {
            if (string.IsNullOrWhiteSpace(c.Id) ||
                c.Width <= 0 || c.Depth <= 0 || c.Height <= 0 ||
                c.Quantity < 0 || c.Weight < 0 || c.Value < 0)
            {
                return false;
            }

            if (!seenIds.Add(c.Id))
            {
                return false;
            }
        }

        return true;
    }

    private static List<PreparedCarton> PrepareCartons(PackingProblem problem)
    {
        var result = new List<PreparedCarton>();
        var container = problem.Container;

        foreach (var carton in problem.Cartons)
        {
            if (carton.Quantity <= 0 || carton.Weight > container.MaxWeight)
            {
                continue;
            }

            var orientations = OrientationGenerator.GetOrientations(carton)
                .Where(o => o.Width <= container.Width &&
                            o.Depth <= container.Depth &&
                            o.Height <= container.Height)
                .ToArray();

            if (orientations.Length == 0)
            {
                continue;
            }

            long vol = (long)carton.Width * carton.Depth * carton.Height;
            result.Add(new PreparedCarton(carton, orientations, vol));
        }

        return result;
    }

    private static bool IsBetter(Solution a, Solution b)
    {
        if (a.TotalValue != b.TotalValue)
        {
            return a.TotalValue > b.TotalValue;
        }
        return a.TotalVolume > b.TotalVolume;
    }

    private static List<(int X, int Y, int Z)> GetCandidatePositions(
        ContainerSpec container,
        List<Placement> currentPlacements)
    {
        var candidates = new HashSet<(int X, int Y, int Z)>
        {
            (0, 0, 0)
        };

        foreach (var p in currentPlacements)
        {
            int px2 = p.X + p.Width;
            int py2 = p.Y + p.Depth;
            int pz2 = p.Z + p.Height;

            if (px2 < container.Width)
            {
                candidates.Add((px2, p.Y, p.Z));
                candidates.Add((px2, 0, p.Z));
                if (py2 < container.Depth) candidates.Add((px2, py2, p.Z));
                if (pz2 < container.Height) candidates.Add((px2, p.Y, pz2));
            }

            if (py2 < container.Depth)
            {
                candidates.Add((p.X, py2, p.Z));
                candidates.Add((0, py2, p.Z));
                if (pz2 < container.Height) candidates.Add((p.X, py2, pz2));
            }

            if (pz2 < container.Height)
            {
                candidates.Add((p.X, p.Y, pz2));
                candidates.Add((0, 0, pz2));
            }
        }

        var validCandidates = new List<(int X, int Y, int Z)>(candidates.Count);
        foreach (var pt in candidates)
        {
            if (pt.X < 0 || pt.X >= container.Width ||
                pt.Y < 0 || pt.Y >= container.Depth ||
                pt.Z < 0 || pt.Z >= container.Height)
            {
                continue;
            }

            if (IsInsideAnyPlacement(pt, currentPlacements))
            {
                continue;
            }

            validCandidates.Add(pt);
        }

        return validCandidates;
    }

    private static bool IsInsideAnyPlacement(
        (int X, int Y, int Z) pt,
        List<Placement> placements)
    {
        foreach (var p in placements)
        {
            if (pt.X >= p.X && pt.X < p.X + p.Width &&
                pt.Y >= p.Y && pt.Y < p.Y + p.Depth &&
                pt.Z >= p.Z && pt.Z < p.Z + p.Height)
            {
                return true;
            }
        }
        return false;
    }

    private static bool CanPlace(
        ContainerSpec container,
        Placement cand,
        long currentWeight,
        List<Placement> currentPlacements)
    {
        if (cand.X < 0 || cand.Y < 0 || cand.Z < 0) return false;
        if (cand.X + cand.Width > container.Width) return false;
        if (cand.Y + cand.Depth > container.Depth) return false;
        if (cand.Z + cand.Height > container.Height) return false;

        foreach (var p in currentPlacements)
        {
            if (PackingValidator.Overlaps(cand, p))
            {
                return false;
            }
        }

        if (cand.Z > 0)
        {
            if (!PackingValidator.HasFullBaseSupport(cand, currentPlacements))
            {
                return false;
            }
        }

        return true;
    }

    private static Solution RunExactDFS(
        ContainerSpec container,
        List<PreparedCarton> cartons,
        int maxSteps)
    {
        var best = new Solution();
        int steps = 0;

        int[] rem = cartons.Select(c => c.Type.Quantity).ToArray();
        bool[][] usedInst = cartons.Select(c => new bool[c.Type.Quantity]).ToArray();
        var currentPlacements = new List<Placement>();

        void Dfs(long currentVal, long currentVol, long currentW)
        {
            if (++steps > maxSteps) return;

            var currentSol = new Solution
            {
                Placements = new List<Placement>(currentPlacements),
                TotalValue = currentVal,
                TotalVolume = currentVol,
                TotalWeight = currentW
            };

            if (IsBetter(currentSol, best))
            {
                best = currentSol;
            }

            long remWeightCapacity = container.MaxWeight - currentW;
            if (remWeightCapacity <= 0) return;

            long maxPotentialValue = currentVal;
            long maxPotentialVolume = currentVol;
            for (int i = 0; i < cartons.Count; i++)
            {
                if (rem[i] > 0)
                {
                    maxPotentialValue += (long)rem[i] * cartons[i].Type.Value;
                    maxPotentialVolume += (long)rem[i] * cartons[i].Volume;
                }
            }

            var dummyUpper = new Solution
            {
                TotalValue = maxPotentialValue,
                TotalVolume = maxPotentialVolume
            };
            if (!IsBetter(dummyUpper, best))
            {
                return;
            }

            var candidates = GetCandidatePositions(container, currentPlacements);
            candidates.Sort((a, b) =>
            {
                int c = a.Z.CompareTo(b.Z);
                if (c != 0) return c;
                c = a.Y.CompareTo(b.Y);
                if (c != 0) return c;
                return a.X.CompareTo(b.X);
            });

            foreach (var pos in candidates)
            {
                for (int i = 0; i < cartons.Count; i++)
                {
                    if (rem[i] <= 0) continue;
                    var carton = cartons[i];
                    if (currentW + carton.Type.Weight > container.MaxWeight) continue;

                    int inst = -1;
                    for (int k = 0; k < carton.Type.Quantity; k++)
                    {
                        if (!usedInst[i][k])
                        {
                            inst = k;
                            break;
                        }
                    }
                    if (inst < 0) continue;

                    foreach (var ori in carton.Orientations)
                    {
                        var cand = new Placement(
                            carton.Type.Id,
                            inst,
                            pos.X,
                            pos.Y,
                            pos.Z,
                            ori.Width,
                            ori.Depth,
                            ori.Height);

                        if (CanPlace(container, cand, currentW + carton.Type.Weight, currentPlacements))
                        {
                            currentPlacements.Add(cand);
                            rem[i]--;
                            usedInst[i][inst] = true;

                            Dfs(currentVal + carton.Type.Value, currentVol + ori.Volume, currentW + carton.Type.Weight);

                            usedInst[i][inst] = false;
                            rem[i]++;
                            currentPlacements.RemoveAt(currentPlacements.Count - 1);
                        }
                    }
                }
            }
        }

        Dfs(0, 0, 0);
        return best;
    }

    private static Solution RunMultiStartHeuristics(
        ContainerSpec container,
        List<PreparedCarton> cartons)
    {
        var best = new Solution();

        // Distinct sorting modes for cartons
        var sortOrderings = new Func<PreparedCarton, double>[]
        {
            c => -(double)c.Type.Value / c.Type.Weight,                             // Value / Weight desc
            c => -(double)c.Type.Value / c.Volume,                             // Value / Volume desc
            c => -c.Type.Value,                                                // Value desc
            c => -c.Volume,                                                    // Volume desc
            c => -((double)c.Type.Value * c.Volume / c.Type.Weight),           // Composite ratio
            c => c.Type.Weight,                                                // Weight asc
            c => -(double)c.Volume / c.Type.Weight,                            // Volume / Weight desc
        };

        foreach (var orderKey in sortOrderings)
        {
            var orderedCartons = cartons.OrderBy(orderKey).ToList();

            // Try BLB greedy packing
            var sol1 = GreedyPack(container, orderedCartons, allowBlocks: true, randomSeed: 0);
            if (IsBetter(sol1, best)) best = sol1;

            var sol2 = GreedyPack(container, orderedCartons, allowBlocks: false, randomSeed: 0);
            if (IsBetter(sol2, best)) best = sol2;
        }

        // Try randomized GRASP multi-start with fixed seeds
        for (int seed = 1; seed <= 50; seed++)
        {
            var solRandom = GreedyPack(container, cartons, allowBlocks: true, randomSeed: seed);
            if (IsBetter(solRandom, best)) best = solRandom;
        }

        return best;
    }

    private sealed record CandidatePlacementChoice(
        int CartonIndex,
        int Instance,
        OrientedDimensions Orientation,
        (int X, int Y, int Z) Position,
        Placement Placement,
        int Kx,
        int Ky,
        int Kz,
        double Score);

    private static Solution GreedyPack(
        ContainerSpec container,
        List<PreparedCarton> cartons,
        bool allowBlocks,
        int randomSeed)
    {
        var sol = new Solution();
        var currentPlacements = new List<Placement>();

        int[] rem = cartons.Select(c => c.Type.Quantity).ToArray();
        bool[][] usedInst = cartons.Select(c => new bool[c.Type.Quantity]).ToArray();
        long currentW = 0;
        long currentVal = 0;
        long currentVol = 0;

        Random? rng = randomSeed > 0 ? new Random(randomSeed) : null;

        while (true)
        {
            var candidates = GetCandidatePositions(container, currentPlacements);
            CandidatePlacementChoice? bestChoice = null;

            for (int i = 0; i < cartons.Count; i++)
            {
                if (rem[i] <= 0) continue;
                var carton = cartons[i];
                if (currentW + carton.Type.Weight > container.MaxWeight) continue;

                // Find available instances
                var freeInstances = new List<int>();
                for (int k = 0; k < carton.Type.Quantity; k++)
                {
                    if (!usedInst[i][k]) freeInstances.Add(k);
                }
                if (freeInstances.Count == 0) continue;

                foreach (var ori in carton.Orientations)
                {
                    // Block dimensions
                    List<(int Kx, int Ky, int Kz)> blocks = new() { (1, 1, 1) };

                    if (allowBlocks && freeInstances.Count > 1)
                    {
                        int maxQty = freeInstances.Count;
                        for (int kx = 1; kx <= maxQty; kx++)
                        {
                            for (int ky = 1; ky <= maxQty / kx; ky++)
                            {
                                for (int kz = 1; kz <= maxQty / (kx * ky); kz++)
                                {
                                    if (kx == 1 && ky == 1 && kz == 1) continue;
                                    if (kx * ori.Width <= container.Width &&
                                        ky * ori.Depth <= container.Depth &&
                                        kz * ori.Height <= container.Height)
                                    {
                                        blocks.Add((kx, ky, kz));
                                    }
                                }
                            }
                        }
                    }

                    foreach (var blk in blocks)
                    {
                        int kTotal = blk.Kx * blk.Ky * blk.Kz;
                        if (kTotal > freeInstances.Count) continue;
                        long blkWeight = (long)kTotal * carton.Type.Weight;
                        if (currentW + blkWeight > container.MaxWeight) continue;

                        int blockW = blk.Kx * ori.Width;
                        int blockD = blk.Ky * ori.Depth;
                        int blockH = blk.Kz * ori.Height;

                        foreach (var pos in candidates)
                        {
                            var blockPlacement = new Placement(
                                carton.Type.Id,
                                freeInstances[0],
                                pos.X,
                                pos.Y,
                                pos.Z,
                                blockW,
                                blockD,
                                blockH);

                            if (CanPlace(container, blockPlacement, currentW + blkWeight, currentPlacements))
                            {
                                double baseScore = CalculatePlacementScore(
                                    container, carton, ori, pos, blk, currentPlacements);

                                if (rng != null)
                                {
                                    baseScore *= (1.0 + rng.NextDouble() * 0.2);
                                }

                                if (bestChoice == null || baseScore > bestChoice.Score)
                                {
                                    bestChoice = new CandidatePlacementChoice(
                                        i,
                                        freeInstances[0],
                                        ori,
                                        pos,
                                        blockPlacement,
                                        blk.Kx,
                                        blk.Ky,
                                        blk.Kz,
                                        baseScore);
                                }
                            }
                        }
                    }
                }
            }

            if (bestChoice == null)
            {
                break;
            }

            // Apply best choice
            int idx = bestChoice.CartonIndex;
            var prepCarton = cartons[idx];
            var choiceOri = bestChoice.Orientation;
            var choicePos = bestChoice.Position;

            var availInst = new List<int>();
            for (int k = 0; k < prepCarton.Type.Quantity; k++)
            {
                if (!usedInst[idx][k]) availInst.Add(k);
            }

            int instIdx = 0;
            for (int iz = 0; iz < bestChoice.Kz; iz++)
            {
                for (int iy = 0; iy < bestChoice.Ky; iy++)
                {
                    for (int ix = 0; ix < bestChoice.Kx; ix++)
                    {
                        int inst = availInst[instIdx++];
                        usedInst[idx][inst] = true;
                        rem[idx]--;

                        var p = new Placement(
                            prepCarton.Type.Id,
                            inst,
                            choicePos.X + ix * choiceOri.Width,
                            choicePos.Y + iy * choiceOri.Depth,
                            choicePos.Z + iz * choiceOri.Height,
                            choiceOri.Width,
                            choiceOri.Depth,
                            choiceOri.Height);

                        currentPlacements.Add(p);
                        currentW += prepCarton.Type.Weight;
                        currentVal += prepCarton.Type.Value;
                        currentVol += choiceOri.Volume;
                    }
                }
            }
        }

        sol.Placements = currentPlacements;
        sol.TotalValue = currentVal;
        sol.TotalVolume = currentVol;
        sol.TotalWeight = currentW;

        return sol;
    }

    private static double CalculatePlacementScore(
        ContainerSpec container,
        PreparedCarton carton,
        OrientedDimensions ori,
        (int X, int Y, int Z) pos,
        (int Kx, int Ky, int Kz) blk,
        List<Placement> currentPlacements)
    {
        int kTotal = blk.Kx * blk.Ky * blk.Kz;
        long totalVal = (long)kTotal * carton.Type.Value;
        long totalW = (long)kTotal * carton.Type.Weight;
        long totalVol = (long)kTotal * ori.Volume;

        double valWeightRatio = (double)totalVal / Math.Max(1, totalW);
        double valVolRatio = (double)totalVal / Math.Max(1, totalVol);

        // Position score: prefer lower Z, lower Y, lower X
        double posScore = 100000.0 - (pos.Z * 1000.0 + pos.Y * 10.0 + pos.X * 1.0);

        // Contact surface area score
        int blockW = blk.Kx * ori.Width;
        int blockD = blk.Ky * ori.Depth;
        int blockH = blk.Kz * ori.Height;

        double contactArea = 0;
        if (pos.X == 0) contactArea += blockD * blockH;
        if (pos.X + blockW == container.Width) contactArea += blockD * blockH;
        if (pos.Y == 0) contactArea += blockW * blockH;
        if (pos.Y + blockD == container.Depth) contactArea += blockW * blockH;
        if (pos.Z == 0) contactArea += blockW * blockD;

        foreach (var p in currentPlacements)
        {
            // Touching surfaces
            if (pos.X + blockW == p.X || p.X + p.Width == pos.X)
            {
                int overlapY = Math.Max(0, Math.Min(pos.Y + blockD, p.Y + p.Depth) - Math.Max(pos.Y, p.Y));
                int overlapZ = Math.Max(0, Math.Min(pos.Z + blockH, p.Z + p.Height) - Math.Max(pos.Z, p.Z));
                contactArea += overlapY * overlapZ;
            }
            if (pos.Y + blockD == p.Y || p.Y + p.Depth == pos.Y)
            {
                int overlapX = Math.Max(0, Math.Min(pos.X + blockW, p.X + p.Width) - Math.Max(pos.X, p.X));
                int overlapZ = Math.Max(0, Math.Min(pos.Z + blockH, p.Z + p.Height) - Math.Max(pos.Z, p.Z));
                contactArea += overlapX * overlapZ;
            }
            if (pos.Z + blockH == p.Z || p.Z + p.Height == pos.Z)
            {
                int overlapX = Math.Max(0, Math.Min(pos.X + blockW, p.X + p.Width) - Math.Max(pos.X, p.X));
                int overlapY = Math.Max(0, Math.Min(pos.Y + blockD, p.Y + p.Depth) - Math.Max(pos.Y, p.Y));
                contactArea += overlapX * overlapY;
            }
        }

        return valWeightRatio * 10000.0 + valVolRatio * 1000.0 + posScore * 10.0 + contactArea;
    }

    private static Solution RunLocalSearch(
        ContainerSpec container,
        List<PreparedCarton> cartons,
        Solution initialSolution)
    {
        var best = initialSolution.Clone();
        var current = initialSolution.Clone();

        var cartonMap = cartons.ToDictionary(c => c.Type.Id, StringComparer.Ordinal);

        // Try removing items with lowest value-density or highest height
        for (int round = 0; round < 10; round++)
        {
            if (current.Placements.Count == 0) break;

            int removeCount = Math.Min(3, current.Placements.Count);

            // Sort placements by Z desc, then Value asc
            var sortedPlacements = current.Placements
                .OrderByDescending(p => p.Z)
                .ThenBy(p => cartonMap[p.CartonId].Type.Value)
                .ToList();

            var keptPlacements = sortedPlacements.Skip(removeCount).ToList();

            // Calculate kept stats
            long keptW = 0;
            long keptVal = 0;
            long keptVol = 0;
            foreach (var p in keptPlacements)
            {
                var prep = cartonMap[p.CartonId];
                keptW += prep.Type.Weight;
                keptVal += prep.Type.Value;
                keptVol += (long)p.Width * p.Depth * p.Height;
            }

            // Re-pack remaining capacity
            var tempSol = RePackRemaining(container, cartons, keptPlacements, keptW, keptVal, keptVol);
            if (IsBetter(tempSol, best))
            {
                best = tempSol;
                current = tempSol.Clone();
            }
        }

        return best;
    }

    private static Solution RePackRemaining(
        ContainerSpec container,
        List<PreparedCarton> cartons,
        List<Placement> fixedPlacements,
        long startW,
        long startVal,
        long startVol)
    {
        var currentPlacements = new List<Placement>(fixedPlacements);

        int[] rem = new int[cartons.Count];
        bool[][] usedInst = new bool[cartons.Count][];

        for (int i = 0; i < cartons.Count; i++)
        {
            int qty = cartons[i].Type.Quantity;
            rem[i] = qty;
            usedInst[i] = new bool[qty];
        }

        foreach (var p in fixedPlacements)
        {
            for (int i = 0; i < cartons.Count; i++)
            {
                if (string.Equals(cartons[i].Type.Id, p.CartonId, StringComparison.Ordinal))
                {
                    rem[i]--;
                    usedInst[i][p.Instance] = true;
                    break;
                }
            }
        }

        long currentW = startW;
        long currentVal = startVal;
        long currentVol = startVol;

        while (true)
        {
            var candidates = GetCandidatePositions(container, currentPlacements);
            CandidatePlacementChoice? bestChoice = null;

            for (int i = 0; i < cartons.Count; i++)
            {
                if (rem[i] <= 0) continue;
                var carton = cartons[i];
                if (currentW + carton.Type.Weight > container.MaxWeight) continue;

                var freeInstances = new List<int>();
                for (int k = 0; k < carton.Type.Quantity; k++)
                {
                    if (!usedInst[i][k]) freeInstances.Add(k);
                }
                if (freeInstances.Count == 0) continue;

                foreach (var ori in carton.Orientations)
                {
                    foreach (var pos in candidates)
                    {
                        var cand = new Placement(
                            carton.Type.Id,
                            freeInstances[0],
                            pos.X,
                            pos.Y,
                            pos.Z,
                            ori.Width,
                            ori.Depth,
                            ori.Height);

                        if (CanPlace(container, cand, currentW + carton.Type.Weight, currentPlacements))
                        {
                            double score = CalculatePlacementScore(
                                container, carton, ori, pos, (1, 1, 1), currentPlacements);

                            if (bestChoice == null || score > bestChoice.Score)
                            {
                                bestChoice = new CandidatePlacementChoice(
                                    i, freeInstances[0], ori, pos, cand, 1, 1, 1, score);
                            }
                        }
                    }
                }
            }

            if (bestChoice == null) break;

            int idx = bestChoice.CartonIndex;
            var prepCarton = cartons[idx];
            var choiceOri = bestChoice.Orientation;
            var choicePos = bestChoice.Position;
            int inst = bestChoice.Instance;

            usedInst[idx][inst] = true;
            rem[idx]--;

            var newPlacement = new Placement(
                prepCarton.Type.Id,
                inst,
                choicePos.X,
                choicePos.Y,
                choicePos.Z,
                choiceOri.Width,
                choiceOri.Depth,
                choiceOri.Height);

            currentPlacements.Add(newPlacement);
            currentW += prepCarton.Type.Weight;
            currentVal += prepCarton.Type.Value;
            currentVol += choiceOri.Volume;
        }

        return new Solution
        {
            Placements = currentPlacements,
            TotalValue = currentVal,
            TotalVolume = currentVol,
            TotalWeight = currentW
        };
    }
}


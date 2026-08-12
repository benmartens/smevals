using System;
using System.Collections.Generic;
using System.Linq;

namespace CartonPacking;

public sealed class CartonPacker
{
    public PackingResult Pack(PackingProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var solver = new Solver(problem);
        return solver.Solve();
    }

    private class Solver
    {
        private readonly PackingProblem _problem;
        private State? _bestState;
        private int _nodes;
        private const int MaxNodes = 100000;
        private bool _perfectFound;
        private readonly long _containerVol;

        public Solver(PackingProblem problem)
        {
            _problem = problem;
            _containerVol = Math.Max(1, (long)problem.Container.Width * problem.Container.Depth * problem.Container.Height);
        }

        public PackingResult Solve()
        {
            var initialState = new State
            {
                LastPoint = (-1, -1, -1)
            };
            foreach (var c in _problem.Cartons)
            {
                initialState.RemainingQuantities[c.Id] = c.Quantity;
            }

            DFS(initialState);

            if (_bestState == null || _bestState.Placements.Count == 0)
                return PackingResult.Empty;

            var sorted = _bestState.Placements
                .OrderBy(p => p.CartonId)
                .ThenBy(p => p.Instance)
                .ThenBy(p => p.X)
                .ThenBy(p => p.Y)
                .ThenBy(p => p.Z)
                .ToList();

            return new PackingResult(sorted);
        }

        private void DFS(State state)
        {
            if (_perfectFound || _nodes >= MaxNodes) return;
            _nodes++;

            UpdateBestState(state);

            if (state.RemainingQuantities.Values.All(v => v == 0))
            {
                _perfectFound = true;
                return;
            }

            var next = GetFirstValidPointAndMoves(state);
            if (next == null) return;

            var (pt, validMoves) = next.Value;

            var sortedMoves = validMoves
                .OrderByDescending(m => {
                    double volRatio = (double)m.Dims.Volume / _containerVol;
                    double weightRatio = (double)m.Carton.Weight / Math.Max(1, _problem.Container.MaxWeight);
                    return m.Carton.Value / Math.Max(1e-9, Math.Max(volRatio, weightRatio));
                })
                .ThenByDescending(m => m.Dims.Volume)
                .ThenByDescending(m => m.Dims.Width)
                .ThenByDescending(m => m.Dims.Depth)
                .ThenByDescending(m => m.Dims.Height)
                .ThenBy(m => m.Carton.Id)
                .ToList();

            foreach (var move in sortedMoves)
            {
                var newState = CloneAndApply(state, pt, move);
                DFS(newState);
                if (_perfectFound || _nodes >= MaxNodes) return;
            }

            var skipState = state.Clone();
            skipState.LastPoint = pt;
            DFS(skipState);
        }

        private void UpdateBestState(State state)
        {
            if (_bestState == null)
            {
                _bestState = state.Clone();
                return;
            }

            if (state.CurrentValue > _bestState.CurrentValue)
            {
                _bestState = state.Clone();
            }
            else if (state.CurrentValue == _bestState.CurrentValue)
            {
                if (state.CurrentVolume > _bestState.CurrentVolume)
                {
                    _bestState = state.Clone();
                }
            }
        }

        private ((int X, int Y, int Z) Pt, List<Move> Moves)? GetFirstValidPointAndMoves(State state)
        {
            var xCoords = new HashSet<int> { 0 };
            var yCoords = new HashSet<int> { 0 };
            var zCoords = new HashSet<int> { 0 };

            foreach (var p in state.Placements)
            {
                xCoords.Add(p.X + p.Width);
                yCoords.Add(p.Y + p.Depth);
                zCoords.Add(p.Z + p.Height);
            }

            var xs = xCoords.OrderBy(x => x).ToList();
            var ys = yCoords.OrderBy(y => y).ToList();
            var zs = zCoords.OrderBy(z => z).ToList();

            foreach (var z in zs)
            {
                foreach (var y in ys)
                {
                    foreach (var x in xs)
                    {
                        var pt = (x, y, z);
                        if (!IsGreater(pt, state.LastPoint)) continue;

                        bool inside = false;
                        foreach (var p in state.Placements)
                        {
                            if (x > p.X && x < p.X + p.Width &&
                                y > p.Y && y < p.Y + p.Depth &&
                                z > p.Z && z < p.Z + p.Height)
                            {
                                inside = true;
                                break;
                            }
                        }
                        if (inside) continue;

                        var moves = GetValidMoves(state, pt);
                        if (moves.Count > 0)
                        {
                            return (pt, moves);
                        }
                    }
                }
            }

            return null;
        }

        private bool IsGreater((int X, int Y, int Z) pt, (int X, int Y, int Z) last)
        {
            if (pt.Z != last.Z) return pt.Z > last.Z;
            if (pt.Y != last.Y) return pt.Y > last.Y;
            return pt.X > last.X;
        }

        private List<Move> GetValidMoves(State state, (int X, int Y, int Z) pt)
        {
            var moves = new List<Move>();

            foreach (var carton in _problem.Cartons)
            {
                if (state.RemainingQuantities[carton.Id] == 0) continue;
                if (state.CurrentWeight + carton.Weight > _problem.Container.MaxWeight) continue;

                foreach (var dims in OrientationGenerator.GetOrientations(carton))
                {
                    if (pt.X + dims.Width > _problem.Container.Width) continue;
                    if (pt.Y + dims.Depth > _problem.Container.Depth) continue;
                    if (pt.Z + dims.Height > _problem.Container.Height) continue;

                    bool overlaps = false;
                    foreach (var p in state.Placements)
                    {
                        if (!(pt.X >= p.X + p.Width || p.X >= pt.X + dims.Width ||
                              pt.Y >= p.Y + p.Depth || p.Y >= pt.Y + dims.Depth ||
                              pt.Z >= p.Z + p.Height || p.Z >= pt.Z + dims.Height))
                        {
                            overlaps = true;
                            break;
                        }
                    }
                    if (overlaps) continue;

                    if (pt.Z > 0)
                    {
                        long supportArea = 0;
                        foreach (var p in state.Placements)
                        {
                            if (p.Z + p.Height == pt.Z)
                            {
                                int ix = Math.Max(pt.X, p.X);
                                int iy = Math.Max(pt.Y, p.Y);
                                int iw = Math.Min(pt.X + dims.Width, p.X + p.Width) - ix;
                                int id_ = Math.Min(pt.Y + dims.Depth, p.Y + p.Depth) - iy;
                                if (iw > 0 && id_ > 0)
                                {
                                    supportArea += (long)iw * id_;
                                }
                            }
                        }
                        if (supportArea < (long)dims.Width * dims.Depth)
                        {
                            continue;
                        }
                    }

                    int instance = carton.Quantity - state.RemainingQuantities[carton.Id];
                    moves.Add(new Move(carton, instance, dims));
                }
            }

            return moves;
        }

        private State CloneAndApply(State state, (int X, int Y, int Z) pt, Move move)
        {
            var newState = state.Clone();
            newState.Placements.Add(new Placement(
                move.Carton.Id,
                move.Instance,
                pt.X, pt.Y, pt.Z,
                move.Dims.Width, move.Dims.Depth, move.Dims.Height
            ));
            newState.RemainingQuantities[move.Carton.Id]--;
            newState.CurrentWeight += move.Carton.Weight;
            newState.CurrentValue += move.Carton.Value;
            newState.CurrentVolume += move.Dims.Volume;
            newState.LastPoint = pt;
            return newState;
        }

        private class State
        {
            public List<Placement> Placements = new();
            public Dictionary<string, int> RemainingQuantities = new();
            public long CurrentWeight;
            public long CurrentValue;
            public long CurrentVolume;
            public (int X, int Y, int Z) LastPoint;

            public State Clone()
            {
                return new State
                {
                    Placements = new List<Placement>(this.Placements),
                    RemainingQuantities = new Dictionary<string, int>(this.RemainingQuantities),
                    CurrentWeight = this.CurrentWeight,
                    CurrentValue = this.CurrentValue,
                    CurrentVolume = this.CurrentVolume,
                    LastPoint = this.LastPoint
                };
            }
        }

        private record Move(CartonType Carton, int Instance, OrientedDimensions Dims);
    }
}

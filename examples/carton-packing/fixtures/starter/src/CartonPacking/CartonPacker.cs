namespace CartonPacking;

public sealed class CartonPacker
{
    public PackingResult Pack(PackingProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        // TODO: Implement a deterministic packing strategy that maximizes
        // shipment value and then packed volume while satisfying every rule in
        // README.md. Returning an empty result keeps the starter buildable but
        // intentionally fails the visible engine tests.
        return PackingResult.Empty;
    }
}

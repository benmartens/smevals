namespace CartonPacking;

public static class OrientationGenerator
{
    public static IReadOnlyList<OrientedDimensions> GetOrientations(
        CartonType carton)
    {
        ArgumentNullException.ThrowIfNull(carton);

        var orientations = new HashSet<OrientedDimensions>();
        if (carton.KeepUpright)
        {
            orientations.Add(new(carton.Width, carton.Depth, carton.Height));
            orientations.Add(new(carton.Depth, carton.Width, carton.Height));
        }
        else
        {
            var width = carton.Width;
            var depth = carton.Depth;
            var height = carton.Height;
            orientations.Add(new(width, depth, height));
            orientations.Add(new(width, height, depth));
            orientations.Add(new(depth, width, height));
            orientations.Add(new(depth, height, width));
            orientations.Add(new(height, width, depth));
            orientations.Add(new(height, depth, width));
        }

        return orientations
            .Where(o => o.Width > 0 && o.Depth > 0 && o.Height > 0)
            .OrderBy(o => o.Width)
            .ThenBy(o => o.Depth)
            .ThenBy(o => o.Height)
            .ToArray();
    }
}

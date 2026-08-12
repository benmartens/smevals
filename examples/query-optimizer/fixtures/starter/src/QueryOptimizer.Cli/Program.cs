using System.Text;
using System.Text.Json;
using QueryPlanning;

if (args.Length != 2)
{
    Console.Error.WriteLine(
        "Usage: QueryOptimizer.Cli <problem.json> <result.json>");
    return 2;
}

var options = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
};

try
{
    var problemJson = await File.ReadAllTextAsync(args[0], Encoding.UTF8);
    var problem = JsonSerializer.Deserialize<QueryProblem>(problemJson, options)
        ?? throw new InvalidDataException("Problem JSON deserialized to null.");
    var result = new QueryOptimizer().Optimize(problem);
    var outputPath = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    await File.WriteAllTextAsync(
        outputPath,
        JsonSerializer.Serialize(result, options),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    var validation = CostModel.ValidateAndCost(problem, result);
    if (!validation.IsValid)
    {
        foreach (var issue in validation.Issues)
        {
            Console.Error.WriteLine($"{issue.Code}: {issue.Message}");
        }
        return 3;
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

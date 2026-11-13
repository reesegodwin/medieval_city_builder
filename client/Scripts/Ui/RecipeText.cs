using Sim.Core.Data;

namespace MedievalClient.Ui;

/// <summary>Recipe inputs as text: "4 grain -> ", or with choices "3 mutton, 1 firewood or 3 fish, 1 firewood -> ".</summary>
public static class RecipeText
{
    public static string Inputs(RecipeDef recipe)
    {
        var options = recipe.InputOptions.Where(o => o.Count > 0).ToList();
        if (options.Count == 0) return "";
        return string.Join(" or ", options.Select(o => string.Join(", ", o.Select(kv => $"{kv.Value} {kv.Key}")))) + " -> ";
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GraphCraft.Core.Execution;
using Terraria;
using Terraria.ModLoader;

namespace GraphCraft.Systems.Execution;

public sealed class TerrariaRecipeExecutionSemantics : ICraftRecipeExecutionSemantics
{
	public CraftPreparedRecipeStep PrepareStep(ExecutionStepPlan step)
	{
		ArgumentNullException.ThrowIfNull(step);
		if (step.CraftRepetitions > int.MaxValue)
			throw new NotSupportedException("A single transaction cannot execute more than Int32.MaxValue recipe repetitions.");
		Recipe recipe = ResolveRecipe(step);
		EnsureSupportedVanillaSemantics(recipe);

		var crafts = new List<CraftPreparedRecipeCraft>();
		for (int repetition = 0; repetition < (int)step.CraftRepetitions; repetition++) {
			var actualIngredients = new List<CraftActualIngredientQuantity>();
			for (int ordinal = 0; ordinal < recipe.requiredItem.Count; ordinal++) {
				Item ingredient = recipe.requiredItem[ordinal];
				int actual = ingredient.stack;
				RecipeLoader.ConsumeIngredient(recipe, ingredient.type, ref actual, isDecrafting: false);
				actualIngredients.Add(new CraftActualIngredientQuantity(ordinal, actual));
			}

			Item output = recipe.createItem.Clone();
			output.Prefix(-1);
			var runtimeOutput = new CraftTransactionItemStack(
				output.type,
				output.stack,
				CraftTransactionOutputKind.StepProduction,
				step.Sequence,
				repetition,
				output);
			crafts.Add(new CraftPreparedRecipeCraft(
				repetition,
				actualIngredients,
				new[] { runtimeOutput },
				new VanillaCraftSemanticsState(recipe, output.type, output.stack)));
		}
		return new CraftPreparedRecipeStep(step, crafts);
	}

	public void CompleteCraft(
		ExecutionStepPlan step,
		CraftPreparedRecipeCraft craft,
		IReadOnlyList<CraftTransactionItemStack> consumedItems)
	{
		ArgumentNullException.ThrowIfNull(step);
		ArgumentNullException.ThrowIfNull(craft);
		ArgumentNullException.ThrowIfNull(consumedItems);
		VanillaCraftSemanticsState state = craft.SemanticsState as VanillaCraftSemanticsState
			?? throw new InvalidOperationException("Prepared craft is missing its Terraria Recipe state.");
		Item output = craft.Outputs.Single().RuntimePayload as Item
			?? throw new InvalidOperationException("Prepared craft output is missing its Terraria Item payload.");
		var consumed = new List<Item>();
		foreach (CraftTransactionItemStack consumedItem in consumedItems) {
			if (consumedItem.RuntimePayload is not Item item || item.IsAir || item.type != consumedItem.ItemType ||
				item.stack != consumedItem.Quantity)
				throw new InvalidOperationException("Craft hook input lacks an exact Terraria Item payload.");
			consumed.Add(item.Clone());
		}

		RecipeLoader.OnCraft(output, state.Recipe, consumed, new Item());
		if (output.type != state.ExpectedOutputType || output.stack != state.ExpectedOutputStack)
			throw new NotSupportedException(
				"An OnCraft hook changed output identity or quantity; dynamic output mutation is outside the current M5.2 transaction boundary.");
	}

	public CraftTransactionItemStack Resize(CraftTransactionItemStack item, long quantity)
	{
		ArgumentNullException.ThrowIfNull(item);
		if (quantity <= 0 || quantity > int.MaxValue)
			throw new ArgumentOutOfRangeException(nameof(quantity));
		if (item.RuntimePayload is not Item payload || payload.IsAir || payload.type != item.ItemType)
			throw new InvalidOperationException("Terraria runtime stack cannot be resized without an exact Item payload.");
		Item clone = payload.Clone();
		clone.stack = checked((int)quantity);
		return item with { Quantity = quantity, RuntimePayload = clone };
	}

	private static Recipe ResolveRecipe(ExecutionStepPlan step)
	{
		int recipeIndex = step.Recipe.RecipeIndex;
		if (recipeIndex < 0 || recipeIndex >= Main.recipe.Length)
			throw new InvalidOperationException($"Recipe #{recipeIndex} is unavailable in Main.recipe.");
		Recipe recipe = Main.recipe[recipeIndex];
		if (recipe is null || recipe.Disabled || recipe.RecipeIndex != recipeIndex ||
			recipe.createItem.type != step.Recipe.Result.ItemType ||
			recipe.createItem.stack != step.Recipe.OutputStack ||
			recipe.requiredItem.Count != step.Recipe.Ingredients.Count) {
			throw new InvalidOperationException($"Recipe #{recipeIndex} identity changed after graph materialization.");
		}
		for (int ordinal = 0; ordinal < recipe.requiredItem.Count; ordinal++) {
			if (recipe.requiredItem[ordinal].type != step.Recipe.Ingredients[ordinal].Source.ItemType ||
				recipe.requiredItem[ordinal].stack != step.Recipe.Ingredients[ordinal].RequiredAmount)
				throw new InvalidOperationException($"Recipe #{recipeIndex} ingredient #{ordinal} changed after planning.");
		}
		return recipe;
	}

	private static void EnsureSupportedVanillaSemantics(Recipe recipe)
	{
		if (recipe.Mod is not null || recipe.createItem.ModItem is not null ||
			recipe.requiredItem.Any(item => item.ModItem is not null)) {
			throw new NotSupportedException(
				"M5.2 automatic execution currently accepts vanilla recipes/items only; mod recipe execution is deferred to a compatibility adapter.");
		}
	}

	private sealed record VanillaCraftSemanticsState(
		Recipe Recipe,
		int ExpectedOutputType,
		int ExpectedOutputStack);
}

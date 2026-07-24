using System.Collections.Generic;
using Terraria;

namespace MagicStorage.Common.Systems.RecurrentRecipes {
	public sealed class Node {
		private readonly object dependencyLock = new();

		public readonly int poolIndex;

		public readonly RecipeInfo info;

		private volatile bool dependenciesInitialized;

		internal bool DependenciesInitialized => dependenciesInitialized;

		internal int[] DependencyRecipeIndexes { get; private set; } = [];

		internal Node(Recipe recipe, int index) {
			poolIndex = index;

			info = new RecipeInfo(recipe);
		}

		internal void InitializeDependencyRecipeIndexes(HashSet<int> indexes) {
			if (DependenciesInitialized)
				return;

			lock (dependencyLock) {
				if (DependenciesInitialized)
					return;

				DependencyRecipeIndexes = [.. indexes];
				dependenciesInitialized = true;
			}
		}

		internal void ClearTrees() {
			info.ClearTrees();
			DependencyRecipeIndexes = [];
			dependenciesInitialized = false;
		}
	}
}

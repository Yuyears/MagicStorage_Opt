using System;
using System.Collections.Generic;
using Terraria.ModLoader;
using Terraria;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MagicStorage.Common.Systems.RecurrentRecipes {
	public static class NodePool {
		private class Loadable : ILoadable {
			public void Load(Mod mod) { }

			public void Unload() => ClearNodes();
		}

		private static readonly ConcurrentDictionary<int, Node> pool = new();
		private static readonly ConcurrentDictionary<int, List<Node>> resultToNodes = new();
		private static int nextPoolIndex;

		internal static Node Get(int index) => pool[index];

		public static Node FindOrCreate(Recipe recipe) {
			if (recipe.Disabled)
				return null;

			int type = recipe.createItem.type;

			var list = resultToNodes.GetOrAdd(type, static _ => new());

			lock (list) {
				Node node = list.FirstOrDefault(n => Utility.RecipesMatchForHistory(recipe, n.info.sourceRecipe));

				if (node is null) {
					int index = ReservePoolIndex();
					node = new Node(recipe, index);
					if (!pool.TryAdd(index, node))
						throw new InvalidOperationException($"Recursive recipe node identity collision at index {index}");

					list.Add(node);
				}

				return node;
			}
		}

		internal static void ClearNodes() {
			foreach (var node in pool.Values)
				node.ClearTrees();

			pool.Clear();
			resultToNodes.Clear();
			Interlocked.Exchange(ref nextPoolIndex, 0);
		}

		private static int ReservePoolIndex() => Interlocked.Increment(ref nextPoolIndex) - 1;

		internal static bool VerifyParallelIdentityAllocation() {
			const int count = 256;
			int[] indexes = new int[count];
			Parallel.For(0, count, i => indexes[i] = ReservePoolIndex());
			return indexes.Distinct().Count() == count;
		}
	}
}

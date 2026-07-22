using System.Text;
using Terraria;
using Terraria.ID;

namespace MagicStorage {
	partial class Utility {
<<<<<<< HEAD
		/// <summary>
		/// Gets an item's content identifier with stack count when greater than one.
		/// </summary>
		/// <param name="item">The item to describe.</param>
		/// <returns>The item identifier with optional stack count.</returns>
		public static string IdentifierAndStack(this Item item) => $"{ItemID.Search.GetName(item.type)}{(item.stack > 1 ? $" ({item.stack})" : "")}";

		/// <summary>
		/// Gets an item's content identifier with the specified stack count.
		/// </summary>
		/// <param name="item">The item whose type should be described.</param>
		/// <param name="stack">The stack count to display.</param>
		/// <returns>The item identifier with optional stack count.</returns>
		public static string IdentifierWithStack(this Item item, int stack) => $"{ItemID.Search.GetName(item.type)}{(stack > 1 ? $" ({stack})" : "")}";

		/// <summary>
		/// Gets an item type identifier with the specified stack count.
		/// </summary>
		/// <param name="type">The item type to describe.</param>
		/// <param name="stack">The stack count to display.</param>
		/// <returns>The item identifier with optional stack count.</returns>
		public static string ItemIdentifierWithStack(int type, int stack) => $"{ItemID.Search.GetName(type)}{(stack > 1 ? $" ({stack})" : "")}";
=======
		public static string IdentifierAndStack(this Item @this) => BuildItemIdentifier(@this.type, @this.stack);

		public static string IdentifierWithStack(this Item @this, int stack) => BuildItemIdentifier(@this.type, stack);

		public static string ItemIdentifierWithStack(int type, int stack) => BuildItemIdentifier(type, stack);

		private static string BuildItemIdentifier(int type, int stack) {
			if (type <= ItemID.None || stack <= 0)
				return "None";

			StringBuilder sb = new(ItemID.Search.GetName(type));

			if (stack > 1)
				sb.Append(" (").Append(stack).Append(')');

			return sb.ToString();
		}

		public static string PrefixedIdentifierAndStack(this Item @this) => BuildItemIdentifier(@this.type, @this.stack, @this.prefix);

		public static string ItemIdentifierWithPrefix(this Item @this, int prefix) => BuildItemIdentifier(@this.type, @this.stack, prefix);

		public static string PrefixedIdentifierWithStack(this Item @this, int stack) => BuildItemIdentifier(@this.type, stack, @this.prefix);

		public static string PrefixedItemIdentifierWithStack(int type, int stack, int prefix) => BuildItemIdentifier(type, stack, prefix);

		private static string BuildItemIdentifier(int type, int stack, int prefix) {
			if (type <= ItemID.None || stack <= 0)
				return "None";

			StringBuilder sb = new();

			if (prefix > 0)
				sb.Append('{').Append(PrefixID.Search.GetName(prefix)).Append("} ");

			sb.Append(ItemID.Search.GetName(type));

			if (stack > 1)
				sb.Append(" (").Append(stack).Append(')');

			return sb.ToString();
		}

		public static string ToChatTag(this Item @this) => GetItemChatTag(@this.type, @this.stack, @this.prefix);

		public static string GetItemChatTag(int type, int stack, int prefix) {
			if (type <= ItemID.None || stack <= 0)
				return "None";

			// NOTE: This method does not handle the "d" (mod data) tag option since it's supposed to be just an easy tag builder

			if (stack > 1) {
				if (prefix > 0)
					return $"[i/s{stack},p{prefix}:{type}]";
				else
					return $"[i/s{stack}:{type}]";
			} else {
				if (prefix > 0)
					return $"[i/p{prefix}:{type}]";
				else
					return $"[i:{type}]";
			}
		}
>>>>>>> d4de99cf9bb3e47aa41040c8a6b33c7fdec913a4
	}
}

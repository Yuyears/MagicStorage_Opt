using System.Collections;
using Terraria.ModLoader.IO;

namespace MagicStorage {
	internal static class TagCompoundComparer {
		public static bool SemanticallyEquals(TagCompound left, TagCompound right) {
			if (ReferenceEquals(left, right))
				return true;
			if (left is null || right is null || left.Count != right.Count)
				return false;

			foreach ((string key, object leftValue) in left) {
				if (!right.ContainsKey(key) || !ValueEquals(leftValue, right[key]))
					return false;
			}

			return true;
		}

		internal static string DescribeFirstDifference(TagCompound left, TagCompound right) => DescribeTagDifference(left, right, "$" ) ?? "none";

		private static bool ValueEquals(object left, object right) {
			if (ReferenceEquals(left, right))
				return true;
			if (left is null || right is null)
				return false;
			if (left is TagCompound leftTag && right is TagCompound rightTag)
				return SemanticallyEquals(leftTag, rightTag);
			if (left is IList leftList && right is IList rightList)
				return ListsEqual(leftList, rightList);

			return left.GetType() == right.GetType() && left.Equals(right);
		}

		private static bool ListsEqual(IList left, IList right) {
			if (left.Count != right.Count)
				return false;

			for (int i = 0; i < left.Count; i++) {
				if (!ValueEquals(left[i], right[i]))
					return false;
			}

			return true;
		}

		private static string DescribeTagDifference(TagCompound left, TagCompound right, string path) {
			if (left.Count != right.Count)
				return $"{path}: key count {left.Count} != {right.Count}";

			foreach ((string key, object leftValue) in left) {
				string childPath = $"{path}.{key}";
				if (!right.ContainsKey(key))
					return $"{childPath}: missing on candidate";

				object rightValue = right[key];
				if (ValueEquals(leftValue, rightValue))
					continue;
				if (leftValue is TagCompound leftTag && rightValue is TagCompound rightTag)
					return DescribeTagDifference(leftTag, rightTag, childPath);
				if (leftValue is IList leftList && rightValue is IList rightList)
					return DescribeListDifference(leftList, rightList, childPath);

				return $"{childPath}: {DescribeValue(leftValue)} != {DescribeValue(rightValue)}";
			}

			return null;
		}

		private static string DescribeListDifference(IList left, IList right, string path) {
			if (left.Count != right.Count)
				return $"{path}: list count {left.Count} != {right.Count}";

			for (int i = 0; i < left.Count; i++) {
				if (!ValueEquals(left[i], right[i]))
					return $"{path}[{i}]: {DescribeValue(left[i])} != {DescribeValue(right[i])}";
			}

			return null;
		}

		private static string DescribeValue(object value) => value is null ? "null" : $"{value.GetType().Name}({value})";
	}
}

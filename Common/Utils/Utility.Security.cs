using MagicStorage.Common.Systems;

namespace MagicStorage {
	partial class Utility {
		/// <summary>
		/// Checks whether a network action result represents a successful operation.
		/// </summary>
		/// <param name="result">The result to inspect.</param>
		/// <returns><see langword="true" /> if the result is successful; otherwise, <see langword="false" />.</returns>
		public static bool IsSuccess(this NetworkActionResult result) => result is NetworkActionResult.Success or NetworkActionResult.OperatorForcedSuccess;

	}
}

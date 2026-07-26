using MagicStorage.Common.Systems;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SerousCommonLib.UI;
using System;
using System.Diagnostics;
using Terraria.Localization;
using Terraria.UI;

namespace MagicStorage.UI.Input {
	public class NewUISearchBar : TextInputBar {
		private static readonly long RefreshDelayTicks = Stopwatch.Frequency * 180 / 1000;
		private long _refreshAt;

		public Func<string> GetHoverText { get; set; }

		internal bool BlockRefreshThreads { get; set; }

		public NewUISearchBar(LocalizedText hintText) : base(hintText) { }

		protected override bool PreDrawText(SpriteBatch spriteBatch, ref Color textColor, ref Color hintColor) {
			if (State.HasText && MagicUI.lastKnownSearchBarErrorReason is not null && !MagicUI.CurrentlyRefreshing)
				textColor = Color.Red;

			return true;
		}

		public override void OnActivityLost() {
			MagicUI.mouseText = "";
			base.OnActivityLost();
		}

		public override void OnInputChanged() {
			if (MagicStorageConfig.SearchBarRefreshOnKey && !BlockRefreshThreads)
				_refreshAt = Stopwatch.GetTimestamp() + RefreshDelayTicks;
			else
				_refreshAt = 0;

			base.OnInputChanged();
		}

		public override void OnInputCleared() {
			_refreshAt = 0;

			if (!BlockRefreshThreads)
				MagicUI.StartMainZoneRefreshThread(caller: "NewUISearchBar.OnInputCleared()", forceMainZoneRebuild: true);

			base.OnInputCleared();
		}

		public override void OnInputFocusLost() {
			_refreshAt = 0;

			if (!BlockRefreshThreads)
				MagicUI.StartMainZoneRefreshThread(caller: "NewUISearchBar.OnInputFocusLost()", forceMainZoneRebuild: true);

			base.OnInputFocusLost();
		}

		public override void MouseOut(UIMouseEvent evt) {
			base.MouseOut(evt);
			MagicUI.mouseText = "";
		}

		protected override void RestrictedUpdate(GameTime gameTime) {
			if (_refreshAt != 0 && Stopwatch.GetTimestamp() >= _refreshAt) {
				_refreshAt = 0;
				if (MagicStorageConfig.SearchBarRefreshOnKey && !BlockRefreshThreads)
					MagicUI.StartMainZoneRefreshThread(caller: "NewUISearchBar.RestrictedUpdate()", forceMainZoneRebuild: true);
			}

			if (State.IsActive) {
				// Update the hover text if any is present
				if (IsMouseHovering && GetHoverText?.Invoke() is string hoverText) {
					if (MagicUI.lastKnownSearchBarErrorReason is string errorText && !MagicUI.CurrentlyRefreshing)
						Utility.AddErrorTextMultiline(ref hoverText, errorText);

					if (!string.IsNullOrWhiteSpace(hoverText))
						MagicUI.mouseText = hoverText;
					else
						MagicUI.mouseText = "";
				}
			}
		}
	}
}

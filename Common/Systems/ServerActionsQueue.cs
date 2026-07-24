using System;
using System.Collections.Concurrent;
using System.Threading;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MagicStorage.Common.Systems {
	// Main.ConsumeAllMainThreadActions() doesn't run on servers unless clients are connected, for whatever reason
	internal class ServerActionsQueue : ModSystem {
		private static readonly ConcurrentQueue<Action> _actions = new();
		private static readonly object _worldLock = new();
		private static CancellationTokenSource _worldCancellation = new();

		public static CancellationToken WorldCancellationToken {
			get {
				lock (_worldLock)
					return _worldCancellation.Token;
			}
		}

		public override void Load() {
			if (Main.netMode == NetmodeID.Server)
				Main.OnTickForThirdPartySoftwareOnly += ConsumeActions;  // Fugly hack to allow actions to always get consumed on the server
		}

		public override void OnWorldUnload() {
			if (Main.netMode != NetmodeID.Server)
				return;

			lock (_worldLock)
				_worldCancellation.Cancel();

			// Ensure that all actions are consumed before unloading the world
			ConsumeActions();
		}

		public override void OnWorldLoad() {
			if (Main.netMode != NetmodeID.Server)
				return;

			lock (_worldLock) {
				if (_worldCancellation.IsCancellationRequested)
					_worldCancellation = new CancellationTokenSource();
			}
		}

		public static void QueueActionBasedOnClientPresence(Action action) {
			if (Main.netMode != NetmodeID.Server)
				Main.QueueMainThreadAction(action);
			else
				QueueAction(action);
		}

		public static void QueueAction(Action action) {
			ArgumentNullException.ThrowIfNull(action);

			_actions.Enqueue(action);
		}

		public static bool QueueActionForWorld(Action action, CancellationToken worldToken) {
			ArgumentNullException.ThrowIfNull(action);

			lock (_worldLock) {
				if (_worldCancellation.IsCancellationRequested || worldToken != _worldCancellation.Token)
					return false;

				_actions.Enqueue(action);
				return true;
			}
		}

		private static void ConsumeActions() {
			while (_actions.TryDequeue(out var action))
				action();
		}
	}
}

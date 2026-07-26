using System;
using System.Collections.Generic;
using System.IO;
using MagicStorage.Components;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using System.Text;
using System.Linq;
using Terraria.Audio;
using MagicStorage.Common.Systems;
using MagicStorage.Common.Players;
using MagicStorage.UI;
using System.Threading;
using ReLogic.Content;
using MagicStorage.Common.Systems.Shimmering;
using MagicStorage.UI.Selling;
using Terraria.Localization;
using MagicStorage.Items;
using MagicStorage.Common.Systems.Auditing;
using MagicStorage.NPCs;
using MagicStorage.Common;
using MagicStorage.CrossMod.Storage;

namespace MagicStorage
{
	public static class NetHelper
	{
		private static bool queueUpdates;
		private static readonly Queue<int> updateQueue = new();
		private static readonly HashSet<int> updateQueueContains = new();
		private static int cachedRecipeTableCount = -1;
		private static ulong cachedRecipeTableDigest;

		[Conditional("NETPLAY")]
		public static void Report(bool reportTime, string message) {
			if (!AssetRepository.IsMainThread) {
				// Local capturing
				bool report = reportTime;
				string msg = message;
				DateTime now = DateTime.Now;

				ServerActionsQueue.QueueActionBasedOnClientPresence(() => Report_Inner(report, msg, now));
			} else
				Report_Inner(reportTime, message, DateTime.Now);
		}

		[Conditional("NETPLAY")]
		private static void Report_Inner(bool reportTime, string message, DateTime now) {
			if (!MagicStorageBetaConfig.PrintTextToChat)
				return;

			StringBuilder sb = new();

			if (reportTime)
				sb.Append("Time: " + now.Ticks + " ");

			sb.Append(message);

			if (Main.netMode != NetmodeID.Server) {
				Main.NewTextMultiline(sb.ToString(), c: Color.White);
			} else if (Main.dedServ) {
				if (reportTime)
					Utility.PrettyWriteLineToConsole("Time: " + now.Ticks, ConsoleColor.Red, ConsoleColor.Black);

				Utility.WriteLineSafely(message);
			}

			MagicStorageMod.Instance.Logger.Debug(sb.ToString());
		}

		public static void HandlePacket(BinaryReader reader, int sender) => TryHandlePacket(reader, sender, logMalformed: true);

		internal static bool TryHandlePacket(BinaryReader reader, int sender, bool logMalformed = false) {
			try {
				HandlePacketCore(reader, sender);
				return true;
			} catch (Exception exception) when (exception is EndOfStreamException or InvalidDataException or FormatException) {
				if (logMalformed)
					MagicStorageMod.Instance.Logger.Warn($"Rejected malformed packet from sender {sender}: {exception.Message}");
				return false;
			}
		}

		private static void HandlePacketCore(BinaryReader reader, int sender)
		{
			MessageType type = (MessageType)reader.ReadByte();
			if (!InboundPacketGuard.Accept(type, sender)) {
				reader.BaseStream.Position = reader.BaseStream.Length;
				return;
			}

			/*
			if (Main.netMode == NetmodeID.MultiplayerClient)
				Main.NewText($"Receiving Message Type \"{Enum.GetName(type)}\"");
			else if(Main.netMode == NetmodeID.Server)
				Console.WriteLine($"Receiving Message Type \"{Enum.GetName(type)}\"");
			*/

			Report(true, "Received message " + type + " from player " + sender);

			switch (type) {
				case MessageType.SearchAndRefreshNetwork:
					ReceiveSearchAndRefresh(reader);
					break;
				case MessageType.ClientStorageOperation:
					ReceiveClientStorageOperation(reader, sender);
					break;
				case MessageType.ServerStorageResult:
					ReceiveServerStorageResult(reader);
					break;
				case MessageType.RefreshNetworkItems:
					ReceiveRefreshNetworkItems(reader);
					break;
				case MessageType.ClientStorageComponentOperation:
					ReceiveStorageComponentOperation(reader, sender);
					break;
				case MessageType.ClientSendDeactivate:
					ReceiveClientDeactivate(reader, sender);
					break;
				case MessageType.ClientStationOperation:
					ReceiveClientStationOperation(reader, sender);
					break;
				case MessageType.ServerStationOperationResult:
					ReceiveServerStationResult(reader);
					break;
				case MessageType.ResetCompactStage:
					ReceiveResetCompactStage(reader, sender);
					break;
				case MessageType.CraftRequest:
					ReceiveCraftRequest(reader, sender);
					break;
				case MessageType.CraftResult:
					ReceiveCraftResult(reader);
					break;
				case MessageType.CraftOutcome:
					ReceiveCraftOutcome(reader);
					break;
				case MessageType.SectionRequest:
					ReceiveClientRequestSection(reader, sender);
					break;
				case MessageType.SyncStorageUnitToClient:
					ClientReceiveStorageSync(reader);
					break;
				case MessageType.SyncStorageUnit:
					ServerReceiveSyncStorageUnit(reader, sender);
					break;
				case MessageType.ForceCraftingGUIRefresh:
					ReceiveClientForceCraftingGUIRefresh(reader, sender);
					break;
				case MessageType.TransferItems:
					ReceiveClientRequestItemTransfer(reader, sender);
					break;
				case MessageType.RequestCoinCompact:
					ReceiveCoinCompactRequest(reader, sender);
					break;
				case MessageType.MassDuplicateSellRequest:
					ReceiveDuplicateSellingRequest(reader, sender);
					break;
				case MessageType.MassDuplicateSellResult:
					ClientReceiveDuplicateSellingResult(reader);
					break;
				case MessageType.RequestStorageUnitStyle:
					ReceiveStorageUnitStyle(reader, sender);
					break;
				case MessageType.ServerQuickStackToStorageResult:
					ClientReceiveQuickStackToNearbyStorageResult(reader);
					break;
				case MessageType.GolemHelpTextUpdate:
					ClientReceiveGolemTextUpdate(reader);
					break;
				case MessageType.ClientRequestServerOp:
					ServerReceiveOperatorRequest(sender);
					break;
				case MessageType.ServerOpResponse:
					ClientReceiveOperatorReponse();
					break;
				case MessageType.ClientRequestServerOpConfirmation:
					ServerReceiveOperatorKeyFromClient(reader, sender);
					break;
				case MessageType.ServerOpConfirmationResult:
					ClientReceiveOperatorConformationResult(reader);
					break;
				case MessageType.PlayerHasServerOp:
					ReceivePlayerHasOperator(reader);
					break;
				case MessageType.ClientRequestPlayerOperatorChange:
					ServerReceivePlayerOperatorChange(reader, sender);
					break;
				case MessageType.ClientRequestPlayerBankDeposit:
					ServerReceiveDepositFromBankRequest(reader, sender);
					break;
				case MessageType.PlayerBankDepositResult:
					ClientReceiveDepositFromBankResult(reader);
					break;
				case MessageType.ComponentPlacement:
					ServerReceiveComponentPlacement(reader, sender);
					break;
				case MessageType.ComponentDestruction:
					ServerReceiveComponentDestruction(reader, sender);
					break;
				case MessageType.ClientLockStorageHeart:
				case MessageType.ClientUnlockStorageHeart:
					ReceiveStorageHeartUsage(reader, sender, type == MessageType.ClientLockStorageHeart);
					break;
				case MessageType.DeleteSpecificItem:
					ServerReceiveExactItemDeletionRequest(reader, sender);
					break;
				case MessageType.RequestShimmerItemInStorage:
					ServerReceiveItemShimmeringRequest(reader, sender);
					break;
				case MessageType.ShimmerItemInStorageResult:
					ClientReceiveItemShimmeringResult(reader);
					break;
				case MessageType.RenameStorageHeart:
					ReceiveStorageHeartName(reader, sender);
					break;
				case MessageType.SyncDepositHistory:
					Obsolete_ReceiveStorageDepositHistory(reader, sender);
					break;
				case MessageType.ClientSendCoreRemoval:
					ReceiveCoreRemoval(reader, sender);
					break;
				case MessageType.ClientSendCoreInsertion:
					ReceiveCoreInsertion(reader, sender);
					break;
				case MessageType.SecurityNetworkCreation:
					ReceiveSecurityNetworkCreation(reader, sender);
					break;
				case MessageType.SecurityNetworkRemoval:
					ReceiveSecurityNetworkRemoval(reader, sender);
					break;
				case MessageType.SecurityNetworkJoin:
					ReceiveSecurityNetworkJoinAttempt(reader, sender);
					break;
				case MessageType.SecurityNetworkAccessible:
					ReceiveSecurityNetworkAccessAttempt(reader, sender);
					break;
				case MessageType.SecurityNetworkModification:
					ReceiveSecurityNetworkChange(reader, sender);
					break;
				case MessageType.RequestSecurityNetworkList:
					ReceiveSecurityNetworkList(reader, sender);
					break;
				case MessageType.SecurityPlayerSync:
					ReceiveSecurityPlayerSync(reader, sender);
					break;
				case MessageType.RequestSecurityPlayerSync:
					ServerReceiveSecurityPlayerSyncRequest(sender);
					break;
				case MessageType.StorageHeartNetwork:
					ReceiveStorageComponentNetwork(reader, sender);
					break;
				case MessageType.StorageHeartNetworkAssignment:
					ReceiveStorageHeartNetworkAssignmentRequest(reader, sender);
					break;
				case MessageType.DefaultAccessibleNetworks:
					RecieveAccessibleNetworksByDefaultRequest(reader, sender);
					break;
				case MessageType.SecurityNetworkPassword:
					ReceiveNetworkPasswordRequest(reader, sender);
					break;
				case MessageType.AuditSystemMessage:
					AuditSystem.HandlePacket(reader, sender);
					break;
				case MessageType.SyncPityDropsPlayer:
					ReceivePityDropsPlayerSync(reader, sender);
					break;
				case MessageType.ClientRequestDepositHistoryChunks:
					ServerReceiveDepositHistoryChunksRequest(reader, sender);
					break;
				case MessageType.ServerResponseDepositHistoryChunks:
					ClientReceiveDepositHistoryChunk(reader);
					break;
				case MessageType.UpdateDepositHistory:
					ClientReceiveDepositHistoryUpdate(reader);
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(type));
			}
		}

		public static void SyncStorageUnit(Point16 position)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient)
			{
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.SyncStorageUnit);
				packet.Write(position.X);
				packet.Write(position.Y);
				packet.Send();

				Report(true, MessageType.SyncStorageUnit + " packet sent from client " + Main.myPlayer);
			}
		}

		public static void ServerReceiveSyncStorageUnit(BinaryReader reader, int remoteClient)
		{
			if (Main.netMode == NetmodeID.Server)
			{
				//byte remoteClient = reader.ReadByte();
				Point16 position = new(reader.ReadInt16(), reader.ReadInt16());

				if (!InboundPacketGuard.TryGetStorageEntity(position, remoteClient, out TEStorageUnit storageUnit, out _)) {
					Report(true, MessageType.SyncStorageUnit + " rejected an invalid or inaccessible Storage Unit at (X: " + position.X + ", Y: " + position.Y + ")");
					return;
				}

				storageUnit.FullySync();

				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.SyncStorageUnitToClient);
				TileEntity.Write(packet, storageUnit, true);
				packet.Send(remoteClient);

				Report(true, MessageType.SyncStorageUnit + " packet received by server from client " + remoteClient);
			}
		}

		[Obsolete("Use ServerReceiveSyncStorageUnit instead")]
		public static void ServerReciveSyncStorageUnit(BinaryReader reader, int remoteClient) => ServerReceiveSyncStorageUnit(reader, remoteClient);

		public static void SendComponentPlace(int i, int j, int type)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient)
			{
				NetMessage.SendTileSquare(Main.myPlayer, i, j, 2, 2);
				NetMessage.SendData(MessageID.TileEntityPlacement, -1, -1, null, i, j, type);
			}
		}

		public static void StartUpdateQueue()
		{
			queueUpdates = true;
		}

		public static void SendTEUpdate(int id, Point16 position)
		{
			if (Main.netMode != NetmodeID.Server)
				return;

			if (queueUpdates)
			{
				if (!updateQueueContains.Contains(id))
				{
					updateQueue.Enqueue(id);
					updateQueueContains.Add(id);
				}
			}
			else
			{
				NetMessage.SendData(MessageID.TileEntitySharing, -1, -1, null, id, position.X, position.Y);
			}
		}

		public static void ProcessUpdateQueue()
		{
			if (queueUpdates)
			{
				if (updateQueue.Count > 0)
					Report(true, "Tile Entity update queue had " + updateQueue.Count + " values");

				queueUpdates = false;
				while (updateQueue.Count > 0)
					NetMessage.SendData(MessageID.TileEntitySharing, -1, -1, null, updateQueue.Dequeue());
				updateQueueContains.Clear();
			}
		}

		public static void SendSearchAndRefresh(int i, int j)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient)
			{
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.SearchAndRefreshNetwork);
				packet.Write((short)i);
				packet.Write((short)j);
				packet.Send();

				Report(true, MessageType.SearchAndRefreshNetwork + " packet sent from client " + Main.myPlayer);
				Report(false, "Refresh origin: (" + i + ", " + j + ")");
			}
		}

		private static void ReceiveSearchAndRefresh(BinaryReader reader)
		{
			Point16 point = new(reader.ReadInt16(), reader.ReadInt16());
			TEStorageComponent.SearchAndRefreshNetwork(point);

			Report(true, MessageType.SearchAndRefreshNetwork + " packet received by client " + Main.myPlayer);
			Report(false, "Refresh origin: (" + point.X + ", " + point.Y + ")");
		}

		public static void ReceiveClientStorageOperation(BinaryReader reader, int sender)
		{
			Point16 position = new(reader.ReadInt16(), reader.ReadInt16());
			TEStorageHeart.Operation op = (TEStorageHeart.Operation)reader.ReadByte();
			using var context = SecuritySystem.CreateAccessContext(sender);

			if (!TileEntity.ByPosition.TryGetValue(position, out TileEntity te) || te is not TEStorageHeart heart)
				return;

			// NOTE: If not the server, the data will be read but not enqueued
			heart.QClientOperation(reader, op, sender);

			Report(true, MessageType.ClientStorageOperation + " packet received by client " + Main.myPlayer);
			Report(false, "Operation: " + op);
		}

		[Obsolete("Use ReceiveClientStorageOperation instead")]
		public static void ReciveClientStorageOperation(BinaryReader reader, int sender) => ReceiveClientStorageOperation(reader, sender);

		public static void ReceiveServerStorageResult(BinaryReader reader) {
			TEStorageHeart.Operation op = (TEStorageHeart.Operation)reader.ReadByte();
			Point16 position = reader.ReadPoint16();
			long operationId = reader.ReadInt64();
			long revision = reader.ReadInt64();

			if (!TileEntity.ByPosition.TryGetValue(position, out TileEntity te) || te is not TEStorageHeart heart)
				goto printReport;

			bool acceptResult = heart.AcceptNetworkRevision(revision);
			if (op == TEStorageHeart.Operation.Withdraw || op == TEStorageHeart.Operation.WithdrawToInventory || op == TEStorageHeart.Operation.Deposit) {
				Item item = ItemIO.Receive(reader, true, true);
				if (Main.netMode == NetmodeID.MultiplayerClient && acceptResult)
					StoragePlayer.GetItem(new EntitySource_TileEntity(heart), item, op != TEStorageHeart.Operation.WithdrawToInventory);
			} else if (op == TEStorageHeart.Operation.DepositAll) {
				int count = reader.ReadInt32();
				for (int k = 0; k < count; k++) {
					Item item = ItemIO.Receive(reader, true, true);
					if (Main.netMode == NetmodeID.MultiplayerClient && acceptResult)
						StoragePlayer.GetItem(new EntitySource_TileEntity(heart), item, false);
				}
			} else if (op == TEStorageHeart.Operation.WithdrawAllAndDestroy) {
				int type = reader.ReadInt32();
				if (Main.netMode == NetmodeID.MultiplayerClient && acceptResult)
					heart.WithdrawManyAndDestroy(type, out _, net: true);
			} else if (op == TEStorageHeart.Operation.DeleteUnloadedGlobalItemData) {
				if (Main.netMode == NetmodeID.MultiplayerClient && acceptResult)
					heart.DestroyUnloadedGlobalItemData(out _, net: true);
			} else if (op == TEStorageHeart.Operation.WithdrawThenTryModuleInventory || op == TEStorageHeart.Operation.WithdrawToInventoryThenTryModuleInventory) {
				Item item = ItemIO.Receive(reader, true, true);
				Item requested = ItemIO.Receive(reader, true, true);
				if (acceptResult && item.IsAir)
					item = CraftingGUI.TryToWithdrawFromModuleItems(heart, requested, wasAlreadyCloned: true);
				if (Main.netMode == NetmodeID.MultiplayerClient && acceptResult)
					StoragePlayer.GetItem(new EntitySource_TileEntity(heart), item, op != TEStorageHeart.Operation.WithdrawToInventoryThenTryModuleInventory);
			}

			heart.CompleteClientOperation(operationId);

		printReport:
			Report(true, MessageType.ServerStorageResult + " packet received by client " + Main.myPlayer);
			Report(false, "Operation: " + op);
		}

		[Obsolete("Use ReceiveServerStorageResult instead")]
		public static void ReciveServerStorageResult(BinaryReader reader) => ReceiveServerStorageResult(reader);

		public static void SendRefreshNetworkItems(Point16 position, bool ignoreSpecificRefreshes = false, IEnumerable<int> typesToRefresh = null)
		{
			if (Main.netMode == NetmodeID.Server)
			{
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.RefreshNetworkItems);
				packet.Write(position.X);
				packet.Write(position.Y);
				packet.Write(position.ResolveToTileEntity() is TEStorageHeart heart ? heart.NetworkRevision : 0L);

				if (typesToRefresh is null || !typesToRefresh.Any())
					packet.Write((ushort)0);
				else {
					List<int> types = typesToRefresh.ToList();
					packet.Write((ushort)types.Count);

					foreach (int id in types)
						packet.Write(id);
				}

				packet.Write(ignoreSpecificRefreshes);

				packet.Send();

				Report(true, MessageType.RefreshNetworkItems + " packet sent from client " + Main.myPlayer);
			}
		}

		private static void ReceiveRefreshNetworkItems(BinaryReader reader)
		{
			Point16 position = new(reader.ReadInt16(), reader.ReadInt16());
			long revision = reader.ReadInt64();
			int count = reader.ReadUInt16();

			List<int> types = new();
			for (int i = 0; i < count; i++)
				types.Add(reader.ReadInt32());

			bool ignoreSpecificRefreshes = reader.ReadBoolean();

			if (Main.netMode == NetmodeID.Server)
				return;

			if (position.ResolveToTileEntity() is TEStorageHeart heart && StoragePlayer.IsClientViewingHeart(heart)) {
				bool accepted = heart.AcceptNetworkRevision(revision);
				MagicStorageMod.Instance.Logger.Info($"Storage refresh notification: heart={position}, incomingRevision={revision}, accepted={accepted}, types={types.Count}, ignoreSpecific={ignoreSpecificRefreshes}");
				if (!accepted)
					goto printReport;

				MagicUI.IgnoreSpecificZoneRefreshing = ignoreSpecificRefreshes;
				MagicUI.SetNextCollectionsToRefresh(types);
				bool fullRefresh = MagicUI.IsDecraftingUIOpen() && (ignoreSpecificRefreshes || types.Contains(DecraftingGUI.selectedItem) || types.Any(DecraftingGUI.IsItemValidForResult));
				MagicStorageMod.Instance.Logger.Info($"Storage refresh notification accepted: heart={position}, mode={(fullRefresh ? "full" : "partial")}");
				if (fullRefresh)
					MagicUI.RequestFullRefresh();
				else
					MagicUI.RequestMainZoneThread();
			}

		printReport:
			Report(true, MessageType.RefreshNetworkItems + " packet received by client " + Main.myPlayer);
		}

		public static void ClientSendDeactivate(Point16 position, bool inActive)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient)
			{
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.ClientSendDeactivate);
				packet.Write(position.X);
				packet.Write(position.Y);
				packet.Write(inActive);
				packet.Send();

				Report(true, MessageType.ClientSendDeactivate + " packet sent from client " + Main.myPlayer);
			}
		}

		public static void ReceiveClientDeactivate(BinaryReader reader, int sender)
		{
			Point16 position = new(reader.ReadInt16(), reader.ReadInt16());
			bool inActive = reader.ReadBoolean();

			if (Main.netMode == NetmodeID.Server)
			{
				Player player = Main.player[sender];
				if (player.HeldItem.type == ModContent.ItemType<StorageDeactivator>()
				&& InboundPacketGuard.TryGetStorageEntity(position, sender, out TEStorageUnit storageUnit, out TEStorageHeart heart, requireInteractionRange: true)
				&& storageUnit.IsTileValidForEntity(position.X, position.Y)
				&& storageUnit.Inactive != inActive)
				{
					storageUnit.Inactive = inActive;
					heart.NotifyStorageUnitRoutingChanged(storageUnit);
					storageUnit.UpdateTileFrameWithNetSend();
					heart.ResetCompactStage();
					if (inActive)
						AuditSystem.ReportStorageUnitDeactivation(sender, storageUnit);
					else
						AuditSystem.ReportStorageUnitActivation(sender, storageUnit);
				}

				Report(true, MessageType.ClientSendDeactivate + " packet received by server from client " + sender);

			//	PrintClientRequest(sender, $"{(inActive ? "Deactivate" : "Activate")} Unit", position);
			}
			else if (Main.netMode == NetmodeID.MultiplayerClient)
			{
				Report(true, MessageType.ClientSendDeactivate + " packet received by client " + Main.myPlayer);
			}
		}

		private static ModPacket PrepareStorageComponentOperation(StorageComponentOperation operation) {
			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.ClientStorageComponentOperation);
			packet.Write((byte)operation);
			return packet;
		}

		public static void RequestRemoteAccessLink(Point16 remotePosition, Point16 heartPosition) {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			ModPacket packet = PrepareStorageComponentOperation(StorageComponentOperation.RemoteAccessLink);
			packet.Write(remotePosition);
			packet.Write(heartPosition);
			packet.Send();
		}

		public static void SetEnvironmentModuleEnabled(TEEnvironmentAccess access, EnvironmentModule module, bool enabled) {
			if (Main.netMode != NetmodeID.MultiplayerClient) {
				access.SetEnabled(module, enabled);
				return;
			}

			ModPacket packet = PrepareStorageComponentOperation(StorageComponentOperation.EnvironmentModuleToggle);
			packet.Write(access.Position);
			packet.Write(module.Type);
			packet.Write(enabled);
			packet.Send();
		}

		private static void ReceiveStorageComponentOperation(BinaryReader reader, int sender) {
			StorageComponentOperation operation = (StorageComponentOperation)reader.ReadByte();
			if (GetStorageComponentOperationPayloadLength(operation) < 0) {
				Report(true, $"Rejected malformed storage component operation {(byte)operation}");
				return;
			}

			switch (operation) {
				case StorageComponentOperation.RemoteAccessLink:
					ReceiveRemoteAccessLink(reader, sender);
					break;
				case StorageComponentOperation.EnvironmentModuleToggle:
					ReceiveEnvironmentModuleToggle(reader, sender);
					break;
			}
		}

		internal static int GetStorageComponentOperationPayloadLength(StorageComponentOperation operation) => operation switch {
				StorageComponentOperation.RemoteAccessLink => 8,
				StorageComponentOperation.EnvironmentModuleToggle => 9,
				_ => -1
			};

		private static void ReceiveRemoteAccessLink(BinaryReader reader, int sender) {
			Point16 remotePosition = reader.ReadPoint16();
			Point16 heartPosition = reader.ReadPoint16();
			Player player = Main.player[sender];
			Item heldItem = player.HeldItem;

			if (!player.active
			|| remotePosition.ResolveToTileEntity() is not TERemoteAccess remoteAccess
			|| heartPosition.ResolveToTileEntity() is not TEStorageHeart heart
			|| !remoteAccess.IsTileValidForEntity(remotePosition.X, remotePosition.Y)
			|| !heart.IsTileValidForEntity(heartPosition.X, heartPosition.Y)
			|| remoteAccess.StorageCenter != Point16.NegativeOne
			|| !InboundPacketGuard.IsWithinTileRange(player.Center.ToTileCoordinates(), remotePosition, player.lastTileRangeX, player.lastTileRangeY)
			|| !SecuritySystem.CanPlayerAccessImmediately(player, remoteAccess.assignedNetwork)
			|| !SecuritySystem.CanPlayerAccessImmediately(player, heart.assignedNetwork)
			|| heldItem.type != ModContent.ItemType<Locator>() && heldItem.type != ModContent.ItemType<LocatorDisk>()
			|| heldItem.ModItem is not Locator locator
			|| locator.Location != heartPosition
			|| !remoteAccess.TryLocate(heartPosition, out _)) {
				Report(true, $"Rejected remote access link from player {sender}");
				return;
			}

			AuditSystem.ReportRemoteAccessLink(sender, heart, remoteAccess);
		}

		private static void ReceiveEnvironmentModuleToggle(BinaryReader reader, int sender) {
			Point16 position = reader.ReadPoint16();
			int moduleType = reader.ReadInt32();
			bool enabled = reader.ReadBoolean();

			if (!InboundPacketGuard.TryGetStorageEntity(position, sender, out TEEnvironmentAccess access, out _, requireInteractionRange: true)
			|| !access.IsTileValidForEntity(position.X, position.Y)
			|| EnvironmentModuleLoader.Get(moduleType) is not EnvironmentModule module
			|| !module.IsAvailable()) {
				Report(true, $"Rejected environment module toggle from player {sender}");
				return;
			}

			access.SetEnabled(module, enabled);
			SendTEUpdate(access.ID, access.Position);
		}

		public static void SendDepositStation(Point16 position, Item item)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient
			&& TileEntity.ByPosition.TryGetValue(position, out TileEntity entity)
			&& entity is TECraftingAccess access) {
				access.TryDepositStation(item);
				Report(true, "SendDepositStation packet sent from client " + Main.myPlayer);
			}
		}

		public static void SendWithdrawStation(Point16 position, int slot)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient
			&& TileEntity.ByPosition.TryGetValue(position, out TileEntity entity)
			&& entity is TECraftingAccess access) {
				access.TryWithdrawStation(slot);
				Report(true, "SendWithdrawStation packet sent from client " + Main.myPlayer);
			}
		}

		public static void ReceiveClientStationOperation(BinaryReader reader, int sender)
		{
			Point16 position = new(reader.ReadInt16(), reader.ReadInt16());
			TECraftingAccess.Operation op = (TECraftingAccess.Operation)reader.ReadByte();
			long operationId = reader.ReadInt64();
			int slot = reader.ReadByte();

			if (!Enum.IsDefined(op)
			|| !InboundPacketGuard.TryGetStorageEntity(position, sender, out TECraftingAccess craftingAccess, out _, requireInteractionRange: true)
			|| !craftingAccess.IsTileValidForEntity(position.X, position.Y)) {
				if (Enum.IsDefined(op))
					TECraftingAccess.SendServerResult(position, op, operationId, sender, accepted: false, new Item());
				return;
			}

			craftingAccess.QClientOperation(op, operationId, slot, sender);

			Report(true, MessageType.ClientStationOperation + " packet received by server from client " + sender);
			Report(false, "Operation: " + op);
		}

		public static void ReceiveServerStationResult(BinaryReader reader)
		{
			TECraftingAccess.Operation op = (TECraftingAccess.Operation)reader.ReadByte();
			long operationId = reader.ReadInt64();
			bool accepted = reader.ReadBoolean();
			Point16 position = reader.ReadPoint16();
			Item item = ItemIO.Receive(reader, true, true);

			if (op == TECraftingAccess.Operation.Withdraw || op == TECraftingAccess.Operation.WithdrawToInventory)
			{
				var heart = StoragePlayer.LocalPlayer.GetStorageHeart();

				if (accepted && Main.netMode == NetmodeID.MultiplayerClient)
				{
					StoragePlayer.GetItem(new EntitySource_TileEntity(heart), item, op == TECraftingAccess.Operation.Withdraw);
					
					TECraftingAccess.UpdateRecipesFromStationAction(item);
				}
			}
			else if (op == TECraftingAccess.Operation.Deposit)
			{
				if (Main.netMode == NetmodeID.MultiplayerClient)
					TECraftingAccess.ReceiveDepositPreparation(position, operationId, accepted);
			}
			else if (op == TECraftingAccess.Operation.DepositCommit)
			{
				int oldType = reader.ReadUInt16();

				if (Main.netMode == NetmodeID.MultiplayerClient)
				{
					TECraftingAccess.ReceiveDepositCommit(position, operationId, accepted, item);
					if (accepted && oldType > ItemID.None)
						TECraftingAccess.UpdateRecipesFromStationAction(new Item(oldType));
				}
			}

			Report(true, "Station operation " + op + " packet received by client " + Main.myPlayer);
		}

		public static void SendResetCompactStage(Point16 heart)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient)
			{
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.ResetCompactStage);
				packet.Write(heart.X);
				packet.Write(heart.Y);
				packet.Send();

				Report(true, MessageType.ResetCompactStage + " packet sent from client " + Main.myPlayer);
				Report(false, "Entity reset: (X: " + heart.X + ", Y: " + heart.Y + ")");
			}
		}

		public static void ReceiveResetCompactStage(BinaryReader reader, int sender)
		{
			Point16 position = new(reader.ReadInt16(), reader.ReadInt16());

			if (Main.netMode == NetmodeID.Server)
			{
				if (TileEntity.ByPosition.TryGetValue(position, out var te) && te is TEStorageHeart heart)
					heart.ResetCompactStage();

				Report(true, MessageType.ResetCompactStage + " packet received by server from client " + sender);
				Report(false, "Entity reset: (X: " + position.X + ", Y: " + position.Y + ")");
			}
			else if (Main.netMode == NetmodeID.MultiplayerClient)
			{
				Report(true, MessageType.ResetCompactStage + " packet received by client " + Main.myPlayer);
			}
		}

		internal const int CraftRequestPayloadLength = sizeof(short) * 2 + sizeof(long) + sizeof(int) * 3 + sizeof(sbyte) + sizeof(ulong) * 2;

		internal static void WriteCraftRequestPayload(BinaryWriter writer, Point16 craftingAccess, long operationId, int recipeIndex, int requestedAmount, int recursionDepth, ulong recipeFingerprint, int recipeCount, ulong recipeTableDigest) {
			writer.Write(craftingAccess.X);
			writer.Write(craftingAccess.Y);
			writer.Write(operationId);
			writer.Write(recipeIndex);
			writer.Write(requestedAmount);
			writer.Write((sbyte)recursionDepth);
			writer.Write(recipeFingerprint);
			writer.Write(recipeCount);
			writer.Write(recipeTableDigest);
		}

		internal static void ReadCraftRequestPayload(BinaryReader reader, out Point16 craftingAccess, out long operationId, out int recipeIndex, out int requestedAmount, out int recursionDepth, out ulong recipeFingerprint, out int recipeCount, out ulong recipeTableDigest) {
			craftingAccess = new(reader.ReadInt16(), reader.ReadInt16());
			operationId = reader.ReadInt64();
			recipeIndex = reader.ReadInt32();
			requestedAmount = reader.ReadInt32();
			recursionDepth = reader.ReadSByte();
			recipeFingerprint = reader.ReadUInt64();
			recipeCount = reader.ReadInt32();
			recipeTableDigest = reader.ReadUInt64();
		}

		internal static bool TryReadCraftRequestPayload(BinaryReader reader, out Point16 craftingAccess, out long operationId, out int recipeIndex, out int requestedAmount, out int recursionDepth, out ulong recipeFingerprint, out int recipeCount, out ulong recipeTableDigest) {
			try {
				ReadCraftRequestPayload(reader, out craftingAccess, out operationId, out recipeIndex, out requestedAmount, out recursionDepth, out recipeFingerprint, out recipeCount, out recipeTableDigest);
				return true;
			} catch (Exception exception) when (exception is EndOfStreamException or InvalidDataException or FormatException) {
				craftingAccess = default;
				operationId = default;
				recipeIndex = default;
				requestedAmount = default;
				recursionDepth = default;
				recipeFingerprint = default;
				recipeCount = default;
				recipeTableDigest = default;
				return false;
			}
		}

		internal static ulong GetRecipeTableDigest() {
			if (cachedRecipeTableCount == Recipe.numRecipes)
				return cachedRecipeTableDigest;

			const ulong offset = 14695981039346656037UL;
			const ulong prime = 1099511628211UL;
			ulong digest = offset;
			for (int i = 0; i < Recipe.numRecipes; i++) {
				Recipe recipe = Main.recipe[i];
				unchecked {
					digest = (digest ^ GetRecipeRouteFingerprint(recipe)) * prime;
					digest = (digest ^ (uint)i) * prime;
				}
			}

			cachedRecipeTableCount = Recipe.numRecipes;
			return cachedRecipeTableDigest = digest;
		}

		internal static void ClearRecipeTableDigestCache() {
			cachedRecipeTableCount = -1;
			cachedRecipeTableDigest = 0;
		}

		internal static ulong GetRecipeFingerprint(Recipe recipe) {
			const ulong offset = 14695981039346656037UL;
			ulong hash = offset;

			static void Mix(ref ulong hash, int value) {
				const ulong prime = 1099511628211UL;
				unchecked {
					hash = (hash ^ (uint)value) * prime;
					hash = (hash ^ (uint)(value >> 16)) * prime;
				}
			}

			Mix(ref hash, recipe.createItem.type);
			Mix(ref hash, recipe.createItem.stack);
			Mix(ref hash, recipe.requiredItem.Count);
			foreach (Item item in recipe.requiredItem) {
				Mix(ref hash, item.type);
				Mix(ref hash, item.stack);
			}
			Mix(ref hash, recipe.requiredTile.Count);
			foreach (int tile in recipe.requiredTile)
				Mix(ref hash, tile);
			Mix(ref hash, recipe.acceptedGroups.Count);
			foreach (int group in recipe.acceptedGroups)
				Mix(ref hash, group);

			return hash;
		}

		internal static ulong GetRecipeRouteFingerprint(Recipe recipe) {
			const ulong offset = 14695981039346656037UL;
			const ulong prime = 1099511628211UL;
			ulong hash = offset;
			foreach (char character in recipe.Mod?.Name ?? "Terraria")
				unchecked { hash = (hash ^ character) * prime; }
			unchecked { hash = (hash ^ (uint)recipe.createItem.type) * prime; }
			return hash;
		}

		internal static bool IsValidCraftRequest(int recipeIndex, int requestedAmount, int recursionDepth, int recipeCount)
			=> recipeIndex >= 0 && recipeIndex < recipeCount
			&& requestedAmount > 0 && requestedAmount <= Item.CommonMaxStack
			&& MagicStorageConfig.IsValidRecursionDepth(recursionDepth);

		public static bool SendCraftRequest(Point16 craftingAccess, int recipeIndex, int requestedAmount)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient)
			{
				if (recipeIndex < 0 || recipeIndex >= Main.recipe.Length || StoragePlayer.LocalPlayer.GetStorageHeart() is not TEStorageHeart heart || heart.HasPendingOperation(TEStorageHeart.PendingOperationKind.Craft))
					return false;
				long operationId = heart.BeginClientOperation(TEStorageHeart.PendingOperationKind.Craft);
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.CraftRequest);
				WriteCraftRequestPayload(packet, craftingAccess, operationId, recipeIndex, requestedAmount, MagicStorageConfig.RecipeRecursionDepth, GetRecipeFingerprint(Main.recipe[recipeIndex]), Recipe.numRecipes, GetRecipeTableDigest());
				packet.Send();

				Report(true, MessageType.CraftRequest + " packet sent from client " + Main.myPlayer);
				return true;
			}

			return false;
		}

		public static void ReceiveCraftRequest(BinaryReader reader, int sender)
		{
			if (!TryReadCraftRequestPayload(reader, out Point16 position, out long operationId, out int recipeIndex, out int requestedAmount, out int recursionDepth, out ulong recipeFingerprint, out int recipeCount, out ulong recipeTableDigest)) {
				Report(true, $"Rejected truncated {MessageType.CraftRequest} packet from player {sender}");
				return;
			}
			if (!IsValidCraftRequest(recipeIndex, requestedAmount, recursionDepth, Main.recipe.Length)) {
				SendCraftOutcome(sender, operationId, position, null, false, 0, CraftRejectionReason.InvalidRequest, [], []);
				return;
			}
			ulong serverRecipeTableDigest = GetRecipeTableDigest();
			ulong serverRecipeFingerprint = GetRecipeFingerprint(Main.recipe[recipeIndex]);
			bool recipeCountMatches = recipeCount == Recipe.numRecipes;
			bool routeDigestMatches = recipeTableDigest == serverRecipeTableDigest;
			bool recipeFingerprintMatches = serverRecipeFingerprint == recipeFingerprint;
			if (!recipeCountMatches || !routeDigestMatches || !recipeFingerprintMatches) {
				MagicStorageMod.Instance.Logger.Warn($"Rejected craft operation={operationId}: recipe identity mismatch recipe={recipeIndex}, countMatches={recipeCountMatches}, routeMatches={routeDigestMatches}, fingerprintMatches={recipeFingerprintMatches}, clientCount={recipeCount} serverCount={Recipe.numRecipes} clientRoute={recipeTableDigest:X16} serverRoute={serverRecipeTableDigest:X16} clientFingerprint={recipeFingerprint:X16} serverFingerprint={serverRecipeFingerprint:X16}");
				SendCraftOutcome(sender, operationId, position, null, false, 0, CraftRejectionReason.RecipeMismatch, [], []);
				return;
			}
			if (!InboundPacketGuard.TryGetStorageEntity(position, sender, out TECraftingAccess access, out TEStorageHeart heart)) {
				SendCraftOutcome(sender, operationId, position, null, false, 0, CraftRejectionReason.AccessDenied, [], []);
				return;
			}

			Report(true, MessageType.CraftRequest + " packet received by server from client " + sender);

			if (!CraftingGUI.QueueCraftOnServer(sender, operationId, heart, access, Main.recipe[recipeIndex], requestedAmount, recursionDepth)) {
				Report(true, $"Rejected craft request from player {sender}: recipe={recipeIndex}, amount={requestedAmount}");
				SendCraftOutcome(sender, operationId, position, heart, false, 0, CraftRejectionReason.QueueFull, [], []);
			}
		}

		internal static void CompleteCraftRequest(int sender, long operationId, TEStorageHeart heart, bool accepted, int requestedAmount, CraftRejectionReason reason, List<Item> items, List<Item> results, List<Item> consumed) {
			HashSet<int> typesToUpdate = accepted ? [.. results.Concat(consumed).Select(static item => item.type)] : [];
			if (accepted)
				AuditSystem.ReportCraftRequest(sender, heart, [.. results], [.. consumed]);
			else
				Report(true, $"Rejected queued craft operation={operationId} from player {sender}: {reason}");

			SendCraftOutcome(sender, operationId, heart.Position, heart, accepted, accepted ? requestedAmount : 0, accepted ? CraftRejectionReason.None : reason, typesToUpdate, items);
			if (accepted)
				SendRefreshNetworkItems(heart.Position, false, typesToUpdate);
		}

		private static void SendCraftOutcome(int client, long operationId, Point16 position, TEStorageHeart heart, bool accepted, int acceptedAmount, CraftRejectionReason reason, IReadOnlyCollection<int> affectedTypes, IReadOnlyCollection<Item> excessItems) {
			long revision = accepted && heart is not null ? heart.AdvanceNetworkRevision() : heart?.NetworkRevision ?? 0;
			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.CraftOutcome);
			packet.Write(operationId);
			packet.Write(position);
			packet.Write(revision);
			packet.Write(accepted);
			packet.Write(acceptedAmount);
			packet.Write((byte)reason);
			packet.Write((ushort)Math.Min(affectedTypes.Count, InboundPacketGuard.MaxItemEntries));
			foreach (int type in affectedTypes.Take(InboundPacketGuard.MaxItemEntries))
				packet.Write(type);
			packet.Write(Math.Min(excessItems.Count, InboundPacketGuard.MaxItemEntries));
			foreach (Item item in excessItems.Take(InboundPacketGuard.MaxItemEntries))
				ItemIO.Send(item, packet, true, true);
			packet.Send(client);
		}

		private static void ReceiveCraftOutcome(BinaryReader reader) {
			long operationId = reader.ReadInt64();
			Point16 position = reader.ReadPoint16();
			long revision = reader.ReadInt64();
			bool accepted = reader.ReadBoolean();
			int acceptedAmount = reader.ReadInt32();
			CraftRejectionReason reason = (CraftRejectionReason)reader.ReadByte();
			int typeCount = reader.ReadUInt16();
			if (!InboundPacketGuard.IsValidCount(typeCount, InboundPacketGuard.MaxItemEntries))
				return;
			int[] affectedTypes = new int[typeCount];
			for (int i = 0; i < typeCount; i++)
				affectedTypes[i] = reader.ReadInt32();
			int itemCount = reader.ReadInt32();
			if (!InboundPacketGuard.IsValidCount(itemCount, InboundPacketGuard.MaxItemEntries))
				return;
			Item[] excessItems = new Item[itemCount];
			for (int i = 0; i < itemCount; i++)
				excessItems[i] = ItemIO.Receive(reader, true, true);

			TEStorageHeart heart = position.ResolveToTileEntity() as TEStorageHeart ?? StoragePlayer.LocalPlayer.GetStorageHeart();
			if (heart is null)
				return;
			bool matched = heart.CompleteClientOperation(operationId);
			if (!matched)
				return;
			bool acceptRevision = heart.AcceptNetworkRevision(revision);

			if (accepted && acceptRevision) {
				foreach (Item item in excessItems)
					Main.LocalPlayer.QuickSpawnItem(new EntitySource_TileEntity(heart), item, item.stack);
				MagicUI.SetNextCollectionsToRefresh(affectedTypes);
				SoundEngine.PlaySound(SoundID.Grab);
			} else {
				Report(false, $"Craft operation={operationId} rejected or stale: accepted={accepted}, amount={acceptedAmount}, reason={reason}, revision={revision}");
			}

			CraftingGUI.InvalidateSelectedRecipePreviewAfterInventoryChange();
			CraftingGUI.ForceNextRecipeRefreshToBeFull();
			CraftingGUI.RequestSelectedRecipeSnapshotForNextRecipeRefresh();
			MagicUI.RequestFullRefresh();
		}

		public static void ReceiveCraftResult(BinaryReader reader)
		{
			Player player = Main.LocalPlayer;
			int count = reader.ReadInt32();
			if (!InboundPacketGuard.IsValidCount(count, InboundPacketGuard.MaxItemEntries))
				return;
			for (int k = 0; k < count; k++)
			{
				Item item  = ItemIO.Receive(reader, true, true);
				var  heart = StoragePlayer.LocalPlayer.GetStorageHeart();

				player.QuickSpawnItem(new EntitySource_TileEntity(heart), item, item.stack);
			}

			Report(true, MessageType.CraftResult + " packet received by client " + Main.myPlayer);
			Report(false, "Item objects crafted: " + count);
		}

		public static void ClientRequestSection(Point16 coords)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient)
			{
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.SectionRequest);

				packet.Write(coords.X);
				packet.Write(coords.Y);

				packet.Send();

				Report(false, MessageType.SectionRequest + " packet sent from client " + Main.myPlayer);
			}
		}

		public static void ReceiveClientRequestSection(BinaryReader reader, int sender)
		{
			Point16 coords = new(reader.ReadInt16(), reader.ReadInt16());

			if (Main.netMode == NetmodeID.Server)
			{
				RemoteClient.CheckSection(sender, coords.ToWorldCoordinates());
			}
		}

		public static void ClientReceiveStorageSync(BinaryReader reader)
		{
			TileEntity.Read(reader, true);

			Report(true, MessageType.SyncStorageUnitToClient + " packet received by client " + Main.myPlayer);
		}

		[Obsolete("Use ClientReceiveStorageSync instead")]
		public static void ClientReciveStorageSync(BinaryReader reader) => ClientReceiveStorageSync(reader);

		public static void ClientRequestForceCraftingGUIRefresh() {
			if (Main.netMode == NetmodeID.MultiplayerClient && StoragePlayer.LocalPlayer.GetStorageHeart() is TEStorageHeart heart) {
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.ForceCraftingGUIRefresh);

				packet.Write(heart.Position.X);
				packet.Write(heart.Position.Y);

				packet.Send();

				Report(true, MessageType.ForceCraftingGUIRefresh + " packet sent from client " + Main.myPlayer);
			}
		}

		public static void ReceiveClientForceCraftingGUIRefresh(BinaryReader reader, int sender) {
			Point16 storage = new(reader.ReadInt16(), reader.ReadInt16());

			if (Main.netMode == NetmodeID.Server) {
				//Forward the packet
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.ForceCraftingGUIRefresh);

				packet.Write(storage.X);
				packet.Write(storage.Y);

				packet.Send(ignoreClient: sender);

				Report(true, MessageType.ForceCraftingGUIRefresh + " packet sent from server from client " + sender);

			//	PrintClientRequest(sender, "Refresh UI", storage);
			} else if (Main.netMode == NetmodeID.MultiplayerClient) {
				if (StoragePlayer.IsClientViewingHeart(storage) && StoragePlayer.IsStorageCrafting()) {
					MagicUI.RequestFullRefresh();
					MagicUI.IgnoreSpecificZoneRefreshing = true;

					Report(true, MessageType.ForceCraftingGUIRefresh + " packet received by client " + Main.myPlayer);
				}
			}
		}

		public static void ClientRequestItemTransfer(TEStorageUnit destination, TEStorageUnit source) {
			if (Main.netMode == NetmodeID.MultiplayerClient) {
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.TransferItems);
				packet.Write(destination.Position);
				packet.Write(source.Position);
				packet.Send();

				Report(true, MessageType.TransferItems + " packet sent from client " + Main.myPlayer);
			}
		}

		public static void ReceiveClientRequestItemTransfer(BinaryReader reader, int sender) {
			Point16 destination = reader.ReadPoint16();
			Point16 source = reader.ReadPoint16();

			if (Main.netMode != NetmodeID.Server)
				return;

			if (!InboundPacketGuard.TryGetStorageEntity(destination, sender, out TEStorageUnit unitDestination, out TEStorageHeart destinationHeart, requireInteractionRange: true)) {
				Report(true, MessageType.TransferItems + " packet failed to read on the server.\n" +
					"Reason: Destination was not an accessible Storage Unit");
				return;
			}

			if (source == destination
			|| !InboundPacketGuard.TryGetStorageEntity(source, sender, out TEStorageUnit unitSource, out TEStorageHeart sourceHeart, requireInteractionRange: true)
			|| sourceHeart.Position != destinationHeart.Position
			|| !unitSource.IsTileValidForEntity(source.X, source.Y)
			|| !unitDestination.IsTileValidForEntity(destination.X, destination.Y)) {
				Report(true, MessageType.TransferItems + " packet failed to read on the server.\n" +
					"Reason: Source was not an accessible Storage Unit in the same network");
				return;
			}

			Report(true, MessageType.TransferItems + " packet was successfully received by server from client " + sender);

			AttemptItemTransferAndSendResult(unitDestination, unitSource, out _);
		}

		public static bool AttemptItemTransferAndSendResult(TEStorageUnit destination, TEStorageUnit source, out List<Item> transferredItems, bool netQueue = true) {
			transferredItems = null;

			if (Main.netMode != NetmodeID.Server)
				return false;

			Report(true, $"Performing AttemptItemTransferAndSendResult on source unit (X: {source.Position.X}, Y: {source.Position.Y}) and destination unit (X: {destination.Position.X}, Y: {destination.Position.Y})...");

			TEStorageUnit.AttemptItemTransfer(destination, source, out transferredItems);

			if (transferredItems.Count == 0) {
				//Nothing to do
				Report(false, "No items were transferred");
				return false;
			}

			Report(false, transferredItems.Count + " items were transferred");

			if (netQueue) {
				StartUpdateQueue();

				destination.GetHeart()?.ResetCompactStage();
			}

			destination.FullySync();
			source.FullySync();

			destination.PostChangeContents();
			source.PostChangeContents();

			if (netQueue)
				ProcessUpdateQueue();

			if (destination.GetHeart() is TEStorageHeart heart)
				SendRefreshNetworkItems(heart.Position, false, transferredItems.Select(static i => i.type).Distinct());

			return true;
		}

		public static void SendCoinCompactRequest(Point16 heart) {
			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.RequestCoinCompact);
			packet.Write(StoragePlayer.LocalPlayer.ViewingStorage());
			packet.Send();

			Report(true, MessageType.RequestCoinCompact + " packet sent to all clients");
		}

		public static void ReceiveCoinCompactRequest(BinaryReader reader, int sender) {
			Point16 position = reader.ReadPoint16();

			if (Main.netMode == NetmodeID.Server) {
				using var context = SecuritySystem.CreateAccessContext(sender);

				if (InboundPacketGuard.TryGetStorageEntity(position, sender, out TEStorageComponent _, out TEStorageHeart heart, requireInteractionRange: true)) {
					heart.CompactCoins();
					AuditSystem.ReportControlCoinCompacting(sender, heart);
				}
				Report(true, MessageType.RequestCoinCompact + " packet received by server from client " + sender);
				Report(false, "Entity read: (X: " + position.X + ", Y: " + position.Y + ")");
			} else if (Main.netMode == NetmodeID.MultiplayerClient) {
				Report(true, MessageType.RequestCoinCompact + " packet received by client " + Main.myPlayer);
			}
		}

		public static bool RequestDuplicateSelling(Point16 heart) {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return true;

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.MassDuplicateSellRequest);
			packet.Write(heart);

			if (!SellModeMetadata.NetSend(packet, consumedPacketSpace: sizeof(byte) + sizeof(short) * 2)) {
				// Packet was too large to send
				Main.NewTextMultiline(Language.GetTextValue("Mods.MagicStorage.StorageGUI.SellDuplicatesMenu.SoldItemsReport.PacketTooLarge"), c: Color.Red);
				return false;
			}
			
			packet.Send();

			Report(true, MessageType.MassDuplicateSellRequest + " packet sent to the server");

			return true;
		}

		public static void ReceiveDuplicateSellingRequest(BinaryReader reader, int sender) {
			Point16 position = reader.ReadPoint16();
			SellModeMetadata.NetReceive(reader);

			if (Main.netMode == NetmodeID.Server) {
				if (TileEntity.ByPosition.TryGetValue(position, out var te) && te is TEStorageHeart heart) {
					int totalItemCount = SellModeMetadata.Count;
					SellModeMetadata.HandleSell(heart, out int soldItemCount, out var sellValue, Main.player[sender]);

					Report(false, $"{soldItemCount} / {totalItemCount} items were sold for {sellValue.TotalValue} copper coins");

					ModPacket packet = MagicStorageMod.Instance.GetPacket();
					packet.Write((byte)MessageType.MassDuplicateSellResult);
					packet.Write((short)sender);
					packet.Write(position);
					packet.Write7BitEncodedInt64(sellValue.TotalValue);
					packet.Write7BitEncodedInt(soldItemCount);
					packet.Write7BitEncodedInt(totalItemCount);

					packet.Send();

					AuditSystem.ReportMassItemSell(sender, heart, soldItemCount, sellValue.TotalValue);
				} else {
					// Invalid request
					SellModeMetadata.Clear();
				}

				Report(false, MessageType.MassDuplicateSellRequest + " packet received by server from client " + sender);
				Report(false, "Entity read: (X: " + position.X + ", Y: " + position.Y + ")");
			} else if (Main.netMode == NetmodeID.MultiplayerClient) {
				SellModeMetadata.Clear();

				Report(true, MessageType.MassDuplicateSellRequest + " packet received by client " + Main.myPlayer);
			}
		}

		public static void ClientReceiveDuplicateSellingResult(BinaryReader reader) {
			short sender = reader.ReadInt16();
			Point16 heart = reader.ReadPoint16();
			long coppersEarned = reader.Read7BitEncodedInt64();

			int sold = reader.Read7BitEncodedInt();
			int totalItemsBeforeSell = reader.Read7BitEncodedInt();

			if (Main.netMode != NetmodeID.MultiplayerClient) {
				//Read the data, but do nothing with it
				return;
			}

			if (!TileEntity.ByPosition.TryGetValue(heart, out TileEntity heartEntity) || heartEntity is not TEStorageHeart) {
				Report(true, MessageType.MassDuplicateSellResult + " packet was malformed: Storage Heart location did not have a Storage Heart");
				return;
			}

			Report(true, $"{sold} items were sold/destroyed at heart (X: {heart.X}, Y: {heart.Y}) for {coppersEarned} copper coins");

			Report(false, MessageType.MassDuplicateSellResult + " packet received by client " + Main.myPlayer);

			if (sender == Main.myPlayer)
				SellModeMetadata.ClientReportSell(sold, totalItemsBeforeSell, new SellModeMetadata.Coins(coppersEarned));
		}

		public static void RequestStorageUnitStyle(Point16 unit) {
			if (Main.netMode == NetmodeID.MultiplayerClient) {
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.RequestStorageUnitStyle);
				packet.Write(unit);
				packet.Send();

				Report(true, MessageType.RequestStorageUnitStyle + " packet sent to the server");
			}
		}

		public static void ReceiveStorageUnitStyle(BinaryReader reader, int sender) {
			Point16 unit = reader.ReadPoint16();

			if (Main.netMode != NetmodeID.Server)
				return;

			//Safeguard:  Ensure that the map section exists before sending data
			RemoteClient.CheckSection(sender, unit.ToWorldCoordinates());

		//	PrintClientRequest(sender, "Update Unit Type", unit);

			if (!InboundPacketGuard.TryGetStorageEntity(unit, sender, out TEStorageUnit storageUnit, out _, requireInteractionRange: true)
			|| !storageUnit.IsTileValidForEntity(unit.X, unit.Y)) {
				Report(true, MessageType.RequestStorageUnitStyle + " packet was malformed: Storage Unit location did not have a Storage Unit");
				return;
			}

			storageUnit.UpdateTileFrameWithNetSend();

			Report(false, MessageType.RequestStorageUnitStyle + " packet received by server from client " + sender);
		}

		public static void ClientReceiveQuickStackToNearbyStorageResult(BinaryReader reader) {
			bool playSound = reader.ReadBoolean();
			int origType = reader.ReadInt32();

			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			// NOTE: 1.4.4 does not play a sound
			/*
			if (playSound)
				SoundEngine.PlaySound(SoundID.Grab);
			*/

			CraftingGUI.NotifyStorageInventoryChanged(origType);
		}

		public static void SendGolemTextUpdate() {
			if (Main.netMode != NetmodeID.Server)
				return;

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.GolemHelpTextUpdate);
			StorageWorld.NetSendHelpTips(packet);
			packet.Send();
		}

		public static void ClientReceiveGolemTextUpdate(BinaryReader reader) {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			StorageWorld.NetReceiveHelpTips(reader);
			Golem.ReportNewTipUnlocked();
		}

		public static void ClientRequestServerOperator() {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.ClientRequestServerOp);
			packet.Send();

			Report(true, MessageType.ClientRequestServerOp + " packet sent to the server");
		}

		public static void ServerReceiveOperatorRequest(int sender) {
			if (Main.netMode != NetmodeID.Server)
				return;

			bool print = !Netcode.KeyIsGenerated;

			string key = Netcode.ServerOperatorKey;

			Report(false, MessageType.ClientRequestServerOp + " packet received by server from client " + sender);

			if (print) {
				string keyMsg = MagicStorageMod.Instance.GetLocalization("ServerOperator.CommandInfo.ServerKeyText").Format(key);

				Utility.WriteLineColoredSafely(keyMsg, ConsoleColor.Yellow, ConsoleColor.Black);
				// Send the text to the server log as well
				MagicStorageMod.Instance.Logger.Info("\n" + keyMsg);
			}

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.ServerOpResponse);
			packet.Send(toClient: sender);

			Report(false, MessageType.ServerOpResponse + " packet sent to client " + sender);
		}

		public static void ClientReceiveOperatorReponse() {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			Report(false, MessageType.ServerOpResponse + " packet received by client " + Main.myPlayer);

			Main.NewText(MagicStorageMod.Instance.GetLocalization("ServerOperator.CommandInfo.ClientKeyText"), Color.Yellow);

			Netcode.RequestingOperatorKey = true;
		}

		public static void ClientSendOperatorKey(string key) {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			Netcode.RequestingOperatorKey = false;

			if (!Netcode.IsKeyValidForConfirmationMessage(key)) {
				//Bail immediately since the key couldn't be valid in the first place
				Netcode.ClientPrintKeyReponse(valid: false);
				return;
			}

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.ClientRequestServerOpConfirmation);
			byte[] bytes = StringScrambling.Scramble(key);
			packet.Write((byte)bytes.Length);
			packet.Write(bytes);
			packet.Send();

			Report(true, MessageType.ClientRequestServerOpConfirmation + " packet sent to the server");
		}

		public static void ServerReceiveOperatorKeyFromClient(BinaryReader reader, int sender) {
			byte count = reader.ReadByte();
			byte[] bytes = reader.ReadBytes(count);

			if (Main.netMode != NetmodeID.Server)
				return;

			bool valid = count == Netcode.KeyLength * sizeof(char)
				&& bytes.Length == count
				&& StringScrambling.Unscramble(bytes) == Netcode.ServerOperatorKey;

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.ServerOpConfirmationResult);
			packet.Write(valid);
			packet.Send(toClient: sender);

			Report(false, MessageType.ServerOpConfirmationResult + " packet sent to client " + sender);

			if (valid)
				ServerSetPlayerOperator(sender, hasOp: true, manualOp: true);
		}

		public static void ClientReceiveOperatorConformationResult(BinaryReader reader) {
			bool valid = reader.ReadBoolean();

			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			Report(false, MessageType.ServerOpConfirmationResult + " packet received by client " + Main.myPlayer);

			Netcode.ClientPrintKeyReponse(valid);

		}

		public static void ClientRequestPlayerOperatorChange(int player, bool hasOp) {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.ClientRequestPlayerOperatorChange);
			packet.Write((byte)player);
			packet.Write(hasOp);
			packet.Send();
		}

		public static void ServerReceivePlayerOperatorChange(BinaryReader reader, int sender) {
			int player = reader.ReadByte();
			bool hasOp = reader.ReadBoolean();

			if (Main.netMode != NetmodeID.Server
			|| !Main.player[sender].GetModPlayer<OperatorPlayer>().IsAdministrator
			|| player < 0 || player >= Main.maxPlayers || !Main.player[player].active)
				return;

			ServerSetPlayerOperator(player, hasOp, manualOp: false);
		}

		public static void ReceivePlayerHasOperator(BinaryReader reader) {
			byte plr = reader.ReadByte();
			BitsByte opFlags = reader.ReadByte();
			if (plr >= Main.maxPlayers)
				return;

			var mp = Main.player[plr].GetModPlayer<OperatorPlayer>();

			opFlags.Retrieve(ref mp.hasOp, ref mp.manualOp);

			if (Main.netMode == NetmodeID.MultiplayerClient && plr == Main.myPlayer && mp.IsAdministrator)  // Force a sync of the network information
				RequestAccessibleNetworksByDefault();

			Report(true, MessageType.PlayerHasServerOp + " packet received by client " + Main.myPlayer);
		}

		internal static void ServerSetPlayerOperator(int plr, bool hasOp, bool manualOp) {
			if (Main.netMode != NetmodeID.Server || !InboundPacketGuard.IsValidTransportSender(plr, Main.maxPlayers))
				return;

			var mp = Main.player[plr].GetModPlayer<OperatorPlayer>();
			bool wasOperator = mp.hasOp, wasAdministrator = mp.IsAdministrator;
			mp.hasOp = hasOp;
			mp.manualOp = manualOp;

			ServerPreparePlayerHasOperatorPacket(plr, mp).Send();

			if (mp.IsAdministrator != wasAdministrator) {
				if (mp.IsAdministrator)
					AuditSystem.ReportAdministratorStatusAssignment(plr);
			} else if (mp.hasOp != wasOperator) {
				if (mp.hasOp)
					AuditSystem.ReportOperatorStatusAssignment(plr);
				else
					AuditSystem.ReportOperatorStatusRemoval(plr);
			}
		}

		internal static ModPacket ServerPreparePlayerHasOperatorPacket(int plr, OperatorPlayer mp) {
			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.PlayerHasServerOp);
			packet.Write((byte)plr);

			BitsByte bb = new(mp.hasOp, mp.manualOp);
			packet.Write(bb);
			
			return packet;
		}

		public static void ClientRequestDepositFromBank(Point16 access, PlayerBankInventory inventory, Action<Player, Item[]> netResult) {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			UIStorageControlDepositPlayerInventoryButton.PendingResultAction = netResult;

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.ClientRequestPlayerBankDeposit);

			packet.Write(access);
			packet.Write((byte)inventory);

			packet.Send();

			Report(true, MessageType.ClientRequestPlayerBankDeposit + " packet sent to the server");
		}

		public static void ServerReceiveDepositFromBankRequest(BinaryReader reader, int sender) {
			Point16 access = reader.ReadPoint16();
			PlayerBankInventory inventoryType = (PlayerBankInventory)reader.ReadByte();
			if (!InboundPacketGuard.TryGetStorageEntity(access, sender, out TEStorageComponent _, out TEStorageHeart storageHeart, requireInteractionRange: true)
			|| !TryGetPlayerBankInventory(Main.player[sender], inventoryType, out Item[] inventory))
				return;

			if (Main.netMode != NetmodeID.Server) {
				Report(true, MessageType.ClientRequestPlayerBankDeposit + " packet received by client " + Main.myPlayer);
				return;
			}

			UIStorageControlDepositPlayerInventoryButton.TryDepositItems(inventory, storageHeart, false, out bool changed);

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.PlayerBankDepositResult);
			packet.Write(changed);

			packet.Write((byte)inventoryType);
			packet.Write((byte)inventory.Length);

			for (int i = 0; i < inventory.Length; i++)
				ItemIO.Send(inventory[i], packet, true, true);

			packet.Send(toClient: sender);

			Report(false, MessageType.PlayerBankDepositResult + " packet sent to client " + sender);

		//	PrintClientRequest(sender, "Deposit Items from Bank/Safe/Forge", access);
		}

		public static void ClientReceiveDepositFromBankResult(BinaryReader reader) {
			bool changed = reader.ReadBoolean();
			PlayerBankInventory inventoryType = (PlayerBankInventory)reader.ReadByte();
			int count = reader.ReadByte();
			if (!TryGetPlayerBankInventory(Main.LocalPlayer, inventoryType, out Item[] target) || count != target.Length)
				return;

			Item[] inventory = new Item[count];

			for (int i = 0; i < count; i++)
				inventory[i] = ItemIO.Receive(reader, true, true);

			if (Main.netMode != NetmodeID.MultiplayerClient) {
				Report(true, MessageType.PlayerBankDepositResult + " packet received by the server");
				return;
			}

			Interlocked.Exchange(ref UIStorageControlDepositPlayerInventoryButton.PendingResultAction, null)?.Invoke(Main.LocalPlayer, inventory);

			if (changed)
				SoundEngine.PlaySound(SoundID.Grab);

			Report(true, MessageType.PlayerBankDepositResult + " packet received by client " + Main.myPlayer);
		}

		private static bool TryGetPlayerBankInventory(Player player, PlayerBankInventory inventory, out Item[] items) {
			items = inventory switch {
				PlayerBankInventory.PiggyBank => player.bank.item,
				PlayerBankInventory.Safe => player.bank2.item,
				PlayerBankInventory.DefendersForge => player.bank3.item,
				PlayerBankInventory.VoidVault => player.bank4.item,
				_ => null
			};
			return items is { Length: > 0 and <= InboundPacketGuard.MaxBankEntries };
		}

		public static void SendComponentPlacement(Point16 position) {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.ComponentPlacement);
			packet.Write(position);
			packet.Send();

			Report(true, MessageType.ComponentPlacement + " packet sent to the server");
		}

		public static void ServerReceiveComponentPlacement(BinaryReader reader, int sender) {
			Point16 position = reader.ReadPoint16();

			if (Main.netMode != NetmodeID.Server)
				return;

		//	PrintClientRequest(sender, "Component Placement", position);
			Report(false, MessageType.ComponentPlacement + " packet received by server from client " + sender);
		}

		public static void SendComponentDestruction(Point16 position) {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.ComponentDestruction);
			packet.Write(position);
			packet.Send();

			Report(true, MessageType.ComponentDestruction + " packet sent to the server");
		}

		public static void ServerReceiveComponentDestruction(BinaryReader reader, int sender) {
			Point16 position = reader.ReadPoint16();

			if (Main.netMode != NetmodeID.Server)
				return;

		//	PrintClientRequest(sender, "Component Destruction", position);
			Report(false, MessageType.ComponentDestruction + " packet received by server from client " + sender);
		}

		public static void ClientInformStorageHeartUsage(TEStorageHeart heart) {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			var msg = heart.clientUsingHeart[Main.myPlayer] ? MessageType.ClientLockStorageHeart : MessageType.ClientUnlockStorageHeart;

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)msg);
			packet.Write(StoragePlayer.LocalPlayer.ViewingStorage());
			packet.Send();

			Report(true, msg + " packet sent to the server");
		}

		public static void ReceiveStorageHeartUsage(BinaryReader reader, int sender, bool inUse) {
			int packetPlayer = Main.netMode == NetmodeID.Server ? -1 : reader.ReadByte();
			int player = InboundPacketGuard.ResolvePlayer(packetPlayer, sender, Main.netMode);
			Point16 position = reader.ReadPoint16();

			var msg = inUse ? MessageType.ClientLockStorageHeart : MessageType.ClientUnlockStorageHeart;

			if (!TileEntity.ByPosition.TryGetValue(position, out TileEntity entity) || entity is not TEStorageHeart heart)
				return;

			heart.clientUsingHeart[player] = inUse;

			if (Main.netMode == NetmodeID.Server) {
				// Forward to other clients
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)msg);
				packet.Write((byte)player);
				packet.Write(position);
				packet.Send(ignoreClient: sender);

				Report(true, msg + " packet sent from server from client " + sender);
			} else
				Report(true, msg + " packet received by client " + Main.myPlayer);
		}

		public static void ClientRequestExactItemDeletion(TEStorageHeart heart, Item item) {
			if (Main.netMode != NetmodeID.MultiplayerClient || !Main.LocalPlayer.GetModPlayer<OperatorPlayer>().hasOp)
				return;

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.DeleteSpecificItem);
			packet.Write(StoragePlayer.LocalPlayer.ViewingStorage());
			ReadOnlySpan<byte> data;
			using (ObjectSwitch.Create(ref item.stack, 1))
				data = Utility.ToByteSpanNoCompression(item);
			packet.Write7BitEncodedInt(data.Length);
			packet.Write(data);
			packet.Write(item.stack);
			packet.Send();
		}

		public static void ServerReceiveExactItemDeletionRequest(BinaryReader reader, int sender) {
			Point16 point = reader.ReadPoint16();
			int dataLength = reader.Read7BitEncodedInt();
			if (dataLength <= 0 || dataLength > InboundPacketGuard.MaxSerializedItemBytes)
				return;
			ReadOnlySpan<byte> item = reader.ReadBytes(dataLength);
			if (item.Length != dataLength)
				return;
			int stack = reader.ReadInt32();

			if (Main.netMode != NetmodeID.Server)
				return;

			if (stack <= 0
			|| !Main.player[sender].GetModPlayer<OperatorPlayer>().hasOp
			|| !InboundPacketGuard.TryGetStorageEntity(point, sender, out TEStorageComponent _, out TEStorageHeart heart, requireInteractionRange: true))
				return;

			int toRemove = stack;
			if (heart.TryDeleteExactItem(item, out var netItem, ref toRemove))
				AuditSystem.ReportItemDeletion(sender, heart, new ReducedItem(netItem.Type, stack - toRemove));
		}

		public static void RequestItemShimmering(TEDecraftingAccess access, int itemType, int toShimmer) {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.RequestShimmerItemInStorage);
			packet.Write(access.Position);
			packet.Write(itemType);
			packet.Write(toShimmer);
			packet.Send();

			Report(true, MessageType.RequestShimmerItemInStorage + " packet sent to the server");
		}

		public static void ServerReceiveItemShimmeringRequest(BinaryReader reader, int sender) {
			Point16 accessPosition = reader.ReadPoint16();
			int itemType = reader.ReadInt32();
			int toShimmer = reader.ReadInt32();

			if (Main.netMode != NetmodeID.Server)
				return;

			Report(true, MessageType.RequestShimmerItemInStorage + " packet received by server from client " + sender);
			if (!InboundPacketGuard.TryGetStorageEntity(accessPosition, sender, out TEDecraftingAccess access, out TEStorageHeart heart, requireInteractionRange: true)
			|| !access.IsTileValidForEntity(accessPosition.X, accessPosition.Y)
			|| itemType <= ItemID.None || itemType >= ItemLoader.ItemCount
			|| toShimmer <= 0) {
				SendItemShimmeringResult(sender, accessPosition, itemType, success: false);
				return;
			}

			Player player = Main.player[sender];
			int available = 0;
			foreach (Item item in heart.GetStoredItems()) {
				if (item.type == itemType)
					available = (int)Math.Min(int.MaxValue, (long)available + item.stack);
			}
			foreach (EnvironmentModule module in heart.GetModules()) {
				foreach (Item item in module.GetAdditionalItems(new EnvironmentSandbox(player, heart)) ?? []) {
					if (item is { IsAir: false } && item.type == itemType)
						available = (int)Math.Min(int.MaxValue, (long)available + item.stack);
				}
			}

			toShimmer = Math.Min(toShimmer, available);
			if (toShimmer <= 0) {
				SendItemShimmeringResult(sender, accessPosition, itemType, success: false);
				return;
			}

			Item shimmeringItem = new Item(itemType, toShimmer);
			int iconicItem = MagicCache.ShimmerInfos[itemType].iconicItem;
			StorageIntermediary storage = new(heart, player.Center, player.Bottom);
			List<IShimmerResult> results = [];

			while (!shimmeringItem.IsAir) {
				IShimmerResult result = ShimmerMetrics.AttemptItemTransmutation(shimmeringItem, storage, net: true);
				if (result is null)
					break;
				results.Add(result);
			}

			int shimmered = toShimmer - shimmeringItem.stack;
			if (shimmered <= 0
			|| !CraftingGUI.TryPlanServerItemConsumption(player, heart, storage.toWithdraw, out List<Item> withdrawals, out List<Item> moduleConsumptions, out List<Item> moduleItems)) {
				SendItemShimmeringResult(sender, accessPosition, itemType, success: false);
				return;
			}

			Report(false, "Handling storage inventory changes and sending excess items...");

			List<Item> items;
			using (SecuritySystem.CreateAccessContext(sender)) {
				if (!CraftingGUI.TryHandleCraftWithdrawAndDeposit(heart, withdrawals, moduleConsumptions, moduleItems, CraftingGUI.CompactItemList(storage.toDeposit), out items)) {
					SendItemShimmeringResult(sender, accessPosition, itemType, success: false);
					return;
				}
			}

			Item effectItem = new(itemType, shimmered);
			StorageIntermediary effectStorage = new(heart, player.Center, player.Bottom) { IgnoreContentChanges = true };
			int previousPlayer = Main.myPlayer;
			Main.myPlayer = sender;
			try {
				foreach (IShimmerResult result in results)
					result.OnShimmer(effectItem, iconicItem, effectStorage, net: false);
			} finally {
				Main.myPlayer = previousPlayer;
			}

			if (items.Count > 0) {
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.CraftResult);
				packet.Write(items.Count);
				foreach (Item item in items)
					ItemIO.Send(item, packet, true, true);
				packet.Send(sender);

				Report(false, MessageType.CraftResult + " packet sent to all clients");
			}

			SendRefreshNetworkItems(heart.Position, false);
			SendItemShimmeringResult(sender, accessPosition, itemType, success: true);
		}

		private static void SendItemShimmeringResult(int client, Point16 accessPosition, int itemType, bool success) {
			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.ShimmerItemInStorageResult);
			packet.Write(accessPosition);
			packet.Write(itemType);
			packet.Write(success);
			packet.Send(client);
		}

		private static void ClientReceiveItemShimmeringResult(BinaryReader reader) {
			Point16 accessPosition = reader.ReadPoint16();
			int itemType = reader.ReadInt32();
			bool success = reader.ReadBoolean();

			if (Main.netMode != NetmodeID.MultiplayerClient || StoragePlayer.LocalPlayer.GetDecraftingAccess()?.Position != accessPosition)
				return;

			DecraftingGUI.SetNextDefaultItemCollectionToRefresh(itemType);
			MagicUI.IgnoreSpecificZoneRefreshing = !success;
			MagicUI.RequestFullRefresh();
		}

		public static void SendStorageHeartName(TEStorageHeart heart) {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.RenameStorageHeart);
			packet.Write(StoragePlayer.LocalPlayer.ViewingStorage());
			packet.Write(heart.storageName);
			packet.Send();

			Report(true, MessageType.RenameStorageHeart + " packet sent to the server");
		}

		public static void ReceiveStorageHeartName(BinaryReader reader, int sender) {
			Point16 position = reader.ReadPoint16();
			if (!TryReadBoundedString(reader, InboundPacketGuard.MaxStorageNameLength, out string name))
				return;

			if (Main.netMode == NetmodeID.MultiplayerClient) {
				if (position.ResolveToTileEntity() is TEStorageHeart clientHeart)
					clientHeart.storageName = name;
				Report(true, MessageType.RenameStorageHeart + " packet received by client " + Main.myPlayer);
				return;
			}

			if (!InboundPacketGuard.TryGetStorageEntity(position, sender, out TEStorageComponent _, out TEStorageHeart heart, requireInteractionRange: true))
				return;

			heart.storageName = name;

			// Broadcast the authoritative value, including back to the requester.
			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.RenameStorageHeart);
			packet.Write(heart.Position);
			packet.Write(name);
			packet.Send();

			Report(true, MessageType.RenameStorageHeart + " packet sent from server from client " + sender);
		}

		internal static bool TryReadBoundedString(BinaryReader reader, int maxCharacters, out string value) {
			value = null;
			int byteCount;
			try {
				byteCount = reader.Read7BitEncodedInt();
			} catch (Exception exception) when (exception is FormatException or EndOfStreamException) {
				return false;
			}

			if (byteCount < 0 || byteCount > maxCharacters * 4)
				return false;

			byte[] bytes = reader.ReadBytes(byteCount);
			if (bytes.Length != byteCount)
				return false;

			value = Encoding.UTF8.GetString(bytes);
			return value.Length <= maxCharacters;
		}

		[Obsolete($"Use {nameof(RequestStorageDepositHistoryChunks)} instead", error: true)]
		public static void SyncStorageDepositHistory(TEStorageHeart heart) {
			if (Main.netMode == NetmodeID.SinglePlayer)
				return;

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.SyncDepositHistory);
			packet.Write(heart.Position);
			heart.SendHistory(packet);
			packet.Send();

			Report(true, MessageType.SyncDepositHistory + " packet sent to the server");
		}

		[Obsolete]
		private static void Obsolete_ReceiveStorageDepositHistory(BinaryReader reader, int sender) => ReceiveStorageDepositHistory(reader, sender);

		[Obsolete("Use ReceiveStorageDepositHistory instead", error: true)]
		public static void ReceiveStorageDepositHistory(BinaryReader reader, int sender) {
			Point16 position = reader.ReadPoint16();

			if (!TileEntity.ByPosition.TryGetValue(position, out TileEntity entity) || entity is not TEStorageHeart heart)
				return;

			heart.ReceiveHistory(reader);

			if (Main.netMode == NetmodeID.MultiplayerClient) {
				Report(true, MessageType.SyncDepositHistory + " packet received by client " + Main.myPlayer);
				return;
			}

			// Forward the history to other clients
			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.SyncDepositHistory);
			packet.Write(position);
			heart.SendHistory(packet);
			packet.Send(ignoreClient: sender);

			Report(true, MessageType.SyncDepositHistory + " packet sent from server from client " + sender);
		}

		public static void ClientSendCoreRemoval(Point16 position) {
			if (Main.netMode == NetmodeID.MultiplayerClient) {
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.ClientSendCoreRemoval);
				packet.Write(position);
				packet.Send();

				Report(true, MessageType.ClientSendCoreRemoval + " packet sent from client " + Main.myPlayer);
			}
		}

		public static void ReceiveCoreRemoval(BinaryReader reader, int sender) {
			Point16 position = reader.ReadPoint16();

			if (Main.netMode == NetmodeID.Server) {
				Player player = Main.player[sender];
				if (player.HeldItem.type == ModContent.ItemType<StorageExtractor>()
				&& InboundPacketGuard.TryGetStorageEntity(position, sender, out TEStorageUnit unit, out TEStorageHeart heart, requireInteractionRange: true)
				&& unit.IsTileValidForEntity(position.X, position.Y)
				&& unit.GetCurrentTier() is StorageUnitTier tier
				&& tier.Type != StorageUnitTier.Empty.Type) {
					var types = unit.GetItems().Select(static i => i.type).Distinct().ToList();

					Item spawnedItem = unit.RemoveItemsAndSpawnCore();
					if (spawnedItem is not null)
						AuditSystem.ReportStorageUnitCoreRemoval(sender, unit, new ReducedItem(spawnedItem));

					// RemoveItemsAndSpawnCore() already sends the frame change
				//	unit.UpdateTileFrameWithNetSend();

					heart.ResetCompactStage();
					SendRefreshNetworkItems(heart.Position, typesToRefresh: types);
				}

				Report(true, MessageType.ClientSendCoreRemoval + " packet received by server from client " + sender);
			}
		}

		public static void ClientSendCoreInsertion(Point16 position, BaseStorageCore core) {
			if (Main.netMode == NetmodeID.MultiplayerClient) {
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.ClientSendCoreInsertion);
				packet.Write(position);
				packet.Write((byte)Main.LocalPlayer.selectedItem);
				packet.Send();

				Report(true, MessageType.ClientSendCoreInsertion + " packet sent from client " + Main.myPlayer);
			}
		}

		public static void ReceiveCoreInsertion(BinaryReader reader, int sender) {
			Point16 position = reader.ReadPoint16();
			int slot = reader.ReadByte();

			if (Main.netMode == NetmodeID.Server) {
				Player player = Main.player[sender];
				if (InboundPacketGuard.IsValidInventorySlot(slot)
				&& InboundPacketGuard.TryGetStorageEntity(position, sender, out TEStorageUnit unit, out TEStorageHeart heart, requireInteractionRange: true)
				&& unit.IsTileValidForEntity(position.X, position.Y)
				&& unit.GetCurrentTier()?.Type == StorageUnitTier.Empty.Type
				&& player.inventory[slot]?.ModItem is BaseStorageCore core) {
					List<Item> coreItems;
					try {
						coreItems = core.RetrieveItems().ToList();
					} catch (Exception exception) {
						MagicStorageMod.Instance.Logger.Warn($"Rejected invalid Storage Core from player {sender}", exception);
						return;
					}

					if (coreItems.Count > core.Tier.Capacity)
						return;

					unit.InsertCore(core);
					unit.GetFramingState(out StorageUnitFullness fullness, out bool active);
					Components.StorageUnit.SetTypeAndStyle(position.X, position.Y, core.Tier, fullness, active);
					unit.UpdateTileFrameWithNetSend();
					AuditSystem.ReportStorageUnitCoreInsertion(sender, unit, core);

					Item inventoryItem = player.inventory[slot];
					inventoryItem.stack--;
					if (inventoryItem.stack <= 0)
						inventoryItem.TurnToAir();
					// InsertCore already sends the frame change
				//	unit.UpdateTileFrameWithNetSend();

					heart.ResetCompactStage();
					SendRefreshNetworkItems(heart.Position, ignoreSpecificRefreshes: true);
				}

				Report(true, MessageType.ClientSendCoreInsertion + " packet received by server from client " + sender);
			}
		}

		public static void RequestSecurityNetworkCreation(string name, string password, bool restricted) {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.SecurityNetworkCreation);
			packet.WriteStringsSafely(name, password);
			packet.Write(restricted);
			packet.Send();

			Report(true, MessageType.SecurityNetworkCreation + " packet sent to the server");
		}

		public static void ReceiveSecurityNetworkCreation(BinaryReader reader, int sender) {
			if (Main.netMode == NetmodeID.Server) {
				reader.ReadStringsSafely(out string name, out string password);
				bool restricted = reader.ReadBoolean();

				Report(true, MessageType.SecurityNetworkCreation + " packet received by server from client " + sender);

				var result = SecuritySystem.ServerCreateNetwork(sender, name, password, restricted, out int networkID);

				// Inform all clients of the result
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.SecurityNetworkCreation);
				packet.Write((byte)result);
				packet.Write(networkID);
				packet.Write((byte)sender);
				packet.Send();

				Report(true, MessageType.SecurityNetworkCreation + " packet sent to all clients");
			} else {
				NetworkActionResult result = (NetworkActionResult)reader.ReadByte();
				int networkID = reader.ReadInt32();
				int creator = reader.ReadByte();

				Report(true, MessageType.SecurityNetworkCreation + " packet received by client " + Main.myPlayer);

				if (creator == Main.myPlayer)
					SecuritySystem.ReportNetworkResult(result, NetworkReportCategory.Creation);

				SecuritySystem.HandleNetworkAccessibilityOnCreation(result, creator, Main.LocalPlayer, networkID);

				Report(false, $"  Result: {result}");

				RequestSecurityNetworkList();
			}
		}

		public static void RequestSecurityNetworkRemoval(int networkID, string password) {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.SecurityNetworkRemoval);
			packet.Write(networkID);
			packet.WriteStringSafely(password);
			packet.Send();

			Report(true, MessageType.SecurityNetworkRemoval + " packet sent to the server");
		}

		public static void ReceiveSecurityNetworkRemoval(BinaryReader reader, int sender) {
			if (Main.netMode == NetmodeID.Server) {
				int networkID = reader.ReadInt32();
				string password = reader.ReadStringSafely();

				Report(true, MessageType.SecurityNetworkRemoval + " packet received by server from client " + sender);

				var result = SecuritySystem.ServerRemoveNetwork(sender, networkID, password);

				// Inform all clients of the result
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.SecurityNetworkRemoval);
				packet.Write((byte)result);
				packet.Write(networkID);
				packet.Write((byte)sender);
				packet.Send();

				Report(true, MessageType.SecurityNetworkRemoval + " packet sent to all clients");
			} else {
				NetworkActionResult result = (NetworkActionResult)reader.ReadByte();
				int networkID = reader.ReadInt32();
				int requestingPlayer = reader.ReadByte();

				Report(true, MessageType.SecurityNetworkRemoval + " packet received by client " + Main.myPlayer);

				if (requestingPlayer == Main.myPlayer)
					SecuritySystem.ReportNetworkResult(result, NetworkReportCategory.Removal);

				SecuritySystem.HandleNetworkAccessibilityOnRemoval(result, Main.LocalPlayer, networkID);

				Report(false, $"  Result: {result}");

				RequestSecurityNetworkList();
			}
		}

		public static void RequestSecurityNetworkJoin(int networkID, string password) {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			// Check if the player already has access to the network
			if (Main.LocalPlayer.GetModPlayer<SecurityPlayer>().HasJoinedNetwork(networkID))
				return;

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.SecurityNetworkJoin);
			packet.Write(networkID);
			packet.WriteStringSafely(password);
			packet.Send();

			Report(true, MessageType.SecurityNetworkJoin + " packet sent to the server");
		}

		public static void ReceiveSecurityNetworkJoinAttempt(BinaryReader reader, int sender) {
			if (Main.netMode == NetmodeID.Server) {
				int networkID = reader.ReadInt32();
				string password = reader.ReadStringSafely();

				Report(true, MessageType.SecurityNetworkJoin + " packet received by server from client " + sender);

				var result = SecuritySystem.ServerJoinNetwork(sender, networkID, password);

				// Inform the client of the result
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.SecurityNetworkJoin);
				packet.Write((byte)result);
				packet.Write(networkID);
				packet.Send(toClient: sender);

				Report(true, MessageType.SecurityNetworkJoin + " packet sent to client " + sender);
			} else {
				NetworkActionResult result = (NetworkActionResult)reader.ReadByte();
				int networkID = reader.ReadInt32();
				string password = null;
				if (result.IsSuccess() && SecuritySystem.GetNetwork(networkID).restricted)
					Main.LocalPlayer.GetModPlayer<SecurityPlayer>().TryGetPassword(networkID, out password);

				Report(true, MessageType.SecurityNetworkJoin + " packet received by client " + Main.myPlayer);

				SecuritySystem.ReportNetworkResult(result, NetworkReportCategory.Join);

				SecuritySystem.HandleNetworkAccessibilityOnJoin(result, Main.LocalPlayer, networkID, password);

				Report(false, $"  Result: {result}");
			}
		}

		public static void RequestSecurityNetworkAccess(int networkID) {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			// Check if the player already has access to the network
			if (Main.LocalPlayer.GetModPlayer<SecurityPlayer>().HasJoinedNetwork(networkID))
				return;

			// Check for operator status, and immediately give access in that case
			if (Main.LocalPlayer.GetModPlayer<OperatorPlayer>().hasOp) {
				Report(true, "Granting immediate access to network due to operator status");

				SecuritySystem.ReportNetworkResult(NetworkActionResult.OperatorForcedSuccess, NetworkReportCategory.Access);

				SecuritySystem.HandleNetworkAccessibilityOnAccess(NetworkActionResult.OperatorForcedSuccess, Main.LocalPlayer, networkID);

				return;
			}

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.SecurityNetworkAccessible);
			packet.Write(networkID);
			packet.Send();

			Report(true, MessageType.SecurityNetworkAccessible + " packet sent to the server");
		}

		public static void ReceiveSecurityNetworkAccessAttempt(BinaryReader reader, int sender) {
			if (Main.netMode == NetmodeID.Server) {
				int networkID = reader.ReadInt32();

				Report(true, MessageType.SecurityNetworkAccessible + " packet received by server from client " + sender);

				var result = SecuritySystem.ServerAccessNetwork(sender, networkID);

				SecuritySystem.HandleNetworkAccessibilityOnAccess(result, Main.player[sender], networkID);

				// Inform the client of the result
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.SecurityNetworkAccessible);
				packet.Write((byte)result);
				packet.Write(networkID);
				packet.Send(toClient: sender);

				Report(true, MessageType.SecurityNetworkAccessible + " packet sent to client " + sender);
			} else {
				NetworkActionResult result = (NetworkActionResult)reader.ReadByte();
				int networkID = reader.ReadInt32();

				Report(true, MessageType.SecurityNetworkAccessible + " packet received by client " + Main.myPlayer);

				SecuritySystem.ReportNetworkResult(result, NetworkReportCategory.Access);

				SecuritySystem.HandleNetworkAccessibilityOnAccess(result, Main.LocalPlayer, networkID);

				Report(false, $"  Result: {result}");
			}
		}

		public static void RequestSecurityNetworkChange(int networkID, string newName, string newPassword, bool? newRestricted) {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			if (newPassword is not null)
				Main.LocalPlayer.GetModPlayer<SecurityPlayer>().RememberPassword(networkID, newPassword);

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.SecurityNetworkModification);
			packet.Write(networkID);

			BitsByte flags = new BitsByte(newName is not null, newPassword is not null, newRestricted is not null);
			if (newRestricted is bool restricted)
				flags[3] = restricted;

			packet.Write(flags);

			if (newName is not null)
				packet.Write(newName);
			if (newPassword is not null)
				packet.Write(newPassword);

			packet.Send();
		}

		public static void ReceiveSecurityNetworkChange(BinaryReader reader, int sender) {
			if (Main.netMode == NetmodeID.Server) {
				int networkID = reader.ReadInt32();
				BitsByte flags = reader.ReadByte();

				string newName = flags[0] ? reader.ReadString() : null;
				string newPassword = flags[1] ? reader.ReadString() : null;
				bool? newRestricted = flags[2] ? flags[3] : null;

				Report(true, MessageType.SecurityNetworkModification + " packet received by server from client " + sender);

				var result = SecuritySystem.ServerModifyNetwork(sender, networkID, newName, newPassword, newRestricted, out bool passwordChanged, out bool privacyChanged);

				if (result.IsSuccess()) {
					bool outdatedAuthorization = passwordChanged || privacyChanged;
					var network = SecuritySystem.GetNetwork(networkID);

					foreach (var player in Main.ActivePlayers) {
						bool isOwner = player.GetModPlayer<SecurityPlayer>().UniqueID == network.creatorID;
						SecuritySystem.HandleNetworkAccessibilityOnModification(result, player, network, outdatedAuthorization, isOwner);
					}
				}

				// Inform all clients of the result
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.SecurityNetworkModification);
				packet.Write((byte)result);
				packet.Write(networkID);
				BitsByte responseFlags = new(passwordChanged, privacyChanged);
				if (result.IsSuccess())
					responseFlags[2] = SecuritySystem.GetNetwork(networkID).restricted;
				packet.Write(responseFlags);
				packet.Write((byte)sender);
				packet.Send();

				Report(true, MessageType.SecurityNetworkModification + " packet sent to all clients");
			} else {
				NetworkActionResult result = (NetworkActionResult)reader.ReadByte();
				int networkID = reader.ReadInt32();
				BitsByte flags = reader.ReadByte();
				int requestingPlayer = reader.ReadByte();

				if (requestingPlayer == Main.myPlayer)
					SecuritySystem.ReportNetworkResult(result, NetworkReportCategory.Modification);

				// If the password or restricted status was changed, the client's authorization status is outdated
				var network = SecuritySystem.GetNetwork(networkID);
				if (result.IsSuccess() && flags[1])
					network = new SecuritySystem.NetworkView(network.creator, network.creatorID, network.name, flags[2], network.id);
				bool isOwner = Main.LocalPlayer.GetModPlayer<SecurityPlayer>().UniqueID == network.creatorID;
				SecuritySystem.HandleNetworkAccessibilityOnModification(result, Main.LocalPlayer, network, flags[0] || flags[1], isOwner);
				if (!result.IsSuccess() && requestingPlayer == Main.myPlayer && flags[0])
					Main.LocalPlayer.GetModPlayer<SecurityPlayer>().ForgetPassword(networkID);

				Report(true, MessageType.SecurityNetworkModification + " packet received by client " + Main.myPlayer);

				Report(false, $"  Result: {result}");

				RequestSecurityNetworkList();
			}
		}

		public static void RequestSecurityNetworkList() {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.RequestSecurityNetworkList);
			packet.Send();

			Report(true, MessageType.RequestSecurityNetworkList + " packet sent to the server");
		}

		public static void ReceiveSecurityNetworkList(BinaryReader reader, int sender) {
			if (Main.netMode == NetmodeID.Server) {
				Report(true, MessageType.RequestSecurityNetworkList + " packet received by server from client " + sender);

				// Inform the client of the result
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.RequestSecurityNetworkList);
				SecuritySystem.SyncClientNetworkViews(packet);
				packet.Send(sender);

				if (sender == -1)
					Report(true, MessageType.RequestSecurityNetworkList + " packet sent to all clients");
				else
					Report(true, MessageType.RequestSecurityNetworkList + " packet sent to client " + sender);
			} else {
				SecuritySystem.ReceiveClientNetworkViews(reader);

				ReportNetworkList();

				Report(false, MessageType.RequestSecurityNetworkList + " packet received by client " + Main.myPlayer);
			}
		}

		[Conditional("NETPLAY")]
		private static void ReportNetworkList() {
			var networks = SecuritySystem.GetNetworks().ToList();

			StringBuilder sb = new($"Security list updated with {networks.Count} networks:\n");
			foreach (var network in networks)
				sb.Append("  ").Append(network.name).Append(" (ID: ").Append(network.id).AppendLine(")");

			Report(true, sb.ToString());
		}

		public static void ReceiveSecurityPlayerSync(BinaryReader reader, int sender) {
			byte plr = reader.ReadByte();
			if (plr >= Main.maxPlayers)
				return;

			SecurityPlayer mp = Main.player[plr].GetModPlayer<SecurityPlayer>();
			mp.ReceiveSync(reader);
		}

		public static void ServerReceiveSecurityPlayerSyncRequest(int sender) {
			if (Main.netMode != NetmodeID.Server)
				return;

			for (int i = 0; i < Main.maxPlayers; i++) {
				if (i != sender && Main.player[i].active)
					Main.player[i].GetModPlayer<SecurityPlayer>().SyncPlayer(sender, -1, false);
			}

			SecurityPlayer player = Main.player[sender].GetModPlayer<SecurityPlayer>();
			player.SyncPlayer(sender, -1, false);
			player.SyncPlayer(-1, sender, false);
		}

		public static void SyncStorageComponentNetwork(TEStorageComponent component) {
			if (Main.netMode != NetmodeID.Server)
				return;

			// Inform all clients of the result
			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.StorageHeartNetwork);
			packet.Write(component.Position);
			packet.Write(component.assignedNetwork);

			packet.Send();

			Report(true, MessageType.StorageHeartNetwork + " packet sent to all clients");
		}

		public static void ReceiveStorageComponentNetwork(BinaryReader reader, int sender) {
			Point16 componentPosition = reader.ReadPoint16();
			int networkID = reader.ReadInt32();

			if (TileEntity.ByPosition.TryGetValue(componentPosition, out TileEntity te) && te is TEStorageComponent component)
				component.assignedNetwork = networkID;

			if (Main.netMode == NetmodeID.Server) {
				Report(true, MessageType.StorageHeartNetwork + " packet received by server from client " + sender);
				return;
			}

			// Checking for the security UI shouldn't be necessary, since components will set to a network either
			//   via the heart (which is handled by another netcode packet) or when destroying the heart (which would
			//   close the security UI anyway)
			/*
			Point16 viewing = Main.LocalPlayer.GetModPlayer<StoragePlayer>().ViewingStorage();

			if (viewing == componentPosition && MagicUI.IsSecurityUIOpen()) {
				// The security UI will need to update
				SecuritySystem.clientListDirty = true;
			}
			*/

			Report(true, $"Component at position {componentPosition} was assigned to network {networkID}");

			Report(false, MessageType.StorageHeartNetwork + " packet received by client " + Main.myPlayer);
		}

		public static void RequestStorageHeartNetworkAssignment(Player player, TEStorageHeart heart, int networkID) {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			if (!SecuritySystem.CanPlayerAccessImmediately(player, heart.assignedNetwork)
			|| !SecuritySystem.CanPlayerAccessImmediately(player, networkID))
				return;

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.StorageHeartNetworkAssignment);
			packet.Write(heart.Position);
			packet.Write(networkID);
			packet.Send();
		}

		public static void ReceiveStorageHeartNetworkAssignmentRequest(BinaryReader reader, int sender) {
			if (Main.netMode == NetmodeID.Server) {
				Point16 heartPosition = reader.ReadPoint16();
				int networkID = reader.ReadInt32();

				Report(true, MessageType.StorageHeartNetworkAssignment + " packet received by server from client " + sender);

				NetworkActionResult result = SecuritySystem.ServerAssignNetwork(sender, heartPosition, networkID);

				// Inform all clients of the result
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.StorageHeartNetworkAssignment);
				packet.Write((byte)result);
				packet.Write((byte)sender);
				packet.Write(heartPosition);  // The location of the heart needs to be sent again in case the client is viewing its security list
				packet.Send();

				Report(true, MessageType.StorageHeartNetworkAssignment + " packet sent to all clients");
			} else if (Main.netMode == NetmodeID.MultiplayerClient) {
				NetworkActionResult result = (NetworkActionResult)reader.ReadByte();
				int requestingPlayer = reader.ReadByte();
				Point16 heartPosition = reader.ReadPoint16();

				Report(true, $"Attempted to modify security network for Storage Heart at position {heartPosition} by client {requestingPlayer} (result: {result})");

				if (requestingPlayer == Main.myPlayer)
					SecuritySystem.ReportNetworkResult(result, NetworkReportCategory.NetworkChange);

				// Shouldn't be necessary, since the UI will listen for the action result
				/*
				if (result.IsSuccess() && StoragePlayer.LocalPlayer.GetStorageHeart() is TEStorageHeart heart && heart.Position == heartPosition && MagicUI.IsStorageUIOpen()) {
					// The security UI will need to update
					SecuritySystem.clientListDirty = true;
				}
				*/

				Report(false, MessageType.StorageHeartNetworkAssignment + " packet received by client " + Main.myPlayer);
			}
		}

		public static void RequestAccessibleNetworksByDefault() {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.DefaultAccessibleNetworks);
			packet.Send();
		}

		public static void RecieveAccessibleNetworksByDefaultRequest(BinaryReader reader, int sender) {
			if (Main.netMode == NetmodeID.Server) {
				Report(true, MessageType.DefaultAccessibleNetworks + " packet received by server from client " + sender);

				NetworkActionResult result = SecuritySystem.ServerDefaultAccessibleNetworks(sender, out var networks);

				// Inform the client of the result
				ModPacket packet = MagicStorageMod.Instance.GetPacket();
				packet.Write((byte)MessageType.DefaultAccessibleNetworks);
				packet.Write((byte)result);

				if (result.IsSuccess()) {
					SecurityPlayer securityPlayer = Main.player[sender].GetModPlayer<SecurityPlayer>();

					packet.Write(networks.Length);

					if (networks.Length > 0) {
						foreach (var network in networks) {
							packet.Write(network.id);

							// Ensure that the server's player instance is able to access the network
							securityPlayer.JoinNetwork(network.id);
						}
					}
				}
				
				packet.Send(toClient: sender);

				Report(true, MessageType.DefaultAccessibleNetworks + " packet sent to client " + sender);
			} else if (Main.netMode == NetmodeID.MultiplayerClient) {
				NetworkActionResult result = (NetworkActionResult)reader.ReadByte();

				if (result.IsSuccess()) {
					SecurityPlayer securityPlayer = Main.LocalPlayer.GetModPlayer<SecurityPlayer>();

					int count = reader.ReadInt32();

					Report(true, count + " networks were available by default" + (count > 0 ? ":" : ""));

					for (int i = 0; i < count; i++) {
						int id = reader.ReadInt32();
						securityPlayer.JoinNetwork(id);
						Report(false, "  ID: " + id);
					}
				}

				Report(!result.IsSuccess(), MessageType.DefaultAccessibleNetworks + " packet received by client " + Main.myPlayer);
			}
		}

		public static void ReceiveNetworkPasswordRequest(BinaryReader reader, int sender) {
			if (Main.netMode == NetmodeID.Server) {
				int networkID = reader.ReadInt32();

				Report(true, MessageType.SecurityNetworkPassword + " packet received by server from client " + sender);

				if (!AuditSystem.IsAdministratorSender(sender) || !SecuritySystem.NetworkExists(networkID))
					Report(true, $"Rejected password request for network {networkID} from player {sender}");
				else
					Report(true, $"Ignored deprecated password request for network {networkID} from administrator {sender}");
			}
		}

		public static void ReceivePityDropsPlayerSync(BinaryReader reader, int sender) {
			int packetPlayer = reader.ReadByte();
			int plr = InboundPacketGuard.ResolvePlayer(packetPlayer, sender, Main.netMode);
			if (plr < 0 || plr >= Main.maxPlayers)
				return;

			PityLootDrops mp = Main.player[plr].GetModPlayer<PityLootDrops>();
			mp.ReceiveSync(reader);

			if (Main.netMode == NetmodeID.Server) {
				// Forward the result
				mp.SyncPlayer(-1, sender, false);
			}
		}

		public static void RequestStorageDepositHistoryChunks(TEStorageHeart heart) {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			heart.ClearDepositHistory();
			heart.requestingDepositHistory = true;

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.ClientRequestDepositHistoryChunks);
			packet.Write(heart.Position);
			packet.Send();

			Report(true, MessageType.ClientRequestDepositHistoryChunks + " packet sent to the server");
		}

		public static void ServerReceiveDepositHistoryChunksRequest(BinaryReader reader, int sender) {
			if (Main.netMode != NetmodeID.Server)
				return;

			Point16 position = reader.ReadPoint16();
			if (position.ResolveToTileEntity() is not TEStorageHeart heart)
				return;

			heart.SendDepositHistoryChunks();

			Report(true, MessageType.ClientRequestDepositHistoryChunks + " packet received by server from client " + sender);
		}

		public static void ClientReceiveDepositHistoryChunk(BinaryReader reader) {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			Point16 position = reader.ReadPoint16();
			if (position.ResolveToTileEntity() is not TEStorageHeart heart)
				return;

			heart.ReceiveDepositHistoryChunk(reader);
		}

		public static void SendDepositHistoryUpdate(TEStorageHeart heart, int[] additions, int[] removals) {
			if (Main.netMode != NetmodeID.Server)
				return;

			ModPacket packet = MagicStorageMod.Instance.GetPacket();
			packet.Write((byte)MessageType.UpdateDepositHistory);
			packet.Write(heart.Position);

			if (additions is { Length: >0 }) {
				packet.Write7BitEncodedInt(additions.Length);
				foreach (int index in additions)
					packet.Write7BitEncodedInt(index);
			} else
				packet.Write((byte)0);

			if (removals is { Length: >0 }) {
				packet.Write7BitEncodedInt(removals.Length);
				foreach (int index in removals)
					packet.Write7BitEncodedInt(index);
			} else
				packet.Write((byte)0);

			packet.Send();
		}

		public static void ClientReceiveDepositHistoryUpdate(BinaryReader reader) {
			if (Main.netMode != NetmodeID.MultiplayerClient)
				return;

			Point16 position = reader.ReadPoint16();
			int additionsCount = reader.Read7BitEncodedInt();
			
			int[] additions;
			if (additionsCount > 0) {
				additions = new int[additionsCount];
				for (int i = 0; i < additionsCount; i++)
					additions[i] = reader.Read7BitEncodedInt();
			} else
				additions = [];

			int removalsCount = reader.Read7BitEncodedInt();
			int[] removals;
			if (removalsCount > 0) {
				removals = new int[removalsCount];
				for (int i = 0; i < removalsCount; i++)
					removals[i] = reader.Read7BitEncodedInt();
			} else
				removals = [];

			if (position.ResolveToTileEntity() is not TEStorageHeart heart)
				return;

			heart.UpdateDepositHistory(additions, removals);
		}
	}

	internal enum MessageType : byte
	{
		SearchAndRefreshNetwork,
		ClientStorageOperation,
		ServerStorageResult,
		RefreshNetworkItems,
		ClientStorageComponentOperation,
		ClientSendDeactivate,
		ClientStationOperation,
		ServerStationOperationResult,
		ResetCompactStage,
		CraftRequest,
		CraftResult,
		SectionRequest,
		SyncStorageUnitToClient,
		SyncStorageUnit,
		ForceCraftingGUIRefresh,
		TransferItems,
		RequestCoinCompact,
		MassDuplicateSellRequest,
		MassDuplicateSellResult,
		RequestStorageUnitStyle,
		ServerQuickStackToStorageResult,
		GolemHelpTextUpdate,
		ClientRequestServerOp,
		ServerOpResponse,
		ClientRequestServerOpConfirmation,
		ServerOpConfirmationResult,
		PlayerHasServerOp,
		ClientRequestPlayerBankDeposit,
		PlayerBankDepositResult,
		ComponentPlacement,
		ComponentDestruction,
		ClientLockStorageHeart,
		ClientUnlockStorageHeart,
		DeleteSpecificItem,
		RequestShimmerItemInStorage,
		ShimmerItemInStorageResult,
		RenameStorageHeart,
		SyncDepositHistory,
		ClientSendCoreRemoval,
		ClientSendCoreInsertion,
		SecurityNetworkCreation,
		SecurityNetworkRemoval,
		SecurityNetworkJoin,
		SecurityNetworkAccessible,
		SecurityNetworkModification,
		RequestSecurityNetworkList,
		SecurityPlayerSync,
		StorageHeartNetwork,
		StorageHeartNetworkAssignment,
		DefaultAccessibleNetworks,
		SecurityNetworkPassword,
		AuditSystemMessage,
		SyncPityDropsPlayer,
		ClientRequestDepositHistoryChunks,
		ServerResponseDepositHistoryChunks,
		UpdateDepositHistory,
		ClientRequestPlayerOperatorChange,
		RequestSecurityPlayerSync,
		CraftOutcome
	}

	internal enum StorageComponentOperation : byte {
		RemoteAccessLink,
		EnvironmentModuleToggle
	}

	internal enum CraftRejectionReason : byte {
		None,
		InvalidRequest,
		RecipeMismatch,
		AccessDenied,
		QueueFull,
		Unavailable,
		StateChanged,
		Cancelled,
		InternalError
	}
}

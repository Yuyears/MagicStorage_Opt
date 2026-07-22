using System;
using MagicStorage.Components;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;

namespace MagicStorage.Common.Systems {
	[Flags]
	internal enum PacketDirection : byte {
		None = 0,
		ClientToServer = 1,
		ServerToClient = 2,
		Bidirectional = ClientToServer | ServerToClient
	}

	internal static class InboundPacketGuard {
		internal const int MaxItemEntries = 4096;
		internal const int MaxBankEntries = 256;

		internal static PacketDirection GetDirection(MessageType type) => type switch {
			MessageType.SearchAndRefreshNetwork
			or MessageType.ClinetStorageOperation
			or MessageType.ClientSendTEUpdate
			or MessageType.ClientSendDeactivate
			or MessageType.ClientStationOperation
			or MessageType.ResetCompactStage
			or MessageType.CraftRequest
			or MessageType.SectionRequest
			or MessageType.SyncStorageUnit
			or MessageType.TransferItems
			or MessageType.RequestCoinCompact
			or MessageType.MassDuplicateSellRequest
			or MessageType.RequestStorageUnitStyle
			or MessageType.ClientRequestServerOp
			or MessageType.ClientRequestServerOpConfirmation
			or MessageType.ClientRequestPlayerBankDeposit
			or MessageType.ComponentPlacement
			or MessageType.ComponentDestruction
			or MessageType.DeleteSpecificItem
			or MessageType.RequestShimmerItemInStorage
			or MessageType.ClientSendCoreRemoval
			or MessageType.ClientSendCoreInsertion
			or MessageType.ClientRequestDepositHistoryChunks => PacketDirection.ClientToServer,

			MessageType.ServerStorageResult
			or MessageType.RefreshNetworkItems
			or MessageType.ServerStationOperationResult
			or MessageType.CraftResult
			or MessageType.SyncStorageUnitToClinet
			or MessageType.MassDuplicateSellResult
			or MessageType.ServerQuickStackToStorageResult
			or MessageType.GolemHelpTextUpdate
			or MessageType.ServerOpResponse
			or MessageType.ServerOpConfirmationResult
			or MessageType.PlayerBankDepositResult
			or MessageType.StorageHeartNetwork
			or MessageType.ServerResponseDepositHistoryChunks
			or MessageType.UpdateDepositHistory => PacketDirection.ServerToClient,

			MessageType.ForceCraftingGUIRefresh
			or MessageType.PlayerHasServerOp
			or MessageType.ClientLockStorageHeart
			or MessageType.ClientUnlockStorageHeart
			or MessageType.RenameStorageHeart
			or MessageType.SyncDepositHistory
			or MessageType.SecurityNetworkCreation
			or MessageType.SecurityNetworkRemoval
			or MessageType.SecurityNetworkJoin
			or MessageType.SecurityNetworkAccessible
			or MessageType.SecurityNetworkModification
			or MessageType.RequestSecurityNetworkList
			or MessageType.SecurityPlayerSync
			or MessageType.StorageHeartNetworkAssignment
			or MessageType.DefaultAccessibleNetworks
			or MessageType.SecurityNetworkPassword
			or MessageType.AuditSystemMessage
			or MessageType.SyncPityDropsPlayer => PacketDirection.Bidirectional,

			_ => PacketDirection.None
		};

		internal static bool IsDirectionAllowed(MessageType type, int netMode) {
			PacketDirection direction = GetDirection(type);
			return direction != PacketDirection.None && netMode switch {
				NetmodeID.Server => direction.HasFlag(PacketDirection.ClientToServer),
				NetmodeID.MultiplayerClient => direction.HasFlag(PacketDirection.ServerToClient),
				NetmodeID.SinglePlayer => true,
				_ => false
			};
		}

		internal static bool Accept(MessageType type, int sender) {
			if (!IsDirectionAllowed(type, Main.netMode)) {
				NetHelper.Report(true, $"Rejected {type}: invalid direction for net mode {Main.netMode}");
				return false;
			}

			if (Main.netMode == NetmodeID.Server) {
				if (!IsValidTransportSender(sender, Main.maxPlayers)) {
					NetHelper.Report(true, $"Rejected {type}: invalid sender {sender}");
					return false;
				}
			}

			return true;
		}

		internal static bool IsValidTransportSender(int sender, int maxPlayers) => sender >= 0 && sender < maxPlayers;

		internal static bool IsValidSender(int sender, int maxPlayers, bool active) => IsValidTransportSender(sender, maxPlayers) && active;

		internal static bool IsValidCount(int count, int maximum) => count >= 0 && count <= maximum;

		internal static bool IsWithinTileRange(Point playerCenter, Point16 target, int rangeX, int rangeY)
			=> playerCenter.X >= target.X - rangeX
			&& playerCenter.X <= target.X + rangeX + 1
			&& playerCenter.Y >= target.Y - rangeY
			&& playerCenter.Y <= target.Y + rangeY + 1;

		internal static bool TryGetStorageEntity<T>(Point16 position, int sender, out T entity, out TEStorageHeart heart, bool requireInteractionRange = false)
			where T : TEStorageComponent {
			entity = null;
			heart = null;

			bool active = sender >= 0 && sender < Main.maxPlayers && Main.player[sender]?.active == true;
			if (!IsValidSender(sender, Main.maxPlayers, active)
			|| !TileEntity.ByPosition.TryGetValue(position, out TileEntity tileEntity)
			|| tileEntity is not T storageEntity
			|| storageEntity.GetHeart() is not TEStorageHeart storageHeart)
				return false;

			Player player = Main.player[sender];
			if (!SecuritySystem.CanPlayerAccessImmediately(player, storageHeart.assignedNetwork)
			|| requireInteractionRange && !IsWithinTileRange(player.Center.ToTileCoordinates(), position, player.lastTileRangeX, player.lastTileRangeY))
				return false;

			entity = storageEntity;
			heart = storageHeart;
			return true;
		}
	}
}

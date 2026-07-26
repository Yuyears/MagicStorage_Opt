using MagicStorage.Common.IO;
using MagicStorage.Common.Systems;
using MagicStorage.Common.Systems.Auditing;
using MagicStorage.Components;
using MagicStorage.Items;
using MagicStorage.Items.ErrorDisplay;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.Default;
using Terraria.ModLoader.IO;

namespace MagicStorage.Common.Commands {
	internal sealed class SerializationVerificationCommand : ModCommand {
		public override string Command => "msverifyserialization";

		public override CommandType Type => CommandType.Chat | CommandType.Console;

		public override string Usage => this.GetUsageText();

		public override string Description => "Run deterministic compressed-item round-trip checks.";

		public override void Action(CommandCaller caller, string input, string[] args) {
			if (args.Length != 0) {
				caller.Reply($"Usage: {Usage}", Color.Red);
				return;
			}

			Item taggedLocator = new(ModContent.ItemType<Locator>()) { favorited = true };
			((Locator)taggedLocator.ModItem).Location = new(123, 456);
			DeserializedNetItem failedMetadata = new() { type = ItemID.StoneBlock, stack = 37, prefix = PrefixID.Keen, favorite = true };
			Item errorPlaceholder = Utility.PrepareFailureItem(BaseErrorDummyItem.NetReadFailItemType, failedMetadata.ToTagData(), failedMetadata);

			(string name, List<Item> items)[] fixtures = [
				("empty", []),
				("single", [new Item(ItemID.StoneBlock, 37) { favorited = true }]),
				("multi", [new Item(ItemID.DirtBlock, 1), new Item(ItemID.Torch, 73), new Item(ItemID.MagicMirror, 1) { favorited = true }]),
				("modded-tagged", [taggedLocator]),
				("error-placeholder", [errorPlaceholder])
			];

			List<string> failures = [];
			int totalChecks = fixtures.Length;
			foreach ((string name, List<Item> items) in fixtures) {
				try {
					int bytes = VerifyRoundTrip(items);
					caller.Reply($"Serialization fixture '{name}' passed ({bytes} bytes).", Color.LightGreen);
				}
				catch (Exception ex) {
					failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
					MagicStorageMod.Instance.Logger.Error($"Serialization fixture '{name}' failed", ex);
				}
			}

			RunCheck("nested scopes", VerifyNestedScopes, failures, caller);
			RunCheck("malformed metadata recovery", VerifyMalformedMetadataRecovery, failures, caller);
			RunCheck("signed integer boundaries", VerifySignedIntegerBoundaries, failures, caller);
			RunCheck("bit buffer boundaries", VerifyBitBufferBoundaries, failures, caller);
			RunCheck("length tier boundaries", VerifyLengthTierBoundaries, failures, caller);
			RunCheck("compressed string boundaries", VerifyStringBoundaries, failures, caller);
			RunCheck("string scrambling boundaries", VerifyStringScramblingBoundaries, failures, caller);
			RunCheck("component persistence keys", VerifyComponentPersistenceKeys, failures, caller);
			RunCheck("audit record isolation", VerifyAuditRecordIsolation, failures, caller);
			RunCheck("legacy audit password migration", VerifyLegacyAuditPasswordMigration, failures, caller);
			RunCheck("unloaded item withdrawal identity", VerifyUnloadedItemWithdrawalIdentity, failures, caller);
			totalChecks += 11;

			if (failures.Count == 0)
				caller.Reply($"Serialization verification passed: {totalChecks}/{totalChecks} checks.", Color.LightGreen);
			else
				caller.Reply($"Serialization verification failed: {totalChecks - failures.Count}/{totalChecks} checks passed.\n{string.Join("\n", failures)}", Color.Red);
		}

		private static int VerifyRoundTrip(List<Item> expected) {
			byte[] serialized = Serialize(expected);
			using MemoryStream stream = new(serialized, writable: false);

			List<Item> actual;
			using (BinaryReader reader = new(stream))
				actual = SaveCompression.LoadItems(reader);

			if (actual.Count != expected.Count)
				throw new InvalidOperationException($"Expected {expected.Count} items, received {actual.Count}.");

			for (int i = 0; i < expected.Count; i++) {
				Item left = expected[i];
				Item right = actual[i];
				if (right.type != left.type || right.stack != left.stack || right.prefix != left.prefix || right.favorited != left.favorited)
					throw new InvalidOperationException($"Item {i} changed: expected type={left.type}, stack={left.stack}, prefix={left.prefix}, favorite={left.favorited}; received type={right.type}, stack={right.stack}, prefix={right.prefix}, favorite={right.favorited}.");

				if (left.ModItem is BaseErrorDummyItem expectedError
				&& (right.ModItem is not BaseErrorDummyItem actualError
					|| actualError.OriginalMod != expectedError.OriginalMod
					|| actualError.OriginalName != expectedError.OriginalName
					|| actualError.OriginalPrefix != expectedError.OriginalPrefix
					|| !TagCompoundComparer.SemanticallyEquals(actualError.data, expectedError.data)))
					throw new InvalidOperationException($"Error placeholder {i} lost its original item data.");
			}

			if (!serialized.AsSpan().SequenceEqual(Serialize(actual)))
				throw new InvalidOperationException("Re-encoding the decoded fixture changed its bytes.");

			return serialized.Length;
		}

		private static byte[] Serialize(List<Item> items) {
			using MemoryStream stream = new();
			using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
				SaveCompression.SaveItems(items, writer);
			return stream.ToArray();
		}

		private static void VerifyNestedScopes() {
			using MemoryStream stream = new();
			ValueWriter writer = new(stream);
			using (writer.CreateScope(NetCompression.lengthTiers, optimizeForBytes: false)) {
				writer.Write(true);
				using (writer.CreateScope(NetCompression.lengthTiers, optimizeForBytes: false))
					writer.Write((byte)0x5a, 8);
				using (writer.CreateScope(NetCompression.lengthTiers, optimizeForBytes: false)) { }
				writer.Write(false);
			}
			writer.Flush();

			stream.Position = 0;
			ValueReader reader = new(new BinaryReader(stream));
			using (reader.ReadScope(NetCompression.lengthTiers, optimizeForBytes: false)) {
				if (!reader.ReadBoolean())
					throw new InvalidOperationException("Outer scope prefix changed.");
				using (reader.ReadScope(NetCompression.lengthTiers, optimizeForBytes: false)) {
					if (reader.ReadByte(8) != 0x5a)
						throw new InvalidOperationException("Nested scope payload changed.");
				}
				using (reader.ReadScope(NetCompression.lengthTiers, optimizeForBytes: false)) { }
				if (reader.ReadBoolean())
					throw new InvalidOperationException("Outer scope suffix changed.");
			}
		}

		private static void VerifyMalformedMetadataRecovery() {
			using MemoryStream stream = new();
			ValueWriter writer = new(stream);
			StackCompressor stackCompressor = new();
			writer.Write((uint)stackCompressor.CommonMaxStack, BitBuffer128.MAX_INT - 1);
			new ItemContentNameLookup().SaveTo(writer);
			new GenericKeyLookup().SaveTo(writer);
			using (writer.CreateScope(NetCompression.lengthTiers, optimizeForBytes: false)) { }
			writer.Flush();

			stream.Position = 0;
			Item recovered = SaveCompression.LoadItem(new BinaryReader(stream));
			if (recovered.ModItem is not BaseErrorDummyItem)
				throw new InvalidOperationException("Malformed metadata did not produce an error item.");
		}

		private static void VerifySignedIntegerBoundaries() {
			using MemoryStream stream = new();
			ValueWriter writer = new(stream);
			writer.Write(int.MinValue, BitBuffer128.MAX_INT);
			writer.Write(int.MaxValue, BitBuffer128.MAX_INT);
			writer.Write(true);
			writer.Write(long.MinValue, BitBuffer128.MAX_LONG);
			writer.Write(long.MaxValue, BitBuffer128.MAX_LONG);
			writer.Flush();

			stream.Position = 0;
			ValueReader reader = new(new BinaryReader(stream));
			if (reader.ReadInt32(BitBuffer128.MAX_INT) != int.MinValue
			|| reader.ReadInt32(BitBuffer128.MAX_INT) != int.MaxValue
			|| !reader.ReadBoolean()
			|| reader.ReadInt64(BitBuffer128.MAX_LONG) != long.MinValue
			|| reader.ReadInt64(BitBuffer128.MAX_LONG) != long.MaxValue)
				throw new InvalidOperationException("A full-width signed integer changed during round trip.");
		}

		private static void VerifyBitBufferBoundaries() {
			for (int start = 1; start < 8; start++) {
				for (byte width = 57; width <= 64; width++) {
					BitBuffer128 buffer = new();
					int head = 0;
					for (int bit = 0; bit < start; bit++)
						buffer.Set((bit & 1) != 0, ref head);

					const ulong expected = 0xD6A5_9C3B_F078_4E21UL;
					buffer.Set(expected, ref head, width);
					for (int bit = 0; bit < start; bit++) {
						if (buffer.GetBoolean(ref head) != ((bit & 1) != 0))
							throw new InvalidOperationException($"Bit buffer prefix changed at start={start}, width={width}.");
					}

					ulong mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
					if (buffer.GetUInt64(ref head, width) != (expected & mask))
						throw new InvalidOperationException($"Bit buffer value changed at start={start}, width={width}.");
				}
			}
		}

		private static void VerifyLengthTierBoundaries() {
			uint[] expected = [0, 15, 16, 335, 336, 4431, 4432, 135503, 135504, uint.MaxValue];
			if (NetCompression.lengthTiers.GetBitCost(4432) != 47)
				throw new InvalidOperationException("Escaped length reported an incorrect bit cost.");

			using MemoryStream stream = new();
			ValueWriter writer = new(stream);
			foreach (uint value in expected)
				NetCompression.lengthTiers.WriteTo(writer, value);
			writer.Flush();

			stream.Position = 0;
			ValueReader reader = new(new BinaryReader(stream));
			foreach (uint value in expected) {
				uint actual = NetCompression.lengthTiers.ReadFrom(reader);
				if (actual != value)
					throw new InvalidOperationException($"Length tier changed {value} to {actual}.");
			}
		}

		private static void VerifyStringBoundaries() {
			VerifyStringRoundTrip(new string('x', 2049));
			VerifyStringRoundTrip(new string('\uffff', ushort.MaxValue));

			try {
				StringCompressor.WriteTo(new ValueWriter(Stream.Null), new string('x', ushort.MaxValue + 1));
			} catch (ArgumentOutOfRangeException) {
				return;
			}

			throw new InvalidOperationException("An oversized compressed string was accepted.");
		}

		private static void VerifyStringRoundTrip(string expected) {
			using MemoryStream stream = new();
			ValueWriter writer = new(stream);
			StringCompressor.WriteTo(writer, expected);
			writer.Flush();
			stream.Position = 0;
			string actual = StringCompressor.ReadFrom(new ValueReader(new BinaryReader(stream)));
			if (actual != expected)
				throw new InvalidOperationException($"Compressed string length {expected.Length} changed during round trip.");
		}

		private static void VerifyStringScramblingBoundaries() {
			char[] values = new char[ushort.MaxValue + 1];
			for (int i = 0; i < values.Length; i++)
				values[i] = (char)i;

			string expected = new(values);
			if (StringScrambling.Unscramble(StringScrambling.Scramble(expected)) != expected)
				throw new InvalidOperationException("Scrambling did not round-trip all UTF-16 values.");

			try {
				StringScrambling.Unscramble([0]);
			} catch (ArgumentException) {
				return;
			}

			throw new InvalidOperationException("Odd-length scrambled data was accepted.");
		}

		private static void VerifyUnloadedItemWithdrawalIdentity() {
			VerifyCompressedTagIntegers();
			VerifyWithdrawal(amount: 2, expectedRemainingStack: 1, expectedIdentityCount: 2);
			VerifyWithdrawal(amount: 3, expectedRemainingStack: 0, expectedIdentityCount: 1);

			static void VerifyCompressedTagIntegers() {
				Item expected = CreateUnloadedItem("IntegerBoundaries", 1);
				UnloadedItem expectedData = (UnloadedItem)expected.ModItem;
				expectedData.data["shortAboveTinyRange"] = (short)14;
				expectedData.data["shortBelowTinyRange"] = (short)-9;
				expectedData.data["intAboveTinyRange"] = 137;
				expectedData.data["intBelowTinyRange"] = -129;
				expectedData.data["longAboveTinyRange"] = 4096L;
				expectedData.data["longBelowTinyRange"] = -2049L;

				using MemoryStream stream = new(Serialize([expected]), writable: false);
				using BinaryReader reader = new(stream);
				Item actual = SaveCompression.LoadItems(reader).Single();
				if (actual.ModItem is not UnloadedItem actualData)
					throw new InvalidOperationException("Compressed tag integer fixture did not remain an unloaded item.");
				if (!TagCompoundComparer.SemanticallyEquals(expectedData.data, actualData.data))
					throw new InvalidOperationException($"Compressed tag integers changed value: {TagCompoundComparer.DescribeFirstDifference(expectedData.data, actualData.data)}");
			}

			static void VerifyWithdrawal(int amount, int expectedRemainingStack, int expectedIdentityCount) {
				Item requestedItem = CreateUnloadedItem("RequestedItem", 3);
				Item otherItem = CreateUnloadedItem("OtherItem", 5);
				Item sameNameDifferentData = CreateUnloadedItem("RequestedItem", 7);
				((UnloadedItem)sameNameDifferentData.ModItem).data["variant"] = 2;
				List<Item> stored = [requestedItem, otherItem, sameNameDifferentData];
				int totalBefore = stored.Sum(static item => item.stack);

				Item request = RoundTripNetItem(requestedItem);
				request.stack = amount;
				UnloadedItem requestData = (UnloadedItem)request.ModItem;
				if (requestData.ModName != "MissingMod" || requestData.ItemName != "RequestedItem")
					throw new InvalidOperationException($"ItemIO changed unloaded identity to '{requestData.ModName}/{requestData.ItemName}'.");
				requestData.data = ReverseTag(requestData.data);
				if (!TagCompoundComparer.SemanticallyEquals(requestData.data, ((UnloadedItem)requestedItem.ModItem).data)
				|| TagCompoundComparer.SemanticallyEquals(requestData.data, ((UnloadedItem)sameNameDifferentData.ModItem).data))
					throw new InvalidOperationException("Tag semantic comparison did not preserve identity across a network round trip.");

				if (!TEStorageUnit.WithdrawFromItemCollection(stored, request, out Item withdrawn))
					throw new InvalidOperationException("No semantically matching unloaded item was found.");
				if (withdrawn.stack != amount)
					throw new InvalidOperationException($"Withdrawal returned stack {withdrawn.stack}, expected {amount}.");
				if (withdrawn.ModItem is not UnloadedItem unloaded || unloaded.ItemName != "RequestedItem")
					throw new InvalidOperationException("Withdrawal returned the wrong unloaded item identity.");
				if (!TagCompoundComparer.SemanticallyEquals(unloaded.data, requestData.data))
					throw new InvalidOperationException($"Withdrawal returned different tag data: {TagCompoundComparer.DescribeFirstDifference(requestData.data, unloaded.data)}");

				if (stored.Sum(static item => item.stack) != totalBefore - amount
				|| stored.Count != expectedIdentityCount + 1
				|| stored.Where(static item => ((UnloadedItem)item.ModItem).ItemName == "RequestedItem").Sum(static item => item.stack) != expectedRemainingStack
					+ sameNameDifferentData.stack
				|| stored.Where(static item => ((UnloadedItem)item.ModItem).ItemName == "OtherItem").Sum(static item => item.stack) != 5
				|| sameNameDifferentData.stack != 7)
					throw new InvalidOperationException("Withdrawing an unloaded item changed the wrong stored identity or quantity.");
			}

			static TagCompound ReverseTag(TagCompound source) {
				TagCompound reversed = [];
				foreach ((string key, object value) in source.Reverse())
					reversed[key] = value is TagCompound nested ? ReverseTag(nested) : value;
				return reversed;
			}

			static Item RoundTripNetItem(Item item) {
				using MemoryStream stream = new();
				using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
					ItemIO.Send(item, writer, true, true);
				stream.Position = 0;
				using BinaryReader reader = new(stream);
				return ItemIO.Receive(reader, true, true);
			}
		}

		private static Item CreateUnloadedItem(string itemName, int stack) {
			Item item = new(ModContent.ItemType<UnloadedItem>(), stack);
			UnloadedItem unloaded = (UnloadedItem)item.ModItem;
			TagCompound data = new() {
				["identity"] = itemName,
				["variant"] = 1,
				["nested"] = new TagCompound {
					["enabled"] = true,
					["value"] = 42L
				},
				["values"] = new List<int> { 1, 2, 3 },
				["bytes"] = new byte[] { 4, 5, 6 }
			};
			unloaded.Setup(new TagCompound {
				["mod"] = "MissingMod",
				["name"] = itemName,
				["data"] = data
			});
			item.stack = stack;
			return item;
		}

		private static void VerifyComponentPersistenceKeys() {
			Point16[] expected = [new(123, 456), new(321, 654)];
			TEStorageHeart center = new();

			VerifyLoad("locations");
			VerifyLoad("components");

			void VerifyLoad(string key) {
				TagCompound tag = new() {
					["components"] = new TagCompound {
						[key] = expected.ToList()
					}
				};

				center.ComponentManager.Load(tag);
				if (!center.ComponentManager.GetAllComponents().SequenceEqual(expected))
					throw new InvalidOperationException($"Component locations were not restored from '{key}'.");

				TagCompound saved = [];
				center.ComponentManager.Save(saved);
				TagCompound data = saved.GetCompound("components");
				if (!data.GetList<Point16>("locations").SequenceEqual(expected))
					throw new InvalidOperationException("Component locations were not saved under 'locations'.");
			}
		}

		private static void VerifyAuditRecordIsolation() {
			using MemoryStream stream = new();
			using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true)) {
				writer.Write(AuditFile.FileMagic);
				writer.Write(AuditFile.CurrentVersion);
				WriteEmptyAuditTables(writer);
				writer.Write(3);
				WriteAuditEntryFrame(writer, new StatusAdministratorAssignment());

				using (MemoryStream malformed = new()) {
					using (BinaryWriter recordWriter = new(malformed, System.Text.Encoding.UTF8, leaveOpen: true)) {
						recordWriter.Write((byte)AuditAction.SecurityNetworkModification);
						recordWriter.Write(DateTime.UtcNow.ToBinary());
						recordWriter.Write((ushort)0);
						recordWriter.Write(7);
						recordWriter.Write(new BitsByte(false, false, false, true, false));
						recordWriter.Write7BitEncodedInt(AuditFile.MaxPasswordBytes + 1);
					}

					writer.Write(checked((int)malformed.Length));
					writer.Write(malformed.GetBuffer(), 0, checked((int)malformed.Length));
				}

				WriteAuditEntryFrame(writer, new StatusOperatorRemoval());
			}

			stream.Position = 0;
			AuditFile file = new();
			AuditFile.DeserializeOne(new BinaryReader(stream), ref file);
			AuditAction[] actions = file.Entries.Select(static entry => entry.Action).ToArray();
			if (!actions.SequenceEqual([AuditAction.StatusServerAdmin, AuditAction.StatusServerOperatorRemoved]))
				throw new InvalidOperationException("A corrupt audit record removed or misaligned valid records.");
		}

		private static void VerifyLegacyAuditPasswordMigration() {
			const string previousPassword = "old";
			const string currentPassword = "newer";

			using MemoryStream stream = new();
			using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true)) {
				WriteEmptyAuditTables(writer);
				writer.Write(1);
				writer.Write((byte)AuditAction.SecurityNetworkModification);
				writer.Write(DateTime.UtcNow.ToBinary());
				writer.Write((ushort)0);
				writer.Write(7);
				writer.Write(new BitsByte(false, true, true, true, true));
				WriteLegacyAuditPassword(writer, previousPassword);
				WriteLegacyAuditPassword(writer, currentPassword);
			}

			stream.Position = 0;
			AuditFile file = new();
			AuditFile.DeserializeOne(new BinaryReader(stream), ref file);
			if (file.Entries.SingleOrDefault() is not SecurityNetworkModification modification
			|| modification.PreviousPassword is not null
			|| modification.CurrentPassword != "[REDACTED]")
				throw new InvalidOperationException("Legacy audit passwords were not migrated to redacted values.");
		}

		private static void WriteEmptyAuditTables(BinaryWriter writer) {
			writer.Write((ushort)0);
			writer.Write((ushort)0);
			writer.Write((ushort)0);
		}

		private static void WriteAuditEntryFrame(BinaryWriter writer, AuditEntry entry) {
			using MemoryStream stream = new();
			using (BinaryWriter recordWriter = new(stream, System.Text.Encoding.UTF8, leaveOpen: true)) {
				recordWriter.Write((byte)entry.Action);
				entry.Serialize(recordWriter);
			}

			writer.Write(checked((int)stream.Length));
			writer.Write(stream.GetBuffer(), 0, checked((int)stream.Length));
		}

		private static void WriteLegacyAuditPassword(BinaryWriter writer, string password) {
			writer.Write7BitEncodedInt(password.Length);
			writer.Write(StringScrambling.Scramble(password));
		}

		private static void RunCheck(string name, Action check, List<string> failures, CommandCaller caller) {
			try {
				check();
				caller.Reply($"Serialization check '{name}' passed.", Color.LightGreen);
			} catch (Exception ex) {
				failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
				MagicStorageMod.Instance.Logger.Error($"Serialization check '{name}' failed", ex);
			}
		}
	}
}

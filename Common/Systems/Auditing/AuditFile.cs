using System;
using System.Collections.Generic;
using System.IO;

namespace MagicStorage.Common.Systems.Auditing {
	internal class AuditFile : IAuditable<AuditFile> {
		internal const ulong FileMagic = 0x31454C494654534D;
		internal const byte CurrentVersion = 1;
		internal const int MaxPasswordBytes = 64 * 1024;
		private const int MaxEntryBytes = 1024 * 1024;
		private const int MaxEntryCount = 1_000_000;

		private AuditPlayerTable _playerTable = new();
		private AuditItemTable _itemTable = new();
		private AuditComponentTable _componentTable = new();

		private int _lastCount;
		private readonly List<AuditEntry> _entries = [];

		internal AuditPlayerTable Players => _playerTable;

		internal AuditItemTable Items => _itemTable;

		internal AuditComponentTable Components => _componentTable;

		public bool HasChanges => _lastCount != _entries.Count;

		public int EntryCount => _entries.Count;

		public IEnumerable<AuditEntry> Entries => _entries.AsReadOnly();

		internal void ForceNoChanges() => _lastCount = _entries.Count;

		public void AddEntry(AuditEntry entry) {
			entry.Source = this;
			entry.EvaluateParameters();
			_entries.Add(entry);
		}

		public void ClearEverything() {
			_playerTable.Clear();
			_itemTable.Clear();
			_componentTable.Clear();

			_entries.Clear();
			_lastCount = 0;
		}

		public static void DeserializeOne<T>(BinaryReader reader, ref T instance) where T : AuditFile {
			try {
				byte version = ReadVersion(reader);

				NetHelper.Report(false, "[AUDIT]   Deserializing player table...");
				AuditPlayerTable.DeserializeOne(reader, ref instance._playerTable);

				NetHelper.Report(false, "[AUDIT]   Deserializing item table...");
				AuditItemTable.DeserializeOne(reader, ref instance._itemTable);

				NetHelper.Report(false, "[AUDIT]   Deserializing component table...");
				AuditComponentTable.DeserializeOne(reader, ref instance._componentTable);

				int count = reader.ReadInt32();
				if (count < 0 || count > MaxEntryCount)
					throw new InvalidDataException($"Audit entry count {count} is outside the supported range.");

				NetHelper.Report(false, $"[AUDIT]   Deserializing {count} audit entries...");

				instance._entries.Clear();
				if (version == 0)
					instance.DeserializeLegacyEntries(reader, count);
				else
					instance.DeserializeFramedEntries(reader, count);
			} catch (Exception ex) {
				MagicStorageMod.Instance.Logger.Error("Failed to deserialize audit file", ex);
			} finally {
				instance._lastCount = instance._entries.Count;
			}
		}

		private static byte ReadVersion(BinaryReader reader) {
			Stream stream = reader.BaseStream;
			if (!stream.CanSeek || stream.Length - stream.Position < sizeof(ulong))
				return 0;

			long start = stream.Position;
			if (reader.ReadUInt64() != FileMagic) {
				stream.Position = start;
				return 0;
			}

			byte version = reader.ReadByte();
			if (version != CurrentVersion)
				throw new InvalidDataException($"Unsupported audit file version {version}.");

			return version;
		}

		private void DeserializeLegacyEntries(BinaryReader reader, int count) {
			for (int i = 0; i < count; i++) {
				try {
					AddDeserializedEntry(DeserializeEntry(reader, legacy: true));
				} catch (Exception ex) {
					MagicStorageMod.Instance.Logger.Error($"Failed to deserialize legacy audit entry {i}; later unframed entries cannot be recovered", ex);
					break;
				}
			}
		}

		private void DeserializeFramedEntries(BinaryReader reader, int count) {
			for (int i = 0; i < count; i++) {
				int length = reader.ReadInt32();
				if (length <= 0)
					throw new InvalidDataException($"Audit entry {i} has invalid length {length}.");

				if (length > MaxEntryBytes) {
					SkipBytes(reader, length);
					MagicStorageMod.Instance.Logger.Error($"Skipped audit entry {i} because its length {length} exceeds the {MaxEntryBytes}-byte limit.");
					continue;
				}

				byte[] data = reader.ReadBytes(length);
				if (data.Length != length)
					throw new EndOfStreamException($"Audit entry {i} declared {length} bytes but only {data.Length} were available.");

				try {
					using MemoryStream stream = new(data, writable: false);
					using BinaryReader entryReader = new(stream);
					AuditEntry entry = DeserializeEntry(entryReader, legacy: false);
					if (stream.Position != stream.Length)
						throw new InvalidDataException($"Audit entry left {stream.Length - stream.Position} unread bytes.");

					AddDeserializedEntry(entry);
				} catch (Exception ex) {
					MagicStorageMod.Instance.Logger.Error($"Skipped corrupt audit entry {i}", ex);
				}
			}
		}

		private static void SkipBytes(BinaryReader reader, int count) {
			Stream stream = reader.BaseStream;
			if (!stream.CanSeek || stream.Length - stream.Position < count)
				throw new EndOfStreamException($"Unable to skip {count} bytes for an oversized audit entry.");

			stream.Position += count;
		}

		private void AddDeserializedEntry(AuditEntry entry) {
			NetHelper.Report(false, $"[AUDIT]     {entry.NetRepresentation()}");
			_entries.Add(entry);
		}

		private AuditEntry DeserializeEntry(BinaryReader reader, bool legacy) {
			AuditAction action = (AuditAction)reader.ReadByte();

			AuditEntry entry = action switch {
				AuditAction.DepositOne => new DepositOne(),
				AuditAction.DepositMany => new DepositMany(),
				AuditAction.WithdrawOne => new WithdrawOne(),
				AuditAction.WithdrawMany => new WithdrawMany(),
				AuditAction.UnitDeactivate => new StorageUnitDeactivation(),
				AuditAction.UnitActivate => new StorageUnitActivation(),
				AuditAction.UnitCoreRemove => new StorageUnitCoreRemoval(),
				AuditAction.UnitCoreInsert => new StorageUnitCoreInsertion(),
				AuditAction.SellItems => new StorageControlSellItems(),
				AuditAction.DestroyItem => new StorageControlDeleteItem(),
				AuditAction.CraftRequest => new CraftRequest(),
				AuditAction.ControlDeleteUnloadedItems => new StorageControlDeleteUnloadedItems(),
				AuditAction.ControlDeleteUnloadedData => new StorageControlDeleteUnloadedData(),
				AuditAction.LinkRemoteAccess => new LinkRemoteAccess(),
				AuditAction.LinkPortableAccess => new LinkPortableAccess(),
				AuditAction.SecurityNetworkAssignment => new SecurityNetworkAssignment(),
				AuditAction.SecurityNetworkModification => new SecurityNetworkModification(),
				AuditAction.SecurityNetworkDelete => new SecurityNetworkDeletion(),
				AuditAction.SecurityNetworkJoin => new SecurityNetworkJoin(),
				AuditAction.ControlCompactCoins => new StorageControlCompactCoins(),
				AuditAction.StatusServerAdmin => new StatusAdministratorAssignment(),
				AuditAction.StatusServerOperatorGranted => new StatusOperatorAssignment(),
				AuditAction.StatusServerOperatorRemoved => new StatusOperatorRemoval(),
				_ => throw new ArgumentOutOfRangeException($"Audit action ID ({action}) was outside the range of expected values"),
			};

			entry.Source = this;
			if (legacy && entry is SecurityNetworkModification modification)
				modification.DeserializeLegacy(reader);
			else
				entry.Deserialize(reader);
			entry.EvaluateParameters();

			return entry;
		}

		public void Serialize(BinaryWriter writer) {
			try {
				writer.Write(FileMagic);
				writer.Write(CurrentVersion);

				NetHelper.Report(false, "[AUDIT]   Serializing player table...");
				_playerTable.Serialize(writer);

				NetHelper.Report(false, "[AUDIT]   Serializing item table...");
				_itemTable.Serialize(writer);

				NetHelper.Report(false, "[AUDIT]   Serializing component table...");
				_componentTable.Serialize(writer);

				NetHelper.Report(false, $"[AUDIT]   Serializing {_entries.Count} audit entries...");

				writer.Write(_entries.Count);
				foreach (AuditEntry entry in _entries) {
					// Ensure that the entry is ready for serialization
					entry.EvaluateParameters();

					NetHelper.Report(false, $"[AUDIT]     {entry.NetRepresentation()}");

					using MemoryStream stream = new();
					using (BinaryWriter entryWriter = new(stream, System.Text.Encoding.UTF8, leaveOpen: true)) {
						entryWriter.Write((byte)entry.Action);
						entry.Serialize(entryWriter);
					}

					int length = checked((int)stream.Length);
					if (length > MaxEntryBytes)
						throw new InvalidDataException($"Audit entry {entry.Action} exceeds the {MaxEntryBytes}-byte limit.");

					writer.Write(length);
					writer.Write(stream.GetBuffer(), 0, length);
				}
			} catch (Exception ex) {
				MagicStorageMod.Instance.Logger.Error("Failed to serialize audit file", ex);
			} finally {
				_lastCount = _entries.Count;
			}
		}
	}
}

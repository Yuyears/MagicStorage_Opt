using System;
using System.IO;
using Terraria;
using Terraria.ModLoader;

namespace MagicStorage.Common.Systems.Auditing {
	internal interface IWriteInterceptor {
		void Flush();
		void WriteLine();
		void WriteLine(string text);
	}

	internal class StreamWriterInterceptor(StreamWriter writer) : IWriteInterceptor {
		private readonly StreamWriter _writer = writer;

		void IWriteInterceptor.Flush() => _writer.Flush();
		void IWriteInterceptor.WriteLine() => _writer.WriteLine();
		void IWriteInterceptor.WriteLine(string text) => _writer.WriteLine(text);
	}

	internal class PacketInterceptor(int bufferLength, string newline, int toClient, int requestID) : IWriteInterceptor {
		private readonly char[] _buffer = new char[bufferLength];
		private readonly string _newline = newline;
		private readonly int _toClient = toClient;
		private readonly int _requestID = requestID;
		private int _head;
		private int _currentBuffer;
		private ModPacket _activePacket;

		void IWriteInterceptor.Flush() {
			if (_head > 0)
				SendPacket();

			InitPacket(AuditSystem.COMMAND_FILE_CONTENT_END);
			_activePacket.Write((ushort)_currentBuffer);
			_activePacket.Write(Path.GetFileNameWithoutExtension(Main.ActiveWorldFileData.Path));
			_activePacket.Send(toClient: _toClient);
		}

		void IWriteInterceptor.WriteLine() => AddToBuffer(_newline);

		void IWriteInterceptor.WriteLine(string text) => AddToBuffer(text + _newline);

		private void AddToBuffer(string text) {
			ReadOnlySpan<char> buffer = text;
			while (!buffer.IsEmpty) {
				if (_activePacket is null)
					InitPacket(AuditSystem.COMMAND_FILE_CONTENT);

				int maxLength = _buffer.Length - _head;
				if (buffer.Length <= maxLength) {
					buffer.CopyTo(_buffer.AsSpan()[_head..]);
					_head += buffer.Length;
					break;
				}

				buffer[..maxLength].CopyTo(_buffer.AsSpan()[_head..]);
				buffer = buffer[maxLength..];
				_head = _buffer.Length;
				SendPacket();
			}
		}

		private void InitPacket(byte command) {
			_activePacket = MagicStorageMod.Instance.GetPacket();
			_activePacket.Write((byte)MessageType.AuditSystemMessage);
			_activePacket.Write(command);
		}

		private void SendPacket() {
			_activePacket.Write(_requestID);
			_activePacket.Write((ushort)_currentBuffer);
			_activePacket.Write((ushort)_head);
			_activePacket.Write(_buffer.AsSpan()[.._head]);
			_activePacket.Send(toClient: _toClient);
			_activePacket = null;
			_head = 0;
			_currentBuffer++;
		}
	}
}

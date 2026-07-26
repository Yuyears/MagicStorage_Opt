using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MagicStorage.Common.IO {
	public class LengthCompressor<T> where T : INumberBase<T>, IBinaryNumber<T>, IMinMaxValue<T> {
		private readonly EncodingTier<T>[] _tiers;
		private readonly EncodingTier<T>? _escapeTier;
		private readonly T _escapeValue;

		internal readonly byte _minPrefixBits;
		internal readonly byte _maxPrefixBits;

		public LengthCompressor(params EncodingTier<T>[] tiers) {
			_tiers = tiers;

			_minPrefixBits = byte.MaxValue;
			_maxPrefixBits = 0;

			foreach (var tier in tiers) {
				if (tier.PrefixBitCount < _minPrefixBits)
					_minPrefixBits = tier.PrefixBitCount;
				if (tier.PrefixBitCount > _maxPrefixBits)
					_maxPrefixBits = tier.PrefixBitCount;
			}
		}

		internal LengthCompressor(EncodingTier<T> escapeTier, EncodingTier<T>[] tiers) : this(tiers) {
			_escapeTier = escapeTier;
			_escapeValue = escapeTier.Minimum + T.CreateChecked((BigInteger.One << escapeTier.BitCount) - BigInteger.One);
		}

		public void WriteTo(ValueWriter writer, T value) {
			if (_escapeTier is { } escapeTier && value > escapeTier.Maximum) {
				escapeTier.WriteTo(writer, _escapeValue);
				EncodingTier<T>.WriteBits(writer, value, (byte)(Unsafe.SizeOf<T>() * 8));
				return;
			}

			foreach (var tier in _tiers) {
				if (value >= tier.Minimum && value <= tier.Maximum) {
					tier.WriteTo(writer, value);
					return;
				}
			}

			throw new ArgumentOutOfRangeException(nameof(value), $"Value {value} is out of range for this LengthCompressor");
		}

		public T ReadFrom(ValueReader reader) {
			byte prefix = reader.ReadByte(_minPrefixBits);

			for (int size = _minPrefixBits; size <= _maxPrefixBits; size++) {
				foreach (var tier in _tiers) {
					if (tier.PrefixBitCount == size && tier.Prefix == prefix) {
						T value = tier.ReadFrom(reader);
						if (_escapeTier is { } escapeTier && tier == escapeTier && value == _escapeValue) {
							value = EncodingTier<T>.ReadBits(reader, (byte)(Unsafe.SizeOf<T>() * 8));
							if (value <= escapeTier.Maximum)
								throw new InvalidOperationException("Escaped length must exceed the legacy tier maximum");
						}

						return value;
					}
				}

				// IMPORTANT: Reading bits goes from LSB to MSB
				prefix = (byte)(prefix | (reader.ReadByte(1) << size));
			}

			throw new InvalidOperationException("No matching encoding tier found for the given prefix in the data stream");
		}

		public int GetBitCost(T value) {
			if (_escapeTier is { } escapeTier && value > escapeTier.Maximum)
				return escapeTier.PrefixBitCount + escapeTier.BitCount + Unsafe.SizeOf<T>() * 8;

			foreach (var tier in _tiers) {
				if (value >= tier.Minimum && value <= tier.Maximum)
					return tier.PrefixBitCount + tier.BitCount;
			}

			throw new ArgumentOutOfRangeException(nameof(value), $"Value {value} is out of range for this LengthCompressor");
		}
	}

	public readonly record struct EncodingTier<T>(byte Prefix, byte PrefixBitCount, byte BitCount, T Minimum, T Maximum)
		where T : INumberBase<T>, IBinaryNumber<T>, IMinMaxValue<T>
	{
		public EncodingTier<T> CreateSuccessive(byte prefix, byte prefixBitCount, T size) {
			return EncodingTier.Create(prefix, prefixBitCount, minimum: this.Maximum + T.One, size);
		}

		public EncodingTier<T> CreateSuccessiveUnbounded(byte prefix, byte prefixBitCount) {
			return EncodingTier.CreateUnbounded(prefix, prefixBitCount, minimum: this.Maximum + T.One);
		}

		public EncodingTier<T> CreateNewPrefix(byte prefix, byte prefixBitCount) {
			return new EncodingTier<T>(prefix, prefixBitCount, this.BitCount, this.Minimum, this.Maximum);
		}

		public void WriteTo(ValueWriter writer, T value) {
			writer.Write(Prefix, PrefixBitCount);

			value -= Minimum;
			WriteBits(writer, value, BitCount);
		}

		internal static void WriteBits(ValueWriter writer, T value, byte bitCount) {
			if (typeof(T) == typeof(byte))
				writer.Write(Unsafe.As<T, byte>(ref value), bitCount);
			else if (typeof(T) == typeof(sbyte))
				writer.Write((byte)Unsafe.As<T, sbyte>(ref value), bitCount);
			else if (typeof(T) == typeof(ushort))
				writer.Write(Unsafe.As<T, ushort>(ref value), bitCount);
			else if (typeof(T) == typeof(short))
				writer.Write((ushort)Unsafe.As<T, short>(ref value), bitCount);
			else if (typeof(T) == typeof(uint))
				writer.Write(Unsafe.As<T, uint>(ref value), bitCount);
			else if (typeof(T) == typeof(int))
				writer.Write((uint)Unsafe.As<T, int>(ref value), bitCount);
			else if (typeof(T) == typeof(ulong))
				writer.Write(Unsafe.As<T, ulong>(ref value), bitCount);
			else if (typeof(T) == typeof(long))
				writer.Write((ulong)Unsafe.As<T, long>(ref value), bitCount);
			else
				throw new NotSupportedException($"Unsupported type: {typeof(T)}");
		}

		public T ReadFrom(ValueReader reader) {
			return ReadBits(reader, BitCount) + Minimum;
		}

		internal static T ReadBits(ValueReader reader, byte bitCount) {
			T value;
			if (typeof(T) == typeof(byte)) {
				byte read = reader.ReadByte(bitCount);
				value = Unsafe.As<byte, T>(ref read);
			} else if (typeof(T) == typeof(sbyte)) {
				sbyte read = (sbyte)reader.ReadByte(bitCount);
				value = Unsafe.As<sbyte, T>(ref read);
			} else if (typeof(T) == typeof(ushort)) {
				ushort read = reader.ReadUInt16(bitCount);
				value = Unsafe.As<ushort, T>(ref read);
			} else if (typeof(T) == typeof(short)) {
				short read = (short)reader.ReadUInt16(bitCount);
				value = Unsafe.As<short, T>(ref read);
			} else if (typeof(T) == typeof(uint)) {
				uint read = reader.ReadUInt32(bitCount);
				value = Unsafe.As<uint, T>(ref read);
			} else if (typeof(T) == typeof(int)) {
				int read = (int)reader.ReadUInt32(bitCount);
				value = Unsafe.As<int, T>(ref read);
			} else if (typeof(T) == typeof(ulong)) {
				ulong read = reader.ReadUInt64(bitCount);
				value = Unsafe.As<ulong, T>(ref read);
			} else if (typeof(T) == typeof(long)) {
				long read = (long)reader.ReadUInt64(bitCount);
				value = Unsafe.As<long, T>(ref read);
			} else
				throw new NotSupportedException($"Unsupported type: {typeof(T)}");

			return value;
		}
	}

	internal static class EncodingTier {
		public static EncodingTier<T> Create<T>(byte prefix, byte prefixBitCount, T minimum, T size)
			where T : INumberBase<T>, IBinaryNumber<T>, IMinMaxValue<T>
		{
			if (!T.IsPow2(size))
				throw new ArgumentException("Size must be a power of two", nameof(size));

			int bitCount = NetCompression.GetBitSize(size);

			return new EncodingTier<T>(prefix, prefixBitCount, (byte)bitCount, minimum, minimum + size - T.One);
		}

		public static EncodingTier<T> CreateZero<T>(byte prefix, byte prefixBitCount, T size)
			where T : INumberBase<T>, IBinaryNumber<T>, IMinMaxValue<T>
		{
			return Create(prefix, prefixBitCount, T.Zero, size);
		}

		public static EncodingTier<T> CreateUnbounded<T>(byte prefix, byte prefixBitCount, T minimum)
			where T : INumberBase<T>, IBinaryNumber<T>, IMinMaxValue<T>
		{
			int bitCount = NetCompression.GetBitSize(T.MaxValue);

			return new EncodingTier<T>(prefix, prefixBitCount, (byte)bitCount, minimum, T.MaxValue);
		}

		public static EncodingTier<T> CreateZeroUnbounded<T>(byte prefix, byte prefixBitCount)
			where T : INumberBase<T>, IBinaryNumber<T>, IMinMaxValue<T>
		{
			return CreateUnbounded(prefix, prefixBitCount, T.Zero);
		}
	}
}

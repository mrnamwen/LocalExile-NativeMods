using System.Buffers.Binary;
using System.Text;

namespace LocalExile.Client.AssetPipeline.Dat64;

/// <summary>
/// Non-destructive <c>.datc64</c> patcher: preserves every existing row and variable-section offset byte-for-byte
/// while appending brand-new rows, UTF-16LE strings (with PoE's 4-byte double-NUL terminator), and typed arrays.
/// </summary>
public sealed class Dat64TablePatcher
{
    private readonly int _originalRowCount;
    private readonly int _rowStride;
    private readonly List<byte> _fixedRows;
    private readonly List<byte> _variableSection;
    private int _rowCount;
    private ulong? _cachedEmptyStringOffset;

    public int RowCount => _rowCount;
    public int RowStride => _rowStride;
    public int OriginalRowCount => _originalRowCount;

    public Dat64TablePatcher(Dat64Table source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.RowStride <= 0)
        {
            throw new InvalidOperationException("Cannot append rows to a .datc64 table with RowStride == 0.");
        }

        _originalRowCount = source.RowCount;
        _rowCount = source.RowCount;
        _rowStride = source.RowStride;

        _fixedRows = new List<byte>(source.FixedRows.Length + (_rowStride * 4));
        _fixedRows.AddRange(source.FixedRows.Span);

        _variableSection = new List<byte>(source.VariableSection.Length + 1024);
        _variableSection.AddRange(source.VariableSection.Span);
    }

    /// <summary>
    /// Creates a mutable byte buffer initialized from an existing row so unknown/default columns stay valid.
    /// </summary>
    public byte[] CloneRow(int templateRowIndex)
    {
        if ((uint)templateRowIndex >= (uint)_rowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(templateRowIndex), $"Template row {templateRowIndex} out of range (RowCount={_rowCount}).");
        }

        byte[] copy = new byte[_rowStride];
        _fixedRows.CopyTo(templateRowIndex * _rowStride, copy, 0, _rowStride);
        return copy;
    }

    /// <summary>
    /// Appends a UTF-16LE string terminated by 4 zero bytes (<c>0x0000 0x0000</c>), matching real 3.29.3d <c>.datc64</c> tables.
    /// Returns its byte offset relative to the start of the variable-data sentinel.
    /// </summary>
    public ulong AppendString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0 && _cachedEmptyStringOffset.HasValue)
        {
            return _cachedEmptyStringOffset.Value;
        }

        // Align string offsets to 2 bytes for clean UTF-16LE reads.
        if ((_variableSection.Count & 1) != 0)
        {
            _variableSection.Add(0);
        }

        ulong offset = (ulong)_variableSection.Count;
        if (value.Length > 0)
        {
            _variableSection.AddRange(Encoding.Unicode.GetBytes(value));
        }

        // PoE .datc64 strings are terminated by 4 zero bytes (double UTF-16LE NUL).
        _variableSection.Add(0);
        _variableSection.Add(0);
        _variableSection.Add(0);
        _variableSection.Add(0);

        if (value.Length == 0)
        {
            _cachedEmptyStringOffset = offset;
        }

        return offset;
    }

    /// <summary>
    /// Appends a little-endian <c>i32[]</c> array to the variable section and returns <c>(count, offset)</c>.
    /// </summary>
    public (ulong Count, ulong Offset) AppendInt32Array(ReadOnlySpan<int> values)
    {
        if (values.IsEmpty)
        {
            return (0UL, (ulong)_variableSection.Count);
        }

        ulong offset = (ulong)_variableSection.Count;
        Span<byte> buf = stackalloc byte[4];
        foreach (int v in values)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf, v);
            _variableSection.AddRange(buf);
        }

        return ((ulong)values.Length, offset);
    }

    /// <summary>
    /// Appends a <c>foreignrow[]</c> array (16 bytes per entry: <c>(rowIndex, 0)</c>) to the variable section.
    /// </summary>
    public (ulong Count, ulong Offset) AppendForeignRowArray(ReadOnlySpan<ulong> targetRowIndices)
    {
        if (targetRowIndices.IsEmpty)
        {
            return (0UL, (ulong)_variableSection.Count);
        }

        ulong offset = (ulong)_variableSection.Count;
        Span<byte> buf = stackalloc byte[16];
        foreach (ulong idx in targetRowIndices)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buf[..8], idx);
            BinaryPrimitives.WriteUInt64LittleEndian(buf[8..16], idx == Dat64Table.NullForeignRow ? Dat64Table.NullForeignRow : 0UL);
            _variableSection.AddRange(buf);
        }

        return ((ulong)targetRowIndices.Length, offset);
    }

    /// <summary>
    /// Appends a complete fixed-size row and returns its new 0-based row index.
    /// </summary>
    public int AppendRow(ReadOnlySpan<byte> rowBytes)
    {
        if (rowBytes.Length != _rowStride)
        {
            throw new ArgumentException($"Row length {rowBytes.Length} does not match table RowStride {_rowStride}.", nameof(rowBytes));
        }

        int newIndex = _rowCount;
        _fixedRows.AddRange(rowBytes);
        _rowCount++;
        return newIndex;
    }

    public void SetString(Span<byte> row, int columnOffset, string value)
    {
        ulong offset = AppendString(value);
        BinaryPrimitives.WriteUInt64LittleEndian(row.Slice(columnOffset, 8), offset);
    }

    public void SetInt32Array(Span<byte> row, int columnOffset, ReadOnlySpan<int> values)
    {
        var (count, offset) = AppendInt32Array(values);
        WriteArrayHeader(row, columnOffset, count, offset);
    }

    public void SetForeignRowArray(Span<byte> row, int columnOffset, ReadOnlySpan<ulong> rowIndices)
    {
        var (count, offset) = AppendForeignRowArray(rowIndices);
        WriteArrayHeader(row, columnOffset, count, offset);
    }

    public static void WriteBool(Span<byte> row, int columnOffset, bool value) =>
        row[columnOffset] = value ? (byte)1 : (byte)0;

    public static void WriteUInt16(Span<byte> row, int columnOffset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(columnOffset, 2), value);

    public static void WriteInt32(Span<byte> row, int columnOffset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(row.Slice(columnOffset, 4), value);

    public static void WriteUInt32(Span<byte> row, int columnOffset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(columnOffset, 4), value);

    public static void WriteForeignRow(Span<byte> row, int columnOffset, ulong? targetRowIndex)
    {
        ulong first = targetRowIndex ?? Dat64Table.NullForeignRow;
        ulong second = targetRowIndex.HasValue ? 0UL : Dat64Table.NullForeignRow;
        BinaryPrimitives.WriteUInt64LittleEndian(row.Slice(columnOffset, 8), first);
        BinaryPrimitives.WriteUInt64LittleEndian(row.Slice(columnOffset + 8, 8), second);
    }

    public static void WriteArrayHeader(Span<byte> row, int columnOffset, ulong count, ulong varOffset)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(row.Slice(columnOffset, 8), count);
        BinaryPrimitives.WriteUInt64LittleEndian(row.Slice(columnOffset + 8, 8), varOffset);
    }

    /// <summary>
    /// Finalizes the patched table and performs a full binary round-trip verification.
    /// </summary>
    public Dat64Table BuildAndVerify()
    {
        var candidate = new Dat64Table(_rowCount, _rowStride, _fixedRows.ToArray(), _variableSection.ToArray());
        byte[] serialized = candidate.Serialize();
        Dat64Table roundTripped = Dat64Table.Parse(serialized);

        if (roundTripped.RowCount != _rowCount || roundTripped.RowStride != _rowStride)
        {
            throw new InvalidOperationException(
                $"Dat64 round-trip mismatch: expected ({_rowCount} rows, stride {_rowStride}), got ({roundTripped.RowCount} rows, stride {roundTripped.RowStride}).");
        }

        if (!roundTripped.FixedRows.Span.SequenceEqual(candidate.FixedRows.Span) ||
            !roundTripped.VariableSection.Span.SequenceEqual(candidate.VariableSection.Span))
        {
            throw new InvalidOperationException("Dat64 round-trip byte comparison failed.");
        }

        return roundTripped;
    }
}

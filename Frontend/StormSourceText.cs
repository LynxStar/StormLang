using System;
using System.Collections.Generic;

namespace Storm.SourceGenerator.Frontend;

/// <summary>
/// Lightweight wrapper around a source file's text content.
/// Provides efficient offset-to-line/column conversion by lazily
/// caching line-start offsets.
/// </summary>
public sealed class StormSourceText
{
    private int[]? lineStarts;

    /// <summary>
    /// Creates a new <see cref="StormSourceText"/> instance.
    /// </summary>
    /// <param name="filePath">The file path (may be null for in-memory sources).</param>
    /// <param name="contents">The full text content of the source.</param>
    public StormSourceText(string? filePath, string contents)
    {
        FilePath = filePath;
        Contents = contents ?? throw new ArgumentNullException(nameof(contents));
    }

    /// <summary>The file path of this source, or null for in-memory sources.</summary>
    public string? FilePath { get; }

    /// <summary>The full text content.</summary>
    public string Contents { get; }

    /// <summary>The length of the source text.</summary>
    public int Length => Contents.Length;

    /// <summary>
    /// Computes the 1-based line and 1-based column for the given zero-based offset.
    /// </summary>
    /// <param name="offset">A zero-based character offset into <see cref="Contents"/>.</param>
    /// <returns>A tuple of (Line, Column), both 1-based.</returns>
    public (int Line, int Column) GetLineAndColumn(int offset)
    {
        if(offset < 0 || offset > Contents.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        var lineStarts = GetLineStarts();

        // Binary search for the line containing this offset.
        var lo = 0;
        var hi = lineStarts.Length - 1;
        while(lo < hi)
        {
            var mid = lo + (hi - lo + 1) / 2;
            if(lineStarts[mid] <= offset)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        var line = lo + 1;           // 1-based
        var column = offset - lineStarts[lo] + 1; // 1-based
        return (line, column);
    }

    /// <summary>
    /// Returns the lazily-computed array of line-start offsets.
    /// Index 0 is always 0 (the start of the first line).
    /// </summary>
    private int[] GetLineStarts()
    {
        if(lineStarts != null)
        {
            return lineStarts;
        }

        var starts = new List<int> { 0 };
        for(var i = 0; i < Contents.Length; i++)
        {
            var c = Contents[i];
            if(c == '\n')
            {
                starts.Add(i + 1);
            }
            else if(c == '\r')
            {
                // \r\n counts as a single newline
                if(i + 1 < Contents.Length && Contents[i + 1] == '\n')
                {
                    i++;
                }

                starts.Add(i + 1);
            }
        }

        lineStarts = starts.ToArray();
        return lineStarts;
    }
}
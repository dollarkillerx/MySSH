using System.Runtime.InteropServices;
using Avalonia.Threading;
using MySSH.Terminal.Rendering;
using XTerm.Search;

namespace MySSH.Terminal;

public sealed partial class TerminalView
{
    private readonly DispatcherTimer _searchRefreshTimer;
    private readonly List<(long Row, int Col, int Len)> _hits = [];
    private readonly List<HitSpan> _rowHits = [];
    private BufferSearch? _engineSearch;
    private bool _searchActive;
    private string _searchText = string.Empty;
    private bool _searchMatchCase;
    private int _searchCurrent = -1;
    private int _searchEpoch;

    /// <summary>Number of hits of the last <see cref="FindNext"/>/<see cref="FindPrevious"/> search.</summary>
    public int SearchResultCount => _hits.Count;

    /// <summary>Index of the current hit (0-based), or -1.</summary>
    public int SearchResultIndex => _searchCurrent;

    /// <summary>
    /// Highlights every occurrence of <paramref name="text"/> in the scrollback and screen and moves to the next
    /// one below the current hit (or the first one at/below the top of the viewport), scrolling it into view.
    /// </summary>
    /// <returns>True when there is at least one hit.</returns>
    public bool FindNext(string text, bool matchCase = false) => Find(text, matchCase, forward: true);

    /// <summary>Like <see cref="FindNext"/>, moving upwards (towards older output); a new search starts from the bottom.</summary>
    public bool FindPrevious(string text, bool matchCase = false) => Find(text, matchCase, forward: false);

    /// <summary>Removes search highlights.</summary>
    public void ClearSearch()
    {
        if (!_searchActive && _hits.Count == 0)
        {
            return;
        }

        _searchActive = false;
        _searchText = string.Empty;
        _hits.Clear();
        _searchCurrent = -1;
        _searchEpoch++;
        _searchRefreshTimer?.Stop();
        _engineSearch?.Clear();
        RefreshRows();
    }

    private bool Find(string text, bool matchCase, bool forward)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            ClearSearch();
            return false;
        }

        var fresh = !_searchActive || text != _searchText || matchCase != _searchMatchCase;
        _searchText = text;
        _searchMatchCase = matchCase;
        _searchActive = true;
        if (fresh)
        {
            RerunSearch(keepCurrent: false);
        }

        if (_hits.Count == 0)
        {
            _searchCurrent = -1;
            RefreshRows();
            return false;
        }

        var buf = Engine.Buffer;
        var trimmed = buf.TrimmedLines;
        if (_searchCurrent < 0 || fresh)
        {
            if (forward)
            {
                var top = buf.YDisp + trimmed;
                var i = _hits.FindIndex(h => h.Row >= top);
                _searchCurrent = i < 0 ? 0 : i;
            }
            else
            {
                var bottom = buf.YDisp + Engine.Rows - 1 + trimmed;
                var i = _hits.FindLastIndex(h => h.Row <= bottom);
                _searchCurrent = i < 0 ? _hits.Count - 1 : i;
            }
        }
        else
        {
            _searchCurrent = forward ? (_searchCurrent + 1) % _hits.Count : (_searchCurrent - 1 + _hits.Count) % _hits.Count;
        }

        _searchEpoch++;
        RevealHit(_hits[_searchCurrent]);
        RefreshRows();
        return true;
    }

    private void RerunSearch(bool keepCurrent)
    {
        if (!_searchActive)
        {
            return;
        }

        var previous = keepCurrent && _searchCurrent >= 0 && _searchCurrent < _hits.Count ? _hits[_searchCurrent] : default;
        _hits.Clear();
        _engineSearch ??= new BufferSearch(Engine);
        _engineSearch.Find(_searchText, new SearchOptions { CaseSensitive = _searchMatchCase });
        var buf = Engine.Buffer;
        var trimmed = buf.TrimmedLines;
        var cols = Engine.Cols;
        for (var r = 0; r < buf.Lines.Length; r++)
        {
            foreach (var h in _engineSearch.HitsOnRow(r))
            {
                // a hit that runs past the right margin continues on the next (soft-wrapped) row
                var col = h.Column;
                var len = h.Cols;
                var row = (long)h.BufferRow;
                while (len > 0)
                {
                    var take = Math.Min(len, cols - col);
                    _hits.Add((row + trimmed, col, take));
                    len -= take;
                    col = 0;
                    row++;
                }
            }
        }

        _hits.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Col.CompareTo(b.Col));
        _searchCurrent = keepCurrent && previous.Len > 0 ? _hits.IndexOf(previous) : -1;
        _searchEpoch++;
        RefreshRows();
    }

    private void RevealHit((long Row, int Col, int Len) hit)
    {
        var buf = Engine.Buffer;
        var abs = hit.Row - buf.TrimmedLines;
        if (abs >= buf.YDisp && abs < buf.YDisp + Engine.Rows)
        {
            return;
        }

        var target = (int)Math.Clamp(abs - (Engine.Rows / 2), 0, buf.YBase);
        Engine.ScrollLines(target - buf.YDisp);
        _overlay.InvalidateVisual();
    }

    private int SearchStampForRow(long stableRow)
    {
        if (_hits.Count == 0)
        {
            return 0;
        }

        var i = FirstHitOnOrAfter(stableRow);
        if (i >= _hits.Count || _hits[i].Row != stableRow)
        {
            return 0;
        }

        // changes when the hit set changes or the current hit enters/leaves this row
        var current = _searchCurrent >= 0 && _searchCurrent < _hits.Count && _hits[_searchCurrent].Row == stableRow ? _searchCurrent + 1 : 0;
        return HashCode.Combine(_searchEpoch, current, _hits.Count);
    }

    /// <summary>Hits on an absolute buffer row, for the row builder (valid until the next call).</summary>
    internal ReadOnlySpan<HitSpan> HitsForRow(long absRow)
    {
        _rowHits.Clear();
        if (_hits.Count == 0)
        {
            return default;
        }

        var stable = absRow + Engine.Buffer.TrimmedLines;
        for (var i = FirstHitOnOrAfter(stable); i < _hits.Count && _hits[i].Row == stable; i++)
        {
            var h = _hits[i];
            _rowHits.Add(new HitSpan(h.Col, h.Col + h.Len, i == _searchCurrent));
        }

        return CollectionsMarshal.AsSpan(_rowHits);
    }

    private int FirstHitOnOrAfter(long stableRow)
    {
        int lo = 0, hi = _hits.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_hits[mid].Row < stableRow)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}

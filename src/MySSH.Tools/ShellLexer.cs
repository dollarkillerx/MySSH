using System.Text;

namespace MySSH.Tools;

/// <summary>One simple command: its words with quotes removed.</summary>
/// <param name="PipedFrom">The previous command's output is piped into this one (<c>a | b</c>).</param>
/// <param name="Nested">Inside a command/process substitution or subshell (<c>$(…)</c>, backticks, <c>&lt;(…)</c>, <c>(…)</c>).</param>
internal sealed record ShellSegment(IReadOnlyList<string> Words, bool PipedFrom, bool Nested);

/// <summary>
/// A best-effort POSIX-shell-ish lexer for policy heuristics (not a shell parser). Splits a command line into simple
/// commands at <c>; &amp; | &amp;&amp; || newline ( ) { }</c>, pulls command substitutions (<c>$( )</c>, backticks, also
/// inside double quotes) and process substitutions out as their own nested segments, removes quotes and escapes, drops
/// redirection targets and comments. Never throws; unbalanced input is lexed as far as it goes.
/// </summary>
internal sealed class ShellLexer
{
    /// <summary>Deeper substitutions/subshells are split as plain separators instead of recursing (bounded stack).</summary>
    private const int MaxDepth = 32;

    private readonly string _s;
    private readonly List<ShellSegment> _output = [];
    private int _i;

    private ShellLexer(string s) => _s = s;

    public static IReadOnlyList<ShellSegment> Split(string command)
    {
        var lexer = new ShellLexer(command ?? "");
        lexer.ParseList(terminator: null, depth: 0);
        return lexer._output;
    }

    private void ParseList(char? terminator, int depth)
    {
        var words = new List<string>();
        StringBuilder? word = null;
        var skipNextWord = false;
        var piped = false;
        var nextPiped = false;

        void EndWord()
        {
            if (word is null) return;
            if (skipNextWord) skipNextWord = false;
            else words.Add(word.ToString());
            word = null;
        }

        void EndSegment()
        {
            EndWord();
            skipNextWord = false;
            if (words.Count > 0)
            {
                _output.Add(new ShellSegment(words, piped, depth > 0));
                words = [];
            }
            piped = nextPiped;
            nextPiped = false;
        }

        while (_i < _s.Length)
        {
            var c = _s[_i];
            if (terminator is { } t && c == t)
            {
                _i++;
                EndSegment();
                return;
            }
            switch (c)
            {
                case ' ' or '\t':
                    EndWord();
                    _i++;
                    break;
                case '\n' or '\r' or ';':
                    EndSegment();
                    _i++;
                    break;
                case '|':
                    var isOr = _i + 1 < _s.Length && _s[_i + 1] == '|';
                    var isPipeErr = !isOr && _i + 1 < _s.Length && _s[_i + 1] == '&';
                    _i += isOr || isPipeErr ? 2 : 1;
                    nextPiped = !isOr;
                    EndSegment();
                    break;
                case '&':
                    if (_i + 1 < _s.Length && _s[_i + 1] == '>')
                    {
                        // &> file / &>> file: redirection
                        EndWord();
                        _i += 2;
                        while (_i < _s.Length && _s[_i] == '>') _i++;
                        skipNextWord = true;
                        break;
                    }
                    _i += _i + 1 < _s.Length && _s[_i + 1] == '&' ? 2 : 1;
                    EndSegment();
                    break;
                case '(':
                    EndSegment();
                    _i++;
                    if (depth < MaxDepth) ParseList(')', depth + 1);
                    break;
                case ')':
                    EndSegment();
                    _i++;
                    break;
                case '>' or '<':
                    EndWord();
                    if (c == '<' && _i + 1 < _s.Length && _s[_i + 1] == '(' && depth < MaxDepth)
                    {
                        _i += 2;
                        ParseList(')', depth + 1);
                        word ??= new StringBuilder();
                        break;
                    }
                    if (c == '>' && _i + 1 < _s.Length && _s[_i + 1] == '(' && depth < MaxDepth)
                    {
                        _i += 2;
                        ParseList(')', depth + 1);
                        break;
                    }
                    // A preceding fd number ("2>") was already emitted as a word; harmless for the heuristics.
                    _i++;
                    while (_i < _s.Length && _s[_i] is '>' or '<' or '&' or '|') _i++;
                    if (c == '<' && _i < _s.Length && _s[_i] == '<')
                        _i++;
                    skipNextWord = true;
                    break;
                case '`' when depth >= MaxDepth:
                    EndSegment();
                    _i++;
                    break;
                case '`':
                    _i++;
                    ParseList('`', depth + 1);
                    word ??= new StringBuilder();
                    break;
                case '$' when _i + 1 < _s.Length && _s[_i + 1] == '(' && depth < MaxDepth:
                    _i += 2;
                    ParseList(')', depth + 1);
                    word ??= new StringBuilder();
                    break;
                case '\'':
                {
                    word ??= new StringBuilder();
                    var end = _s.IndexOf('\'', _i + 1);
                    if (end < 0) end = _s.Length;
                    word.Append(_s, _i + 1, end - _i - 1);
                    _i = Math.Min(end + 1, _s.Length);
                    break;
                }
                case '"':
                    word ??= new StringBuilder();
                    _i++;
                    ParseDoubleQuoted(word, depth);
                    break;
                case '\\':
                    word ??= new StringBuilder();
                    if (_i + 1 < _s.Length)
                    {
                        if (_s[_i + 1] != '\n') word.Append(_s[_i + 1]); // backslash-newline = line continuation
                        _i += 2;
                    }
                    else _i++;
                    break;
                case '#' when word is null:
                    while (_i < _s.Length && _s[_i] != '\n') _i++;
                    break;
                case '{' or '}' when word is null && (_i + 1 >= _s.Length || _s[_i + 1] is ' ' or '\t' or '\n' or ';'):
                    EndSegment();
                    _i++;
                    break;
                default:
                    word ??= new StringBuilder();
                    word.Append(c);
                    _i++;
                    break;
            }
        }
        EndSegment();
    }

    private void ParseDoubleQuoted(StringBuilder word, int depth)
    {
        while (_i < _s.Length)
        {
            var c = _s[_i];
            if (c == '"')
            {
                _i++;
                return;
            }
            if (c == '\\' && _i + 1 < _s.Length && _s[_i + 1] is '$' or '`' or '"' or '\\' or '\n')
            {
                if (_s[_i + 1] != '\n') word.Append(_s[_i + 1]);
                _i += 2;
            }
            else if (c == '$' && _i + 1 < _s.Length && _s[_i + 1] == '(' && depth < MaxDepth)
            {
                _i += 2;
                ParseList(')', depth + 1);
            }
            else if (c == '`' && depth < MaxDepth)
            {
                _i++;
                ParseList('`', depth + 1);
            }
            else
            {
                word.Append(c);
                _i++;
            }
        }
    }

    /// <summary>"-rf" style short-option cluster (single dash, not "--", not a lone "-").</summary>
    public static bool IsShortCluster(string word) => word.Length > 1 && word[0] == '-' && word[1] != '-';

    public static bool ShortClusterHasAny(string word, string letters) =>
        IsShortCluster(word) && word.AsSpan(1).IndexOfAny(letters) >= 0;

    /// <summary>Command name without directory ("/usr/bin/rm" → "rm").</summary>
    public static string CommandName(string word)
    {
        var slash = word.LastIndexOf('/');
        return slash >= 0 && slash < word.Length - 1 ? word[(slash + 1)..] : word;
    }
}

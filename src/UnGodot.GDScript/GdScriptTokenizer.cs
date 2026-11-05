using UnGodot.Variants;

namespace UnGodot.GDScript;

/// <summary>One token produced by the bytecode tokenizer.</summary>
public sealed class GdToken
{
    public GlobalToken Type { get; init; }

    public Variant Literal { get; init; } = Variant.Nil;

    public int StartLine { get; init; }

    public int EndLine { get; init; }

    public int StartColumn { get; init; }

    /// <summary>Built-in function index, for <see cref="GlobalToken.G_TK_BUILT_IN_FUNC"/>.</summary>
    public int FuncIndex { get; init; } = -1;
}

/// <summary>
/// Replays a GDScript 1.0 bytecode token stream as a source-like token stream: it resolves
/// identifiers, constants and built-in functions, and synthesises the INDENT/DEDENT tokens
/// that the compiler stored as indentation values on newline tokens.
/// </summary>
public sealed class GdScriptTokenizer
{
    private readonly GdScriptBytecode _bytecode;
    private readonly BytecodeRevision _revision;
    private readonly Dictionary<int, int> _columns = [];
    private int _token;
    private int _currentLine;
    private int _currentIndent;
    private int _pendingIndents;
    private bool _multilineMode;

    /// <summary>When true, newlines inside a line are swallowed instead of emitted.</summary>
    public bool MultilineMode
    {
        get => _multilineMode;
        set => _multilineMode = value;
    }

    public GdScriptTokenizer(GdScriptBytecode bytecode, BytecodeRevision revision)
    {
        _bytecode = bytecode;
        _revision = revision;

        if (revision.Version != bytecode.Version)
        {
            throw new GdScriptBytecodeException(
                $"bytecode version mismatch: file is v{bytecode.Version}, revision {revision.Rev} is v{revision.Version}");
        }

        // Godot 3.x did not store columns; derive them the way GDRE does so the token
        // positions stay useful for diagnostics.
        int previousColumn = 1;
        foreach (KeyValuePair<int, int> entry in bytecode.Lines)
        {
            int column = entry.Value >> 24;
            if (column == 0 && entry.Key < bytecode.Tokens.Length &&
                revision.ToGlobalToken(bytecode.Tokens[entry.Key]) == GlobalToken.G_TK_NEWLINE)
            {
                column = (int)(bytecode.Tokens[entry.Key] >> 8) + 1;
            }
            else if (column == 0)
            {
                column = previousColumn;
            }

            previousColumn = column;
            _columns[entry.Key] = column;
        }
    }

    public int TokenIndex => _token;

    public GdToken Scan()
    {
        if (_pendingIndents > 0)
        {
            _pendingIndents--;
            return new GdToken
            {
                Type = GlobalToken.G_TK_INDENT,
                StartLine = _currentLine,
                EndLine = _currentLine,
            };
        }

        if (_pendingIndents < 0)
        {
            _pendingIndents++;
            return new GdToken
            {
                Type = GlobalToken.G_TK_DEDENT,
                StartLine = _currentLine,
                EndLine = _currentLine,
            };
        }

        int line = GetLine();
        GlobalToken token = GetToken();
        if (token == GlobalToken.G_TK_EOF)
        {
            return new GdToken
            {
                Type = GlobalToken.G_TK_EOF,
                StartLine = line,
                StartColumn = GetColumn(),
            };
        }

        if (token == GlobalToken.G_TK_NEWLINE)
        {
            _currentLine = line;
            int previousIndent = _currentIndent;
            _currentIndent = GetLineIndent();

            if (!_multilineMode)
            {
                _pendingIndents += _currentIndent - previousIndent;
                Advance();
                return new GdToken
                {
                    Type = GlobalToken.G_TK_NEWLINE,
                    StartLine = _currentLine,
                    EndLine = _currentLine,
                };
            }

            while (token == GlobalToken.G_TK_NEWLINE)
            {
                Advance();
                line = GetLine();
                token = GetToken();
                if (token == GlobalToken.G_TK_NEWLINE)
                {
                    _currentLine = line;
                    _currentIndent = GetLineIndent();
                }
            }
        }

        Variant literal = Variant.Nil;
        int func = -1;
        switch (token)
        {
            case GlobalToken.G_TK_IDENTIFIER:
                literal = Variant.FromString(GetIdentifier());
                break;

            case GlobalToken.G_TK_CONSTANT:
                literal = GetConstant();
                break;

            case GlobalToken.G_TK_BUILT_IN_TYPE:
                func = GetOldTokenType();
                literal = Variant.FromString(VariantTextWriter.TypeName((VariantType)Math.Clamp(func, 0, 26)));
                break;

            case GlobalToken.G_TK_BUILT_IN_FUNC:
                func = GetBuiltInFunc();
                literal = Variant.FromString(_revision.GetFunctionName(func));
                break;

            case GlobalToken.G_TK_ERROR:
                literal = Variant.FromString(string.Empty);
                break;
        }

        var result = new GdToken
        {
            Type = token,
            Literal = literal,
            FuncIndex = func,
            StartLine = _currentLine,
            EndLine = line,
            StartColumn = GetColumn(),
        };

        _currentLine = line;
        if (token != GlobalToken.G_TK_EOF)
        {
            Advance();
        }

        return result;
    }

    private GlobalToken GetToken(int offset = 0)
    {
        int index = _token + offset;
        if (index < 0 || index >= _bytecode.Tokens.Length)
        {
            return GlobalToken.G_TK_EOF;
        }

        return _revision.ToGlobalToken(_bytecode.Tokens[index]);
    }

    private string GetIdentifier(int offset = 0)
    {
        int index = _token + offset;
        if (index < 0 || index >= _bytecode.Tokens.Length)
        {
            return string.Empty;
        }

        uint identifier = _bytecode.Tokens[index] >> 8;
        return identifier < _bytecode.Identifiers.Count ? _bytecode.Identifiers[(int)identifier] : string.Empty;
    }

    private int GetBuiltInFunc(int offset = 0)
    {
        int index = _token + offset;
        if (index < 0 || index >= _bytecode.Tokens.Length)
        {
            return _revision.FunctionNames.Count;
        }

        return (int)(_bytecode.Tokens[index] >> 8);
    }

    private int GetOldTokenType(int offset = 0)
    {
        int index = _token + offset;
        if (index < 0 || index >= _bytecode.Tokens.Length)
        {
            return -1;
        }

        return (int)(_bytecode.Tokens[index] >> 8);
    }

    private Variant GetConstant(int offset = 0)
    {
        int index = _token + offset;
        if (index < 0 || index >= _bytecode.Tokens.Length)
        {
            return Variant.Nil;
        }

        uint constant = _bytecode.Tokens[index] >> 8;
        return constant < _bytecode.Constants.Count ? _bytecode.Constants[(int)constant] : Variant.Nil;
    }

    private int GetLine(int offset = 0) => _bytecode.GetLine(_token + offset);

    private int GetLineIndent(int offset = 0)
    {
        int index = _token + offset;
        return index >= 0 && index < _bytecode.Tokens.Length ? (int)(_bytecode.Tokens[index] >> 8) : 0;
    }

    private int GetColumn(int offset = 0)
    {
        if (_columns.Count == 0)
        {
            return -1;
        }

        int index = _token + offset;
        int best = -1;
        bool found = false;
        foreach (KeyValuePair<int, int> entry in _columns)
        {
            if (entry.Key > index)
            {
                break;
            }

            best = entry.Value;
            found = true;
        }

        if (!found)
        {
            best = _columns.Values.First();
        }

        return best;
    }

    private void Advance()
    {
        if (_token + 1 >= _bytecode.Tokens.Length)
        {
            // Stay on the last token so a damaged stream still terminates as EOF.
            _token = _bytecode.Tokens.Length;
            return;
        }

        _token++;
    }
}
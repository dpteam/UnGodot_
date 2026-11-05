using System.Text;
using UnGodot.Variants;

namespace UnGodot.GDScript;

public sealed class DecompileOptions
{
    /// <summary>Spaces instead of tabs (GDRE: "Indent/type: Spaces").</summary>
    public bool UseSpaces { get; init; }

    public int TabSize { get; init; } = 4;
}

public sealed class DecompileResult
{
    public required string Source { get; init; }
    public required BytecodeRevision Revision { get; init; }
    public required IReadOnlyList<GdToken> Tokens { get; init; }
    public string? Error { get; init; }
}

public sealed class GdScriptDecompileException : Exception
{
    public GdScriptDecompileException(string message) : base(message)
    {
    }
}

/// <summary>
/// GDScript 1.0 (Godot 2/3) bytecode decompiler.
///
/// The GDScript bytecode is a token stream with source positions, so decompiling is
/// "re-printing the tokens with the original line/indentation structure" plus resolving
/// identifiers, constants and built-in function names. This is a direct port of
/// <c>GDScriptDecomp::decompile_buffer</c> from GDRE Tools (MIT), which is itself derived
/// from Godot's tokenizer.
/// </summary>
public static class GdScriptDecompiler
{
    public static DecompileResult Decompile(byte[] buffer, BytecodeRevision revision, DecompileOptions? options = null)
    {
        options ??= new DecompileOptions();
        GdScriptBytecode bytecode = GdScriptBytecode.Parse(buffer);
        return Decompile(bytecode, revision, options);
    }

    public static DecompileResult Decompile(GdScriptBytecode bytecode, BytecodeRevision revision, DecompileOptions? options = null)
    {
        options ??= new DecompileOptions();
        var tokenizer = new GdScriptTokenizer(bytecode, revision);

        var tokens = new List<GdToken>();
        while (true)
        {
            GdToken token = tokenizer.Scan();
            if (token.Type == GlobalToken.G_TK_EOF)
            {
                break;
            }

            tokens.Add(token);
        }

        string source = Print(tokens, bytecode, revision, options);
        return new DecompileResult
        {
            Source = source,
            Revision = revision,
            Tokens = tokens,
        };
    }

    private static string Print(
        List<GdToken> tokens,
        GdScriptBytecode bytecode,
        BytecodeRevision revision,
        DecompileOptions options)
    {
        bool useSpaces = options.UseSpaces;
        int tabSize = options.TabSize;
        int version = bytecode.Version;

        var output = new StringBuilder(Math.Max(256, tokens.Count * 4));
        string line = string.Empty;
        int indent = 0;
        GlobalToken prevToken = GlobalToken.G_TK_NEWLINE;
        int prevLine = 1;
        bool firstLine = true;

        void WriteCurrentLine()
        {
            if (useSpaces)
            {
                output.Append(' ', Math.Max(0, indent) * tabSize);
            }
            else
            {
                output.Append('\t', Math.Max(0, indent));
            }

            output.Append(line);
        }

        bool IsNewlineOrIndent(GlobalToken token) =>
            token is GlobalToken.G_TK_NEWLINE or GlobalToken.G_TK_DEDENT or GlobalToken.G_TK_INDENT;

        bool CheckNewLine(int index)
        {
            if (index < 0 || index >= tokens.Count)
            {
                return false;
            }

            int lineNumber = tokens[index].StartLine;
            if (lineNumber > prevLine && lineNumber != 0)
            {
                return true;
            }

            lineNumber = tokens[index].EndLine;
            return lineNumber != prevLine && lineNumber != 0;
        }

        void HandleNewline(int index, GlobalToken current)
        {
            int currentLine = tokens[index].EndLine;
            WriteCurrentLine();
            if (currentLine <= prevLine)
            {
                currentLine = prevLine + 1;
            }

            while (currentLine > prevLine)
            {
                if (current != GlobalToken.G_TK_NEWLINE && version < GdScriptBytecode.GdScript2Version)
                {
                    // Mid-statement line break: keep the logical line valid.
                    output.Append('\\');
                }

                output.Append('\n');
                prevLine++;
            }

            firstLine = false;
            line = string.Empty;
            prevToken = GlobalToken.G_TK_NEWLINE;
        }

        void EnsureSpace()
        {
            if (!line.EndsWith(' ') && !IsNewlineOrIndent(prevToken))
            {
                line += " ";
            }
        }

        void EnsureEndingSpace(int index, GlobalToken checkToken = GlobalToken.G_TK_NEWLINE)
        {
            if (line.EndsWith(' ') || index >= tokens.Count - 1)
            {
                return;
            }

            GdToken next = tokens[index + 1];
            if (IsNewlineOrIndent(next.Type) || CheckNewLine(index + 1))
            {
                return;
            }

            if (checkToken != GlobalToken.G_TK_NEWLINE && next.Type == checkToken)
            {
                return;
            }

            line += " ";
        }

        for (int i = 0; i < tokens.Count; i++)
        {
            GdToken token = tokens[i];
            GlobalToken current = token.Type;

            if (!IsNewlineOrIndent(current) && CheckNewLine(i))
            {
                if (current == GlobalToken.G_TK_CONSTANT && token.Literal.Type == VariantType.String)
                {
                    string escaped = MultilineEscape(token.Literal.AsString());
                    int newlines = escaped.Count(c => c == '\n');
                    int lineSpan = token.EndLine - token.StartLine;
                    if (newlines == lineSpan)
                    {
                        line += "\"\"\"" + escaped + "\"\"\"";
                        prevLine = token.EndLine;
                        continue;
                    }
                }

                HandleNewline(i, current);
            }

            switch (current)
            {
                case GlobalToken.G_TK_EMPTY:
                    break;

                case GlobalToken.G_TK_ANNOTATION:
                case GlobalToken.G_TK_IDENTIFIER:
                    line += token.Literal.AsString();
                    break;

                case GlobalToken.G_TK_CONSTANT:
                    line += GetConstantString(token.Literal, revision);
                    break;

                case GlobalToken.G_TK_SELF:
                    line += "self";
                    break;

                case GlobalToken.G_TK_BUILT_IN_TYPE:
                case GlobalToken.G_TK_BUILT_IN_FUNC:
                    line += token.Literal.AsString();
                    break;

                case GlobalToken.G_TK_OP_IN:
                    EnsureSpace();
                    line += "in ";
                    break;

                case GlobalToken.G_TK_OP_EQUAL:
                    EnsureSpace();
                    line += "== ";
                    break;

                case GlobalToken.G_TK_OP_NOT_EQUAL:
                    EnsureSpace();
                    line += "!= ";
                    break;

                case GlobalToken.G_TK_OP_LESS:
                    EnsureSpace();
                    line += "< ";
                    break;

                case GlobalToken.G_TK_OP_LESS_EQUAL:
                    EnsureSpace();
                    line += "<= ";
                    break;

                case GlobalToken.G_TK_OP_GREATER:
                    EnsureSpace();
                    line += "> ";
                    break;

                case GlobalToken.G_TK_OP_GREATER_EQUAL:
                    EnsureSpace();
                    line += ">= ";
                    break;

                case GlobalToken.G_TK_OP_AND:
                    EnsureSpace();
                    line += "and ";
                    break;

                case GlobalToken.G_TK_OP_OR:
                    EnsureSpace();
                    line += "or ";
                    break;

                case GlobalToken.G_TK_OP_NOT:
                    EnsureSpace();
                    line += "not ";
                    break;

                case GlobalToken.G_TK_OP_ADD:
                    EnsureSpace();
                    line += "+ ";
                    break;

                case GlobalToken.G_TK_OP_SUB:
                    EnsureSpace();
                    line += "- ";
                    break;

                case GlobalToken.G_TK_OP_MUL:
                    EnsureSpace();
                    line += "* ";
                    break;

                case GlobalToken.G_TK_OP_DIV:
                    EnsureSpace();
                    line += "/ ";
                    break;

                case GlobalToken.G_TK_OP_MOD:
                    EnsureSpace();
                    line += "%";
                    if (prevToken is GlobalToken.G_TK_CONSTANT or GlobalToken.G_TK_IDENTIFIER or GlobalToken.G_TK_PARENTHESIS_CLOSE)
                    {
                        EnsureEndingSpace(i);
                    }

                    break;

                case GlobalToken.G_TK_OP_SHIFT_LEFT:
                    EnsureSpace();
                    line += "<< ";
                    break;

                case GlobalToken.G_TK_OP_SHIFT_RIGHT:
                    EnsureSpace();
                    line += ">> ";
                    break;

                case GlobalToken.G_TK_OP_ASSIGN:
                    EnsureSpace();
                    line += "= ";
                    break;

                case GlobalToken.G_TK_OP_ASSIGN_ADD:
                    EnsureSpace();
                    line += "+= ";
                    break;

                case GlobalToken.G_TK_OP_ASSIGN_SUB:
                    EnsureSpace();
                    line += "-= ";
                    break;

                case GlobalToken.G_TK_OP_ASSIGN_MUL:
                    EnsureSpace();
                    line += "*= ";
                    break;

                case GlobalToken.G_TK_OP_ASSIGN_DIV:
                    EnsureSpace();
                    line += "/= ";
                    break;

                case GlobalToken.G_TK_OP_ASSIGN_MOD:
                    EnsureSpace();
                    line += "%= ";
                    break;

                case GlobalToken.G_TK_OP_ASSIGN_SHIFT_LEFT:
                    EnsureSpace();
                    line += "<<= ";
                    break;

                case GlobalToken.G_TK_OP_ASSIGN_SHIFT_RIGHT:
                    EnsureSpace();
                    line += ">>= ";
                    break;

                case GlobalToken.G_TK_OP_ASSIGN_BIT_AND:
                    EnsureSpace();
                    line += "&= ";
                    break;

                case GlobalToken.G_TK_OP_ASSIGN_BIT_OR:
                    EnsureSpace();
                    line += "|= ";
                    break;

                case GlobalToken.G_TK_OP_ASSIGN_BIT_XOR:
                    EnsureSpace();
                    line += "^= ";
                    break;

                case GlobalToken.G_TK_OP_BIT_AND:
                    EnsureSpace();
                    line += "& ";
                    break;

                case GlobalToken.G_TK_OP_BIT_OR:
                    EnsureSpace();
                    line += "| ";
                    break;

                case GlobalToken.G_TK_OP_BIT_XOR:
                    EnsureSpace();
                    line += "^ ";
                    break;

                case GlobalToken.G_TK_OP_BIT_INVERT:
                    EnsureSpace();
                    line += "~ ";
                    break;

                case GlobalToken.G_TK_CF_IF:
                    EnsureSpace();
                    line += "if ";
                    break;

                case GlobalToken.G_TK_CF_ELIF:
                    line += "elif ";
                    break;

                case GlobalToken.G_TK_CF_ELSE:
                    EnsureSpace();
                    line += "else";
                    EnsureEndingSpace(i, GlobalToken.G_TK_COLON);
                    break;

                case GlobalToken.G_TK_CF_FOR:
                    line += "for ";
                    break;

                case GlobalToken.G_TK_CF_WHILE:
                    line += "while ";
                    break;

                case GlobalToken.G_TK_CF_BREAK:
                    line += "break";
                    break;

                case GlobalToken.G_TK_CF_CONTINUE:
                    line += "continue";
                    break;

                case GlobalToken.G_TK_CF_PASS:
                    line += "pass";
                    break;

                case GlobalToken.G_TK_CF_RETURN:
                    line += "return";
                    EnsureEndingSpace(i);
                    break;

                case GlobalToken.G_TK_CF_MATCH:
                    line += "match";
                    EnsureEndingSpace(i);
                    break;

                case GlobalToken.G_TK_PR_FUNCTION:
                    EnsureSpace();
                    line += "func";
                    EnsureEndingSpace(i, GlobalToken.G_TK_PARENTHESIS_OPEN);
                    break;

                case GlobalToken.G_TK_PR_CLASS:
                    EnsureSpace();
                    line += "class ";
                    break;

                case GlobalToken.G_TK_PR_CLASS_NAME:
                    EnsureSpace();
                    line += "class_name ";
                    break;

                case GlobalToken.G_TK_PR_EXTENDS:
                    EnsureSpace();
                    line += "extends ";
                    break;

                case GlobalToken.G_TK_PR_IS:
                    EnsureSpace();
                    line += "is ";
                    break;

                case GlobalToken.G_TK_PR_ONREADY:
                    line += "onready ";
                    break;

                case GlobalToken.G_TK_PR_TOOL:
                    line += "tool";
                    EnsureEndingSpace(i);
                    break;

                case GlobalToken.G_TK_PR_STATIC:
                    line += "static ";
                    break;

                case GlobalToken.G_TK_PR_EXPORT:
                    line += "export ";
                    break;

                case GlobalToken.G_TK_PR_SETGET:
                    line += " setget ";
                    break;

                case GlobalToken.G_TK_PR_CONST:
                    line += "const ";
                    break;

                case GlobalToken.G_TK_PR_VAR:
                    EnsureSpace();
                    line += "var ";
                    break;

                case GlobalToken.G_TK_PR_AS:
                    EnsureSpace();
                    line += "as ";
                    break;

                case GlobalToken.G_TK_PR_VOID:
                    line += "void ";
                    break;

                case GlobalToken.G_TK_PR_ENUM:
                    line += "enum ";
                    break;

                case GlobalToken.G_TK_PR_PRELOAD:
                    line += "preload";
                    break;

                case GlobalToken.G_TK_PR_ASSERT:
                    line += "assert ";
                    break;

                case GlobalToken.G_TK_PR_YIELD:
                    line += "yield";
                    EnsureEndingSpace(i, GlobalToken.G_TK_PARENTHESIS_OPEN);
                    break;

                case GlobalToken.G_TK_PR_SIGNAL:
                    line += "signal ";
                    break;

                case GlobalToken.G_TK_PR_BREAKPOINT:
                    line += "breakpoint";
                    EnsureEndingSpace(i);
                    break;

                case GlobalToken.G_TK_PR_REMOTE:
                    line += "remote ";
                    break;

                case GlobalToken.G_TK_PR_SYNC:
                    line += "sync ";
                    break;

                case GlobalToken.G_TK_PR_MASTER:
                    line += "master ";
                    break;

                case GlobalToken.G_TK_PR_SLAVE:
                    line += "slave ";
                    break;

                case GlobalToken.G_TK_PR_PUPPET:
                    line += "puppet ";
                    break;

                case GlobalToken.G_TK_PR_REMOTESYNC:
                    line += "remotesync ";
                    break;

                case GlobalToken.G_TK_PR_MASTERSYNC:
                    line += "mastersync ";
                    break;

                case GlobalToken.G_TK_PR_PUPPETSYNC:
                    line += "puppetsync ";
                    break;

                case GlobalToken.G_TK_BRACKET_OPEN:
                    line += "[";
                    break;

                case GlobalToken.G_TK_BRACKET_CLOSE:
                    line += "]";
                    break;

                case GlobalToken.G_TK_CURLY_BRACKET_OPEN:
                    line += "{";
                    break;

                case GlobalToken.G_TK_CURLY_BRACKET_CLOSE:
                    line += "}";
                    break;

                case GlobalToken.G_TK_PARENTHESIS_OPEN:
                    line += "(";
                    break;

                case GlobalToken.G_TK_PARENTHESIS_CLOSE:
                    line += ")";
                    break;

                case GlobalToken.G_TK_COMMA:
                    line += ", ";
                    break;

                case GlobalToken.G_TK_SEMICOLON:
                    line += ";";
                    break;

                case GlobalToken.G_TK_PERIOD:
                    line += ".";
                    break;

                case GlobalToken.G_TK_QUESTION_MARK:
                    line += "?";
                    break;

                case GlobalToken.G_TK_COLON:
                    line += ":";
                    EnsureEndingSpace(i);
                    break;

                case GlobalToken.G_TK_DOLLAR:
                    line += "$";
                    break;

                case GlobalToken.G_TK_FORWARD_ARROW:
                    EnsureSpace();
                    line += "->";
                    EnsureEndingSpace(i);
                    break;

                case GlobalToken.G_TK_INDENT:
                    indent++;
                    break;

                case GlobalToken.G_TK_DEDENT:
                    indent--;
                    break;

                case GlobalToken.G_TK_NEWLINE:
                    HandleNewline(i, current);
                    break;

                case GlobalToken.G_TK_CONST_PI:
                    line += "PI";
                    break;

                case GlobalToken.G_TK_CONST_TAU:
                    line += "TAU";
                    break;

                case GlobalToken.G_TK_WILDCARD:
                    line += "_";
                    break;

                case GlobalToken.G_TK_CONST_INF:
                    line += "INF";
                    break;

                case GlobalToken.G_TK_CONST_NAN:
                    line += "NAN";
                    break;

                case GlobalToken.G_TK_PR_SLAVESYNC:
                    line += "slavesync ";
                    break;

                case GlobalToken.G_TK_CF_DO:
                    line += "do ";
                    break;

                case GlobalToken.G_TK_CF_CASE:
                    line += "case ";
                    break;

                case GlobalToken.G_TK_CF_SWITCH:
                    line += "switch ";
                    break;

                case GlobalToken.G_TK_AMPERSAND_AMPERSAND:
                    EnsureSpace();
                    line += "&& ";
                    break;

                case GlobalToken.G_TK_PIPE_PIPE:
                    EnsureSpace();
                    line += "|| ";
                    break;

                case GlobalToken.G_TK_BANG:
                    EnsureSpace();
                    line += "!";
                    break;

                case GlobalToken.G_TK_STAR_STAR:
                    EnsureSpace();
                    line += "** ";
                    break;

                case GlobalToken.G_TK_STAR_STAR_EQUAL:
                    EnsureSpace();
                    line += "**= ";
                    break;

                case GlobalToken.G_TK_CF_WHEN:
                    EnsureSpace();
                    line += "when ";
                    break;

                case GlobalToken.G_TK_PR_AWAIT:
                    EnsureSpace();
                    line += "await ";
                    break;

                case GlobalToken.G_TK_PR_NAMESPACE:
                    EnsureSpace();
                    line += "namespace ";
                    break;

                case GlobalToken.G_TK_PR_SUPER:
                    EnsureSpace();
                    line += "super";
                    EnsureEndingSpace(i, GlobalToken.G_TK_PERIOD);
                    break;

                case GlobalToken.G_TK_PR_TRAIT:
                    EnsureSpace();
                    line += "trait ";
                    break;

                case GlobalToken.G_TK_PERIOD_PERIOD:
                    line += "..";
                    break;

                case GlobalToken.G_TK_PERIOD_PERIOD_PERIOD:
                    line += "...";
                    break;

                case GlobalToken.G_TK_UNDERSCORE:
                    line += "_";
                    break;

                case GlobalToken.G_TK_BACKTICK:
                    line += "`";
                    break;

                case GlobalToken.G_TK_ABSTRACT:
                    line += "abstract ";
                    break;

                case GlobalToken.G_TK_ERROR:
                case GlobalToken.G_TK_EOF:
                case GlobalToken.G_TK_CURSOR:
                case GlobalToken.G_TK_VCS_CONFLICT_MARKER:
                    break;

                case GlobalToken.G_TK_MAX:
                default:
                    throw new GdScriptDecompileException($"invalid token {current} at index {i}");
            }

            prevToken = current;
        }

        if (line.Length > 0 ||
            (IsNewlineOrIndent(prevToken) && version < GdScriptBytecode.GdScript2Version && indent > 0))
        {
            WriteCurrentLine();
        }

        if (output.Length == 0 && (bytecode.Identifiers.Count > 0 || bytecode.Constants.Count > 0 || bytecode.Tokens.Length > 0))
        {
            throw new GdScriptDecompileException("the script produced no source (invalid token stream)");
        }

        _ = firstLine;
        return output.ToString();
    }

    private static string GetConstantString(Variant value, BytecodeRevision revision)
    {
        // GDScript 1.0 stores negative integers as `op_sub, constant`; writing them as
        // unsigned hex keeps them parseable and matches GDRE's output.
        if (revision.Version < GdScriptBytecode.GdScript2Version && value.Type == VariantType.Int && value.AsInt() < 0)
        {
            long number = value.AsInt();
            return number < int.MinValue
                ? "0x" + unchecked((ulong)number).ToString("X")
                : "0x" + unchecked((uint)number).ToString("X");
        }

        return VariantTextWriter.Write(value, revision.VariantMajor, GodotTextStyle.Script);
    }

    private static string MultilineEscape(string value)
    {
        // c_escape_multiline keeps newlines verbatim inside triple quotes.
        var builder = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\a':
                    builder.Append("\\a");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\v':
                    builder.Append("\\v");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }
}
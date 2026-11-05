namespace UnGodot.GDScript;

public enum BytecodeTestResult
{
    Pass,
    Fail,
    Corrupt,
}

/// <summary>
/// Structural validation of a token stream against a bytecode revision, ported from GDRE's
/// <c>GDScriptDecomp::_test_bytecode</c>.
///
/// The important part is the built-in call check: the number of arguments at the call site
/// must fit the function's signature for that revision. Revisions that share a bytecode
/// version (3.1.0 vs 3.1.1, 3.2 vs 3.5) differ mostly in their function tables, so this is
/// what tells them apart without an engine version hint.
/// </summary>
public static class BytecodeTester
{
    public static BytecodeTestResult Test(
        GdScriptBytecode bytecode,
        BytecodeRevision revision,
        out int tokenMax,
        out int functionMax,
        out string? error)
    {
        tokenMax = 0;
        functionMax = 0;
        error = null;

        uint[] tokens = bytecode.Tokens;
        int version = bytecode.Version;
        int functionCount = revision.FunctionNames.Count;

        if (version != revision.Version)
        {
            error = $"bytecode version {version} != revision version {revision.Version}";
            return BytecodeTestResult.Fail;
        }

        GlobalToken Token(int index) => revision.ToGlobalToken(tokens[index]);

        bool IsReservedWordUsable(int index)
        {
            bool previousPeriod = index > 0 && Token(index - 1) == GlobalToken.G_TK_PERIOD;
            bool previousFunc = index > 0 && Token(index - 1) == GlobalToken.G_TK_PR_FUNCTION;
            bool callLike = index + 1 < tokens.Length && Token(index + 1) == GlobalToken.G_TK_PARENTHESIS_OPEN;
            return previousPeriod || previousFunc || callLike;
        }

        for (int i = 0; i < tokens.Length; i++)
        {
            GlobalToken token = Token(i);
            tokenMax = Math.Max(tokenMax, (int)(tokens[i] & 0xFF));

            switch (token)
            {
                case GlobalToken.G_TK_MAX:
                case GlobalToken.G_TK_CURSOR:
                    error = $"invalid token {token} at {i}";
                    return BytecodeTestResult.Fail;

                case GlobalToken.G_TK_EOF:
                    if (i != tokens.Length - 1)
                    {
                        error = "EOF token is not the last token";
                        return BytecodeTestResult.Fail;
                    }

                    break;

                case GlobalToken.G_TK_PR_FUNCTION:
                    if (IsReservedWordUsable(i))
                    {
                        break;
                    }

                    if (i + 2 >= tokens.Length || Token(i + 2) != GlobalToken.G_TK_PARENTHESIS_OPEN)
                    {
                        error = $"malformed function declaration at {i}";
                        return BytecodeTestResult.Fail;
                    }

                    break;

                case GlobalToken.G_TK_PR_STATIC:
                    if (IsReservedWordUsable(i))
                    {
                        break;
                    }

                    if (i + 1 >= tokens.Length || Token(i + 1) != GlobalToken.G_TK_PR_FUNCTION)
                    {
                        error = $"static is not followed by a function at {i}";
                        return BytecodeTestResult.Fail;
                    }

                    break;

                case GlobalToken.G_TK_PR_ENUM:
                    if (IsReservedWordUsable(i))
                    {
                        break;
                    }

                    if (i + 1 >= tokens.Length ||
                        (Token(i + 1) != GlobalToken.G_TK_IDENTIFIER && Token(i + 1) != GlobalToken.G_TK_CURLY_BRACKET_OPEN))
                    {
                        error = $"enum declaration error at {i}";
                        return BytecodeTestResult.Fail;
                    }

                    break;

                case GlobalToken.G_TK_CF_PASS:
                    if (IsReservedWordUsable(i))
                    {
                        break;
                    }

                    if (version < GdScriptBytecode.GdScript2Version)
                    {
                        if (i + 1 < tokens.Length)
                        {
                            GlobalToken next = Token(i + 1);
                            if (next is not (GlobalToken.G_TK_NEWLINE or GlobalToken.G_TK_SEMICOLON or GlobalToken.G_TK_EOF))
                            {
                                error = $"pass is followed by {next} at {i}";
                                return BytecodeTestResult.Fail;
                            }
                        }
                    }

                    break;

                case GlobalToken.G_TK_IDENTIFIER:
                    if ((tokens[i] >> 8) >= bytecode.Identifiers.Count)
                    {
                        error = $"identifier index out of range at {i}";
                        return BytecodeTestResult.Fail;
                    }

                    break;

                case GlobalToken.G_TK_CONSTANT:
                    if ((tokens[i] >> 8) >= bytecode.Constants.Count)
                    {
                        error = $"constant index out of range at {i}";
                        return BytecodeTestResult.Fail;
                    }

                    break;

                case GlobalToken.G_TK_BUILT_IN_TYPE:
                    if ((tokens[i] >> 8) > 26)
                    {
                        error = $"variant type index out of range at {i}";
                        return BytecodeTestResult.Fail;
                    }

                    break;

                case GlobalToken.G_TK_BUILT_IN_FUNC:
                {
                    int functionId = (int)(tokens[i] >> 8);
                    functionMax = Math.Max(functionMax, functionId);
                    if (functionId >= functionCount)
                    {
                        error = $"built-in function index {functionId} >= {functionCount} at {i}";
                        return BytecodeTestResult.Fail;
                    }

                    if (!IsBuiltinCall(i, tokens.Length, Token))
                    {
                        break;
                    }

                    int argumentCount = CountArguments(tokens, i, revision, out bool malformed);
                    if (malformed)
                    {
                        error = $"cannot read the call arguments of '{revision.GetFunctionName(functionId)}' at {i}";
                        return BytecodeTestResult.Fail;
                    }

                    if (functionId < revision.ArgCounts.Count)
                    {
                        (int min, int max) = revision.ArgCounts[functionId];
                        if (argumentCount < min || argumentCount > max)
                        {
                            error = $"'{revision.GetFunctionName(functionId)}' called with {argumentCount} arguments at {i} (expected {min}..{max})";
                            return BytecodeTestResult.Fail;
                        }
                    }

                    break;
                }
            }
        }

        return BytecodeTestResult.Pass;
    }

    private static bool IsBuiltinCall(int position, int tokenCount, Func<int, GlobalToken> token)
    {
        if (position > 0 && token(position - 1) == GlobalToken.G_TK_PERIOD)
        {
            return false;
        }

        // Godot 3 emitted a built-in function token for any identifier sharing a built-in
        // name, so only a following "(" makes it an actual call.
        return position + 1 < tokenCount && token(position + 1) == GlobalToken.G_TK_PARENTHESIS_OPEN;
    }

    /// <summary>Counts the arguments of a call whose opening parenthesis is at <paramref name="position"/> + 1.</summary>
    private static int CountArguments(uint[] tokens, int position, BytecodeRevision revision, out bool malformed)
    {
        malformed = false;
        int depth = 0;
        int arguments = 0;
        bool hasContent = false;

        for (int i = position + 2; i < tokens.Length; i++)
        {
            GlobalToken token = revision.ToGlobalToken(tokens[i]);
            switch (token)
            {
                case GlobalToken.G_TK_BRACKET_OPEN:
                case GlobalToken.G_TK_CURLY_BRACKET_OPEN:
                case GlobalToken.G_TK_PARENTHESIS_OPEN:
                    depth++;
                    hasContent = true;
                    break;

                case GlobalToken.G_TK_BRACKET_CLOSE:
                case GlobalToken.G_TK_CURLY_BRACKET_CLOSE:
                case GlobalToken.G_TK_PARENTHESIS_CLOSE:
                    depth--;
                    break;

                case GlobalToken.G_TK_COMMA:
                    if (depth == 0)
                    {
                        arguments++;
                    }

                    break;

                case GlobalToken.G_TK_NEWLINE:
                case GlobalToken.G_TK_INDENT:
                case GlobalToken.G_TK_DEDENT:
                case GlobalToken.G_TK_CURSOR:
                    break;

                default:
                    hasContent = true;
                    break;
            }

            if (depth < 0)
            {
                return hasContent ? arguments + 1 : 0;
            }
        }

        malformed = true;
        return -1;
    }
}
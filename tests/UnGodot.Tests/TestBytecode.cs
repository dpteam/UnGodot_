using System.Buffers.Binary;
using System.Text;
using UnGodot.GDScript;

namespace UnGodot.Tests;
    /// <summary>Builds a GDScript 1.0 <c>GDSC</c> container, token by token.</summary>
    internal sealed class ScriptBuilder
    {
        private readonly BytecodeRevision _revision;
        private readonly List<string> _identifiers = [];
        private readonly List<byte[]> _constants = [];
        private readonly List<(int Token, int Line)> _lines = [];
        private readonly List<uint> _tokens = [];

        public ScriptBuilder(BytecodeRevision revision)
        {
            _revision = revision;
        }

        public int Identifier(string name)
        {
            _identifiers.Add(name);
            return _identifiers.Count - 1;
        }

        public int ConstantInt(long value)
        {
            byte[] bytes = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), (int)value);
            _constants.Add(bytes);
            return _constants.Count - 1;
        }

        public int ConstantReal(double value)
        {
            byte[] bytes = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, 3);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), (float)value);
            _constants.Add(bytes);
            return _constants.Count - 1;
        }

        public int ConstantString(string value)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            int padded = (utf8.Length + 3) & ~3;
            byte[] bytes = new byte[8 + padded];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, 4);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), utf8.Length);
            utf8.CopyTo(bytes, 8);
            _constants.Add(bytes);
            return _constants.Count - 1;
        }

        public ScriptBuilder Line(int line)
        {
            _lines.Add((_tokens.Count, line));
            return this;
        }

        public ScriptBuilder Token(GlobalToken token, int payload = 0, int? line = null)
        {
            if (line is not null)
            {
                Line(line.Value);
            }

            int local = ((IReadOnlyList<GlobalToken>)_revision.TokenMap).ToList().IndexOf(token);
            if (local < 0)
            {
                throw new InvalidOperationException($"{token} is not used by {_revision.Rev}");
            }

            _tokens.Add((uint)(local | (payload << 8)));
            return this;
        }

        public ScriptBuilder Newline(int indent = 0)
        {
            return Token(GlobalToken.G_TK_NEWLINE, indent);
        }

        public byte[] Build()
        {
            var output = new List<byte>();
            output.AddRange("GDSC"u8.ToArray());
            output.AddRange(BitConverter.GetBytes((uint)_revision.Version));
            output.AddRange(BitConverter.GetBytes((uint)_identifiers.Count));
            output.AddRange(BitConverter.GetBytes((uint)_constants.Count));
            output.AddRange(BitConverter.GetBytes((uint)_lines.Count));
            output.AddRange(BitConverter.GetBytes((uint)_tokens.Count));

            foreach (string identifier in _identifiers)
            {
                byte[] raw = Encoding.UTF8.GetBytes(identifier);
                var encoded = new byte[raw.Length + 1];
                for (int i = 0; i < raw.Length; i++)
                {
                    encoded[i] = (byte)(raw[i] ^ 0xB6);
                }

                encoded[^1] = 0xB6; // the NUL terminator, also XORed
                output.AddRange(BitConverter.GetBytes((uint)encoded.Length));
                output.AddRange(encoded);
            }

            foreach (byte[] constant in _constants)
            {
                output.AddRange(constant);
            }

            foreach ((int token, int line) in _lines)
            {
                output.AddRange(BitConverter.GetBytes((uint)token));
                output.AddRange(BitConverter.GetBytes((uint)line));
            }

            foreach (uint token in _tokens)
            {
                if ((token & 0xFFFFFF80) == 0)
                {
                    output.Add((byte)token);
                }
                else
                {
                    output.AddRange(BitConverter.GetBytes(token | 0x80));
                }
            }

            return output.ToArray();
        }
    }


/// <summary>Handy bytecode fixtures built with the real revision tables.</summary>
internal static class TestBytecode
{
    public static readonly BytecodeRevision Revision311 =
        BytecodeVersionDatabase.ForRev("514a3fb") ?? throw new InvalidOperationException("missing 3.1.1 revision");

    /// <summary><c>extends Node</c> + <c>func _ready(): print("hi")</c>, built token by token.</summary>
    public static byte[] SimpleScript()
    {
        var builder = new ScriptBuilder(Revision311);
        int node = builder.Identifier("Node");
        int ready = builder.Identifier("_ready");
        int hi = builder.ConstantString("hi");

        builder
            .Line(1)
            .Token(GlobalToken.G_TK_PR_EXTENDS)
            .Token(GlobalToken.G_TK_IDENTIFIER, node)
            .Newline()
            .Line(2)
            .Token(GlobalToken.G_TK_PR_FUNCTION)
            .Token(GlobalToken.G_TK_IDENTIFIER, ready)
            .Token(GlobalToken.G_TK_PARENTHESIS_OPEN)
            .Token(GlobalToken.G_TK_PARENTHESIS_CLOSE)
            .Token(GlobalToken.G_TK_COLON)
            .Newline(1)
            .Line(3)
            .Token(GlobalToken.G_TK_BUILT_IN_FUNC, Revision311.FunctionNames.ToList().IndexOf("print"))
            .Token(GlobalToken.G_TK_PARENTHESIS_OPEN)
            .Token(GlobalToken.G_TK_CONSTANT, hi)
            .Token(GlobalToken.G_TK_PARENTHESIS_CLOSE)
            .Newline()
            .Token(GlobalToken.G_TK_EOF);

        return builder.Build();
    }
}
